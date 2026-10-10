using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The true weather moving on its own (Design Document §10.6): every five minutes the wind wanders a few degrees and a
/// little faster or slower around the prevailing conditions. A scenario setting the weather (a front coming through)
/// sets new prevailing conditions to wander around. Command gets a met-service observation every 30 minutes,
/// rounded and about ten minutes old, so it is always slightly behind the truth.
/// </summary>
public sealed class WeatherSystem(GeoPoint? station = null, int seed = 2026) : ISimulationSystem
{
    public static readonly TimeSpan DriftInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ObservationInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ObservationLag = TimeSpan.FromMinutes(10);

    /// <summary>Furthest the wind wanders from the prevailing direction, degrees.</summary>
    public const double MaxVeerDegrees = 25;

    private readonly GeoPoint _station = station ?? new GeoPoint(53.3498, -6.2603);
    private readonly Random _random = new(seed);
    private readonly List<(DateTimeOffset At, WorldWeather Weather)> _history = [];
    private WorldWeather? _lastSet;
    private WorldWeather? _prevailing;
    private DateTimeOffset? _lastDrift;
    private DateTimeOffset? _start;
    private DateTimeOffset? _lastObservation;

    public int Order => 0;

    public void Update(SimulationContext context)
    {
        var world = context.World;
        _start ??= context.SimTime;

        // Someone else (the scenario) changed the weather: that is the new prevailing condition.
        if (world.Weather != _lastSet)
        {
            _prevailing = world.Weather;
            _lastSet = world.Weather;
        }

        if (_lastDrift is null || context.SimTime - _lastDrift >= DriftInterval)
        {
            _lastDrift = context.SimTime;
            Drift(context);
            _history.Add((context.SimTime, world.Weather));
            _history.RemoveAll(h => context.SimTime - h.At > ObservationLag + DriftInterval * 2);
        }

        if (context.SimTime - _start >= ObservationInterval
            && (_lastObservation is null || context.SimTime - _lastObservation >= ObservationInterval))
        {
            _lastObservation = context.SimTime;
            Observe(context);
        }
    }

    private void Drift(SimulationContext context)
    {
        var prevailing = _prevailing!;
        var current = context.World.Weather;

        var direction = current.WindFromDegrees + Gaussian() * 8;
        var offset = ((direction - prevailing.WindFromDegrees + 540) % 360) - 180;
        direction = (prevailing.WindFromDegrees + Math.Clamp(offset, -MaxVeerDegrees, MaxVeerDegrees) + 360) % 360;

        var speed = Math.Clamp(current.WindSpeedMps * (1 + Gaussian() * 0.1),
            prevailing.WindSpeedMps * 0.7, prevailing.WindSpeedMps * 1.3);

        var shift = Math.Abs(((direction - current.WindFromDegrees + 540) % 360) - 180);
        if (shift < 5 && Math.Abs(speed - current.WindSpeedMps) < 0.5) return;

        var next = current with { WindFromDegrees = Math.Round(direction), WindSpeedMps = Math.Round(speed, 1) };
        context.World.Weather = next;
        _lastSet = next;
        context.EmitTruth(new WeatherChanged(next.WindFromDegrees, next.WindSpeedMps, next.TemperatureC, next.RelativeHumidity));
    }

    /// <summary>A met observation: the weather of ten minutes ago, rounded as a forecaster would give it.</summary>
    private void Observe(SimulationContext context)
    {
        var observed = _history.LastOrDefault(h => h.At <= context.SimTime - ObservationLag).Weather
                       ?? _history.FirstOrDefault().Weather ?? context.World.Weather;
        context.EmitPerceived(new WeatherObserved("Met service", _station,
            Math.Round(observed.WindFromDegrees / 10) * 10 % 360, Math.Round(observed.WindSpeedMps * 2) / 2,
            Math.Round(observed.TemperatureC), Math.Round(observed.RelativeHumidity, 2)), EventSources.Engine);
    }

    /// <summary>Standard normal (Box–Muller).</summary>
    private double Gaussian()
    {
        var u1 = 1 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
