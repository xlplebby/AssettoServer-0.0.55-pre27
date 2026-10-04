using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AssettoServer.Network.Tcp;
using AssettoServer.Shared.Network.Packets.Outgoing;
using AssettoServer.Shared.Weather;
using NodaTime;

namespace AssettoServer.Server.Weather.Implementation;

/// <summary>Per-client time-of-day preference stored by ClubhousePlugin.</summary>
public record ClientTimePreference(int StartTod, int Multiplier, long ReferenceTimestamp);

/// <summary>
/// Per-client weather override state. Thread-safe fields only — tick is called
/// from the weather broadcast loop (one goroutine at a time per client).
/// </summary>
public class ClientWeatherState
{
    // ── configuration (written by packet handler, read by tick) ─────────────
    public int   Mode;            // 0=off 1=single 2=cycle
    public int[] Types = [];      // weather type IDs, length 1..8
    public int   MinDuration;     // seconds
    public int   MaxDuration;
    public int   MinTransition;   // seconds
    public int   MaxTransition;

    // ── runtime state ────────────────────────────────────────────────────────
    public int    CurrentIdx;     // index into Types
    public int    NextIdx;        // index being faded to  
    public float  TransitionValue;// 0..1 on the fade to NextIdx weather
    public long   PhaseEndWall;   // wall-clock unix second when current phase ends
    public bool   InTransition;   // true = currently fading
    public long   TransitionEnd;  // wall-clock unix second when transition completes

    private static readonly Random _rng = new();

    /// <summary>Advance cycler state. Call once per second from SendWeather loop.</summary>
    public void Tick()
    {
        if (Mode != 2 || Types.Length < 2) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!InTransition)
        {
            if (now >= PhaseEndWall)
            {
                // Start a transition to the next weather
                InTransition  = true;
                NextIdx       = (CurrentIdx + 1) % Types.Length;
                int transSec  = MinTransition + _rng.Next(MaxTransition - MinTransition + 1);
                TransitionEnd = now + transSec;
                TransitionValue = 0f;
            }
        }
        else
        {
            long transDuration = TransitionEnd - (PhaseEndWall + (long)(MaxTransition));
            // Compute progress linearly
            long totalTrans = TransitionEnd - (TransitionEnd - (TransitionEnd - now + (TransitionEnd - now)));
            // Simpler: just lerp by wall clock
            long transStart = PhaseEndWall; // transition started at phaseEnd
            long elapsed    = now - transStart;
            long duration   = TransitionEnd - transStart;
            TransitionValue = duration <= 0 ? 1f : Math.Clamp((float)elapsed / duration, 0f, 1f);

            if (now >= TransitionEnd)
            {
                // Transition complete — advance to next weather, start new hold phase
                CurrentIdx      = NextIdx;
                TransitionValue = 0f;
                InTransition    = false;
                int holdSec     = MinDuration + _rng.Next(MaxDuration - MinDuration + 1);
                PhaseEndWall    = now + holdSec;
            }
        }
    }

    /// <summary>Call when the state is first configured or reset.</summary>
    public void Initialise()
    {
        CurrentIdx      = 0;
        NextIdx         = 0;
        TransitionValue = 0f;
        InTransition    = false;
        long now        = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int holdSec     = MinDuration + _rng.Next(Math.Max(1, MaxDuration - MinDuration + 1));
        PhaseEndWall    = now + holdSec;
    }
}

public class WeatherFxV1Implementation : IWeatherImplementation
{
    private readonly EntryCarManager _entryCarManager;

    public static readonly ConcurrentDictionary<string, ClientTimePreference>  ClientTimeOffsets   = new();
    public static readonly ConcurrentDictionary<string, ClientWeatherState>    ClientWeatherStates = new();
    public static readonly ConcurrentDictionary<string, (float Wetness, float Puddles)> ClientSurfaceStates = new();

