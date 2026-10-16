using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Services;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Real weather (Phase 19). When the loaded scenario asks for live weather (or <c>Simulation:LiveWeather</c> is set) it
/// fetches the conditions where the scenario happens, and again every <see cref="RefreshEvery"/>; for historical weather,
/// the conditions at the scenario's date and hour, once. The fetch runs off the tick: the reading is applied (truth) and the
/// met service reports it (perceived) on the first tick after it arrives. If the service can't be reached the scenario's
/// own weather stands, and the instructor sees why in the log.
/// </summary>
public sealed class RealWeatherSystem(IWeatherFeed feed, bool live = false) : ISimulationSystem
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

    private Task<WeatherReading?>? _pending;
    private DateTimeOffset? _next;
    private bool _historicalDone;
    private WeatherSource? _source;

    public int Order => -8; // before weather and hazards read the wind

    public void Update(SimulationContext context)
    {
        var training = context.World.Training;
        var source = training.Weather?.Source ?? (live ? WeatherSource.Live : WeatherSource.Fixed);
        if (source != _source)
        {
            (_source, _next, _historicalDone, _pending) = (source, null, false, null);
        }
        if (source == WeatherSource.Fixed) return;

        if (_pending is { IsCompleted: true } done)
        {
            _pending = null;
            var reading = done.IsCompletedSuccessfully ? done.Result : null;
            if (reading is null)
                context.EmitTruth(new RandomEventOccurred("Weather", "weather service unreachable: the scenario's weather stands"));
            else
            {
                context.EmitTruth(new WeatherChanged(reading.WindFromDegrees, reading.WindSpeedMps, reading.TemperatureC, reading.RelativeHumidity));
                context.EmitPerceived(new WeatherObserved("Met Éireann", Systems.RandomEventSystem.Near(context.World), reading.WindFromDegrees, reading.WindSpeedMps,
                    reading.TemperatureC, reading.RelativeHumidity), EventSources.Comms);
            }
        }
        if (_pending is not null) return;

        var at = Systems.RandomEventSystem.Near(context.World);
        if (source == WeatherSource.Historical)
        {
            if (_historicalDone || training.Weather?.HistoricalAt is not { } when) return;
            _historicalDone = true;
            _pending = feed.HistoricalAsync(at, when);
            return;
        }
        if (_next is { } next && context.SimTime < next) return;
        _next = context.SimTime + RefreshEvery;
        _pending = feed.CurrentAsync(at);
    }
}
