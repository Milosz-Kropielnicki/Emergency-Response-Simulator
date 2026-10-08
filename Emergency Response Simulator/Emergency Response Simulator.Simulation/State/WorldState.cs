using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// The true state of the world (Design Document §10.1): where the fire really is, how many people are
/// really hurt, which way the wind really blows. Only the engine, its systems, the instructor and the
/// AAR may read it; the trainee only ever sees <see cref="PerceivedState"/>.
/// </summary>
/// <remarks>
/// Discrete changes arrive as events through <see cref="Apply"/>. Continuous processes (movement,
/// fire growth) may be advanced directly by simulation systems each tick, emitting a truth event
/// when something meaningful changes. Not thread-safe: the engine accesses it from one tick at a time.
/// </remarks>
public sealed class WorldState
{
    public Dictionary<Guid, WorldIncident> Incidents { get; } = [];
    public Dictionary<Guid, WorldUnit> Units { get; } = [];
    public WorldWeather Weather { get; set; } = new(WindFromDegrees: 270, WindSpeedMps: 4, TemperatureC: 14, RelativeHumidity: 0.7);

    /// <summary>
    /// Applies an event to ground truth. Receives perceived events too, because orders such as
    /// a dispatch change what really happens even though they are made on perceived information.
    /// </summary>
    public void Apply(SimEvent simEvent)
    {
        switch (simEvent.Payload)
        {
            case WorldIncidentStarted e:
                Incidents[e.WorldIncidentId] = new WorldIncident
                {
                    Id = e.WorldIncidentId,
                    Type = e.Type,
                    Location = e.Location,
                    Severity = e.Severity,
                    ActualCasualties = e.ActualCasualties,
                    StartedAt = simEvent.SimTime,
                };
                break;

            case WorldIncidentChanged e when Incidents.TryGetValue(e.WorldIncidentId, out var incident):
                incident.Severity = e.Severity;
                incident.ActualCasualties = e.ActualCasualties;
                incident.Extinguished = e.Extinguished;
                break;

            case WeatherChanged e:
                Weather = new WorldWeather(e.WindFromDegrees, e.WindSpeedMps, e.TemperatureC, e.RelativeHumidity);
                break;

            case UnitRegistered e:
                Units[e.UnitId] = new WorldUnit { Id = e.UnitId, Callsign = e.Callsign, Location = e.Location };
                break;

            case UnitDispatched e when Units.TryGetValue(e.UnitId, out var unit):
                unit.OrderedIncidentId = e.IncidentId;
                break;

            case UnitDispatchCancelled e when Units.TryGetValue(e.UnitId, out var unit):
                unit.OrderedIncidentId = null;
                break;
        }
    }
}

public sealed class WorldIncident
{
    public Guid Id { get; init; }
    public IncidentType Type { get; init; }
    public GeoPoint Location { get; set; }

    /// <summary>0 (nothing) to 1 (catastrophic).</summary>
    public double Severity { get; set; }

    public int ActualCasualties { get; set; }
    public bool Extinguished { get; set; }
    public DateTimeOffset StartedAt { get; init; }
}

public sealed class WorldUnit
{
    public Guid Id { get; init; }
    public required string Callsign { get; init; }
    public GeoPoint Location { get; set; }
    public double SpeedKph { get; set; }
    public double Heading { get; set; }

    /// <summary>The perceived incident this unit has been ordered to.</summary>
    public Guid? OrderedIncidentId { get; set; }

    public bool BrokenDown { get; set; }
}

public sealed record WorldWeather(double WindFromDegrees, double WindSpeedMps, double TemperatureC, double RelativeHumidity);