    // Rain values keyed by WeatherFxType int, sourced from DefaultWeatherTypeProvider
    private static readonly Dictionary<int, (float Intensity, float Wetness, float Water)> RainByType = new()
    {
        { (int)WeatherFxType.LightThunderstorm,  (0.10f, 0.00f, 0.00f) },
        { (int)WeatherFxType.Thunderstorm,        (0.20f, 0.00f, 0.00f) },
        { (int)WeatherFxType.HeavyThunderstorm,   (0.40f, 0.00f, 0.00f) },
        { (int)WeatherFxType.LightDrizzle,        (0.05f, 0.05f, 0.00f) },
        { (int)WeatherFxType.Drizzle,             (0.15f, 0.10f, 0.05f) },
        { (int)WeatherFxType.HeavyDrizzle,        (0.25f, 0.20f, 0.10f) },
        { (int)WeatherFxType.LightRain,           (0.30f, 0.30f, 0.10f) },
        { (int)WeatherFxType.Rain,                (0.60f, 0.60f, 0.30f) },
        { (int)WeatherFxType.HeavyRain,           (1.00f, 1.00f, 0.50f) },
        { (int)WeatherFxType.LightSleet,          (0.20f, 0.30f, 0.10f) },
        { (int)WeatherFxType.Sleet,               (0.50f, 0.50f, 0.20f) },
        { (int)WeatherFxType.HeavySleet,          (0.80f, 0.70f, 0.30f) },
        { (int)WeatherFxType.LightSnow,           (0.00f, 0.20f, 0.05f) },
        { (int)WeatherFxType.Snow,                (0.00f, 0.30f, 0.10f) },
        { (int)WeatherFxType.HeavySnow,           (0.00f, 0.40f, 0.15f) },
        { (int)WeatherFxType.Fog,                 (0.00f, 0.20f, 0.00f) },
        { (int)WeatherFxType.Mist,                (0.00f, 0.10f, 0.00f) },
        { (int)WeatherFxType.Hail,                (0.50f, 0.50f, 0.10f) },
        { (int)WeatherFxType.Tornado,             (0.30f, 0.00f, 0.00f) },
        { (int)WeatherFxType.Hurricane,           (0.50f, 0.00f, 0.00f) },
    };

    public WeatherFxV1Implementation(EntryCarManager entryCarManager, CSPFeatureManager cspFeatureManager)
    {
        _entryCarManager = entryCarManager;
        cspFeatureManager.Add(new CSPFeature { Name = "WEATHERFX_V1", Mandatory = true });
    }

    public void SendWeather(WeatherData weather, ZonedDateTime dateTime, ACTcpClient? client = null)
    {
        long baseTimestamp = dateTime.ToInstant().ToUnixTimeSeconds();
        int  serverTod     = dateTime.Hour * 3600 + dateTime.Minute * 60 + dateTime.Second;
        var newWeather = new CSPWeatherUpdate
        {
            UnixTimestamp       = (ulong) baseTimestamp,
            WeatherType         = (byte) weather.Type.WeatherFxType,
            UpcomingWeatherType = (byte) weather.UpcomingType.WeatherFxType,
            TransitionValue     = weather.TransitionValue,
            TemperatureAmbient  = (Half) weather.TemperatureAmbient,
            TemperatureRoad     = (Half) weather.TemperatureRoad,
            TrackGrip           = (Half) weather.TrackGrip,
            WindDirectionDeg    = (Half) weather.WindDirection,
            WindSpeed           = (Half) weather.WindSpeed,
            Humidity            = (Half) weather.Humidity,
            Pressure            = (Half) weather.Pressure,
            RainIntensity       = (Half) weather.RainIntensity,
            RainWetness         = (Half) weather.RainWetness,
            RainWater           = (Half) weather.RainWater
        };

        if (client != null)
        {
            var custom = ApplyClientOverrides(newWeather, baseTimestamp, serverTod, client.Guid.ToString());
            client.SendPacketUdp(in custom);
            return;
        }

        bool anyTime    = !ClientTimeOffsets.IsEmpty;
        bool anyWeather = !ClientWeatherStates.IsEmpty;
        bool anySurface = !ClientSurfaceStates.IsEmpty;
        if (!anyTime && !anyWeather && !anySurface)
        {
            _entryCarManager.BroadcastPacketUdp(in newWeather);
            return;
        }

        foreach (var entryCar in _entryCarManager.EntryCars)
        {
            var c = entryCar?.Client;
            if (c?.HasSentFirstUpdate == true)
            {
                var custom = ApplyClientOverrides(newWeather, baseTimestamp, serverTod, c.Guid.ToString());
                c.SendPacketUdp(in custom);
            }
        }
    }

