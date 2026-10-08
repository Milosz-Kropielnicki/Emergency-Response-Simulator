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

    /// <summary>
    /// Where command has said each (perceived) incident is. Units drive to the reported location,
    /// which may not be where the real incident is.
    /// </summary>
    public Dictionary<Guid, GeoPoint> ReportedIncidentLocations { get; } = [];
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
                Units[e.UnitId] = new WorldUnit
                {
                    Id = e.UnitId, Callsign = e.Callsign, Type = e.Type, Location = e.Location, Home = e.Location,
                };
                break;

            case IncidentCreated e:
                ReportedIncidentLocations[e.IncidentId] = e.Location;
                break;

            case UnitDispatched e when Units.TryGetValue(e.UnitId, out var unit):
                unit.OrderedIncidentId = e.IncidentId;
                unit.Phase = ResponsePhase.TurningOut;
                unit.PhaseStartedAt = simEvent.SimTime;
                break;

            case UnitDispatchCancelled e when Units.TryGetValue(e.UnitId, out var unit):
                unit.OrderedIncidentId = null;
                unit.Phase = ResponsePhase.Idle;
                unit.SpeedKph = 0;
                break;

            // Status changes made by command (e.g. "Transporting", "Available") end the automatic response.
            case UnitStatusChanged e when Units.TryGetValue(e.UnitId, out var unit)
                                          && e.Status is UnitStatus.Available or UnitStatus.Transporting
                                              or UnitStatus.OutOfService or UnitStatus.Cancelled:
                unit.Phase = ResponsePhase.Idle;
                unit.SpeedKph = 0;
                if (e.Status != UnitStatus.Transporting)
                    unit.OrderedIncidentId = null;
                break;

            case UnitRadioFailed e when Units.TryGetValue(e.UnitId, out var unit):
                unit.RadioFailed = e.Failed;
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
    public UnitType Type { get; init; }
    public GeoPoint Location { get; set; }
    public GeoPoint Home { get; init; }
    public double SpeedKph { get; set; }
    public double Heading { get; set; }

    /// <summary>The perceived incident this unit has been ordered to.</summary>
    public Guid? OrderedIncidentId { get; set; }

    public bool BrokenDown { get; set; }

    /// <summary>The unit keeps working, but nothing it says reaches command.</summary>
    public bool RadioFailed { get; set; }

    public ResponsePhase Phase { get; set; }
    public DateTimeOffset PhaseStartedAt { get; set; }

    /// <summary>Where the current journey started and is heading, and how long it takes.</summary>
    public GeoPoint TravelFrom { get; set; }
    public GeoPoint TravelTo { get; set; }
    public TimeSpan TravelTime { get; set; }
    public DateTimeOffset LastFixAt { get; set; }
}

/// <summary>What a responding unit is actually doing, advanced by <c>UnitResponseSystem</c>.</summary>
public enum ResponsePhase
{
    Idle,
    TurningOut,
    Travelling,
    OnScene,
    Operating,
}

public sealed record WorldWeather(double WindFromDegrees, double WindSpeedMps, double TemperatureC, double RelativeHumidity);
