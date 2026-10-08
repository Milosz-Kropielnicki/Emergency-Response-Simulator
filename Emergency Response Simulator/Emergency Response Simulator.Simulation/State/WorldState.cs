using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

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

    /// <summary>
    /// Areas drivers have been told to keep out of: declared road closures, hot zones, fire exclusion zones.
    /// Declared by command, so every crew knows about them.
    /// </summary>
    public Dictionary<Guid, Geometry> DeclaredNoGoAreas { get; } = [];

    /// <summary>Real, unreported blockages. A crew only learns of one by reaching it.</summary>
    public Dictionary<Guid, (Geometry Line, string Description)> Obstructions { get; } = [];

    /// <summary>Bumped whenever <see cref="DeclaredNoGoAreas"/> changes, so moving crews re-plan.</summary>
    public int NoGoVersion { get; private set; }

    /// <summary>The priority command has given each incident (approvers judge requests on it).</summary>
    public Dictionary<Guid, IncidentPriority> ReportedIncidentPriorities { get; } = [];

    /// <summary>Each incident's ICS structure; the span-of-control penalty is applied from it.</summary>
    public Dictionary<Guid, IncidentCommand> Commands { get; } = [];

    public Dictionary<Guid, (string Name, AgencyType Type)> Agencies { get; } = [];

    // Things waiting for someone in the world to respond (see CommandResponseSystem).
    public List<PendingOrder> PendingOrders { get; } = [];
    public List<PendingRequest> PendingRequests { get; } = [];
    public List<PendingNotification> PendingNotifications { get; } = [];
    public List<PendingApprovalReply> PendingApprovalReplies { get; } = [];
    public Dictionary<Guid, string> ApprovalRequesters { get; } = [];

    /// <summary>Units committed to an incident (as ordered), for span-of-control counts.</summary>
    public IReadOnlyCollection<Guid> UnitsAt(Guid incidentId) =>
        Units.Values.Where(u => u.OrderedIncidentId == incidentId).Select(u => u.Id).ToList();
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
                ReportedIncidentPriorities[e.IncidentId] = e.Priority;
                Commands[e.IncidentId] = new IncidentCommand();
                break;

            case IncidentUpdated { Priority: { } priority } e:
                ReportedIncidentPriorities[e.IncidentId] = priority;
                break;

            case AgencyRegistered e:
                Agencies[e.AgencyId] = (e.Name, e.Type);
                break;

            case IncidentCommanderAssigned e when Commands.TryGetValue(e.IncidentId, out var c1):
                c1.Apply(e);
                break;
            case IcsPositionAssigned e when Commands.TryGetValue(e.IncidentId, out var c2):
                c2.Apply(e);
                break;
            case IcsGroupFormed e when Commands.TryGetValue(e.IncidentId, out var c3):
                c3.Apply(e);
                break;
            case IcsGroupDisbanded e when Commands.TryGetValue(e.IncidentId, out var c4):
                c4.Apply(e);
                break;
            case UnitAssignedToGroup e when Commands.TryGetValue(e.IncidentId, out var c5):
                c5.Apply(e);
                break;

            case OrderIssued e:
                PendingOrders.Add(new PendingOrder(e, simEvent.SimTime));
                break;
            case ResourceRequested e:
                PendingRequests.Add(new PendingRequest(e, simEvent.SimTime));
                break;
            case NotificationSent e:
                PendingNotifications.Add(new PendingNotification(e, simEvent.SimTime));
                break;
            case ApprovalRequested e:
                ApprovalRequesters[e.ApprovalId] = e.RequestedBy;
                break;
            case ApprovalDecided e when ApprovalRequesters.TryGetValue(e.ApprovalId, out var requester):
                PendingApprovalReplies.Add(new PendingApprovalReply(e, requester, simEvent.SimTime));
                break;

            case UnitDispatched e when Units.TryGetValue(e.UnitId, out var unit):
                unit.OrderedIncidentId = e.IncidentId;
                unit.Phase = ResponsePhase.TurningOut;
                unit.PhaseStartedAt = simEvent.SimTime;
                break;

            case UnitDispatchCancelled e when Units.TryGetValue(e.UnitId, out var unit):
                if (Commands.TryGetValue(e.IncidentId, out var fromCommand))
                    fromCommand.Apply(e);
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

            case ZoneDeclared e when e.Type is ZoneType.RoadClosure or ZoneType.HotZone or ZoneType.FireExclusion:
                DeclaredNoGoAreas[e.ZoneId] = e.Boundary.Count >= 3
                    ? Wgs84.CreatePolygon(e.Boundary)
                    : Wgs84.Factory.CreateLineString(e.Boundary.Select(p => p.ToPoint().Coordinate).ToArray());
                NoGoVersion++;
                break;

            case ZoneLifted e when DeclaredNoGoAreas.Remove(e.ZoneId):
                NoGoVersion++;
                break;

            case UnitBrokeDown e when Units.TryGetValue(e.UnitId, out var broken):
                broken.BrokenDown = true;
                broken.BreakdownFault = e.Fault;
                broken.BrokeDownAt = simEvent.SimTime;
                broken.SpeedKph = 0;
                break;

            case RoadObstructed e:
                Obstructions[e.ObstructionId] = (
                    Wgs84.Factory.CreateLineString(e.Line.Select(p => p.ToPoint().Coordinate).ToArray()), e.Description);
                break;

            case RoadObstructionCleared e:
                Obstructions.Remove(e.ObstructionId);
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
    public string? BreakdownFault { get; set; }
    public DateTimeOffset? BrokeDownAt { get; set; }
    public bool BreakdownReported { get; set; }

    /// <summary>The unit keeps working, but nothing it says reaches command.</summary>
    public bool RadioFailed { get; set; }

    public ResponsePhase Phase { get; set; }
    public DateTimeOffset PhaseStartedAt { get; set; }

    public DateTimeOffset LastFixAt { get; set; }

    /// <summary>The journey being driven: legs along the road network, and progress along them.</summary>
    public IReadOnlyList<RouteLeg> Route { get; set; } = [];
    public int LegIndex { get; set; }
    public double LegProgressMeters { get; set; }
    public GeoPoint Destination { get; set; }

    /// <summary>The <see cref="WorldState.NoGoVersion"/> the route was planned with.</summary>
    public int PlannedWithNoGoVersion { get; set; }

    /// <summary>Obstructions this crew has run into and now avoids.</summary>
    public Dictionary<Guid, Geometry> KnownObstructions { get; } = [];

    /// <summary>When the crew stopped at an obstruction; they re-route after assessing it.</summary>
    public DateTimeOffset? HeldUpSince { get; set; }
    public Guid? HeldUpBy { get; set; }

    public TimeSpan RemainingTime =>
        TimeSpan.FromSeconds(Route.Skip(LegIndex).Sum(l => l.Duration.TotalSeconds)
                             - (LegIndex < Route.Count ? LegProgressMeters / (Route[LegIndex].SpeedKph / 3.6) : 0));
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

public sealed record PendingOrder(OrderIssued Order, DateTimeOffset IssuedAt);

/// <summary>A resource request moving through approval and delivery.</summary>
public sealed class PendingRequest(ResourceRequested request, DateTimeOffset requestedAt)
{
    public ResourceRequested Request { get; } = request;
    public DateTimeOffset RequestedAt { get; } = requestedAt;
    public bool Decided { get; set; }
    public DateTimeOffset? ArriveAt { get; set; }
}

public sealed record PendingNotification(NotificationSent Notification, DateTimeOffset SentAt);

public sealed record PendingApprovalReply(ApprovalDecided Decision, string Requester, DateTimeOffset DecidedAt);

public sealed record WorldWeather(double WindFromDegrees, double WindSpeedMps, double TemperatureC, double RelativeHumidity);