    private static CSPWeatherUpdate ApplyClientOverrides(
        CSPWeatherUpdate pkt, long baseTimestamp, int serverTod, string guid)
    {
        // ── Weather override ─────────────────────────────────────────────────
        if (ClientWeatherStates.TryGetValue(guid, out var wx) && wx.Mode > 0 && wx.Types.Length > 0)
        {
            wx.Tick();

            static (float I, float W, float Wt) GetRain(int typeId) =>
                RainByType.TryGetValue(typeId, out var r) ? r : (0f, 0f, 0f);

            if (wx.Mode == 1 || wx.Types.Length == 1)
            {
                pkt.WeatherType         = (byte) wx.Types[0];
                pkt.UpcomingWeatherType = (byte) wx.Types[0];
                pkt.TransitionValue     = 0;
                var (i, w, wt)          = GetRain(wx.Types[0]);
                pkt.RainIntensity       = (Half) i;
                pkt.RainWetness         = (Half) w;
                pkt.RainWater           = (Half) wt;
            }
            else if (wx.InTransition)
            {
                pkt.WeatherType         = (byte) wx.Types[wx.CurrentIdx];
                pkt.UpcomingWeatherType = (byte) wx.Types[wx.NextIdx];
                pkt.TransitionValue     = (ushort)(wx.TransitionValue * ushort.MaxValue);
                var (i0, w0, wt0)       = GetRain(wx.Types[wx.CurrentIdx]);
                var (i1, w1, wt1)       = GetRain(wx.Types[wx.NextIdx]);
                float t                 = wx.TransitionValue;
                pkt.RainIntensity       = (Half)(i0  + (i1  - i0)  * t);
                pkt.RainWetness         = (Half)(w0  + (w1  - w0)  * t);
                pkt.RainWater           = (Half)(wt0 + (wt1 - wt0) * t);
            }
            else
            {
                pkt.WeatherType         = (byte) wx.Types[wx.CurrentIdx];
                pkt.UpcomingWeatherType = (byte) wx.Types[wx.CurrentIdx];
                pkt.TransitionValue     = 0;
                var (i, w, wt)          = GetRain(wx.Types[wx.CurrentIdx]);
                pkt.RainIntensity       = (Half) i;
                pkt.RainWetness         = (Half) w;
                pkt.RainWater           = (Half) wt;
            }
        }

        // ── Time override ────────────────────────────────────────────────────
        if (ClientTimeOffsets.TryGetValue(guid, out var timePref))
            pkt.UnixTimestamp = ApplyTimePreference(baseTimestamp, serverTod, timePref);
        // ── Surface override (wetness/puddles, raises weather-derived values via Math.Max) ──
        if (ClientSurfaceStates.TryGetValue(guid, out var surf))
        {
            pkt.RainWetness = (Half)Math.Max((float)pkt.RainWetness, surf.Wetness);
            pkt.RainWater   = (Half)Math.Max((float)pkt.RainWater,   surf.Puddles);
        }
        return pkt;
    }

    private static ulong ApplyTimePreference(long baseTimestamp, int serverTod, ClientTimePreference pref)
    {
        long desiredTod;
        if (pref.Multiplier <= 0)
        {
            desiredTod = pref.StartTod;
        }
        else
        {
            long elapsed = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - pref.ReferenceTimestamp);
            desiredTod = ((pref.StartTod + elapsed * pref.Multiplier) % 86400L + 86400L) % 86400L;
        }
        int offs = (int)(desiredTod - serverTod);
        if (offs >  43200) offs -= 86400;
        if (offs < -43200) offs += 86400;
        return (ulong)(baseTimestamp + offs);
    }
}
