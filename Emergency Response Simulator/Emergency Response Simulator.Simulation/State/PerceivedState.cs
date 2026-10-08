using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// What the organisation currently believes: the data behind the COP (Design Document §6.7, §10.1).
/// Built purely by projecting perceived events in sequence order, so it can be rebuilt as of any
/// moment for replay and the AAR. Truth events are ignored.
/// </summary>
public sealed class PerceivedState : ICopService
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Agency> _agencies = [];
    private readonly Dictionary<Guid, Incident> _incidents = [];
    private readonly Dictionary<Guid, Unit> _units = [];
    private readonly Dictionary<Guid, Report> _reports = [];
    private readonly Dictionary<Guid, Alert> _alerts = [];
    private readonly Dictionary<Guid, Zone> _zones = [];

    public event EventHandler? Changed;

    public DateTimeOffset AsOf { get; private set; }
    public long LastSequence { get; private set; }

    public IReadOnlyList<Agency> Agencies => Snapshot(_agencies);
    public IReadOnlyList<Incident> Incidents => Snapshot(_incidents);
    public IReadOnlyList<Unit> Units => Snapshot(_units);
    public IReadOnlyList<Report> Reports => Snapshot(_reports);
    public IReadOnlyList<Alert> Alerts => Snapshot(_alerts);
    public IReadOnlyList<Zone> Zones => Snapshot(_zones);
    public PerceivedWeather? Weather { get; private set; }

    public Incident? FindIncident(Guid incidentId)
    {
        lock (_lock) return _incidents.GetValueOrDefault(incidentId);
    }

    public Unit? FindUnit(Guid unitId)
    {
        lock (_lock) return _units.GetValueOrDefault(unitId);
    }

    /// <summary>Keeps this picture up to date with a live session.</summary>
    public void Follow(IEventStore store, Guid sessionId)
    {
        store.Appended += (_, simEvent) =>
        {
            if (simEvent.SessionId == sessionId)
                Apply(simEvent);
        };
    }

    public void Apply(SimEvent simEvent)
    {
        if (simEvent.Visibility != EventVisibility.Perceived)
            return;

        lock (_lock)
        {
            if (simEvent.Sequence <= LastSequence)
                return; // already applied

            ApplyPayload(simEvent.Payload, simEvent.SimTime);
            LastSequence = simEvent.Sequence;
            AsOf = simEvent.SimTime;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPayload(DomainEvent payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case AgencyRegistered e:
                _agencies[e.AgencyId] = new Agency { Id = e.AgencyId, Name = e.Name, ShortName = e.ShortName, Type = e.Type };
                break;

            case UnitRegistered e:
                var registered = new Unit
                {
                    Id = e.UnitId,
                    Name = e.Callsign,
                    Callsign = e.Callsign,
                    Type = e.Type,
                    AgencyId = e.AgencyId,
                    Agency = e.AgencyId is { } agencyId ? _agencies.GetValueOrDefault(agencyId) : null,
                    Location = e.Location.ToPoint(),
                    HomeStation = e.HomeStation,
                    CrewSize = e.CrewSize,
                    Capabilities = [.. e.Capabilities],
                    LastAvlUpdate = at,
                };
                _units[e.UnitId] = registered;
                registered.Agency?.Resources.Add(registered);
                break;

            case CallReceived e:
                AddReport(new Report
                {
                    Id = e.CallId,
                    Source = ReportSource.EmergencyCall,
                    SourceName = e.Caller,
                    Claim = e.Summary,
                    Confidence = Confidence.Low,
                    Verification = VerificationStatus.Reported,
                    Location = e.Location?.ToPoint(),
                    LocationAccuracyMeters = e.LocationAccuracyMeters,
                    ReceivedAt = at,
                });
                break;

            case ReportReceived e:
                AddReport(new Report
                {
                    Id = e.ReportId,
                    IncidentId = e.IncidentId,
                    Source = e.Source,
                    SourceName = e.SourceName,
                    Claim = e.Claim,
                    Confidence = e.Confidence,
                    Verification = e.Verification,
                    Location = e.Location?.ToPoint(),
                    LocationAccuracyMeters = e.LocationAccuracyMeters,
                    ReceivedAt = at,
                });
                break;

            case IncidentCreated e:
                _incidents[e.IncidentId] = new Incident
                {
                    Id = e.IncidentId,
                    Number = e.Number,
                    Name = e.Name,
                    Type = e.Type,
                    Priority = e.Priority,
                    Location = e.Location.ToPoint(),
                    Address = e.Address,
                    ReportedAt = at,
                    LastUpdatedAt = at,
                };
                break;

            case IncidentUpdated e when _incidents.TryGetValue(e.IncidentId, out var incident):
                incident.Priority = e.Priority ?? incident.Priority;
                incident.Status = e.Status ?? incident.Status;
                incident.CasualtiesReported = e.CasualtiesReported ?? incident.CasualtiesReported;
                incident.CasualtiesConfirmed = e.CasualtiesConfirmed ?? incident.CasualtiesConfirmed;
                incident.EvacuationRequired = e.EvacuationRequired ?? incident.EvacuationRequired;
                if (e.Threats is not null)
                    incident.Threats = [.. e.Threats];
                incident.LastUpdatedAt = at;
                break;

            case UnitDispatched e when _units.TryGetValue(e.UnitId, out var unit):
                Unassign(unit);
                unit.Status = UnitStatus.Dispatched;
                unit.AssignedIncidentId = e.IncidentId;
                if (_incidents.TryGetValue(e.IncidentId, out var target))
                {
                    unit.AssignedIncident = target;
                    target.AssignedUnits.Add(unit);
                    target.LastUpdatedAt = at;
                }
                break;

            case UnitDispatchCancelled e when _units.TryGetValue(e.UnitId, out var unit):
                // CANCELLED → AVAILABLE (Design Document §6.4)
                Unassign(unit);
                unit.Status = UnitStatus.Available;
                unit.Eta = null;
                break;

            case UnitStatusChanged e when _units.TryGetValue(e.UnitId, out var unit):
                unit.Status = e.Status;
                if (e.Status is UnitStatus.Available or UnitStatus.OutOfService)
                    Unassign(unit);
                break;

            case UnitPositionReported e when _units.TryGetValue(e.UnitId, out var unit):
                unit.Location = e.Location.ToPoint();
                unit.SpeedKph = e.SpeedKph;
                unit.Heading = e.Heading;
                unit.Eta = e.Eta;
                unit.LastAvlUpdate = at;
                break;

            case AlertRaised e:
                var alert = new Alert
                {
                    Id = e.AlertId,
                    Category = e.Category,
                    Severity = e.Severity,
                    Title = e.Title,
                    Message = e.Message,
                    IncidentId = e.IncidentId,
                    UnitId = e.UnitId,
                    RaisedAt = at,
                };
                _alerts[e.AlertId] = alert;
                if (e.IncidentId is { } alertIncidentId && _incidents.TryGetValue(alertIncidentId, out var alertIncident))
                {
                    alert.Incident = alertIncident;
                    alertIncident.Alerts.Add(alert);
                }
                break;

            case AlertAcknowledged e when _alerts.TryGetValue(e.AlertId, out var acknowledged):
                acknowledged.AcknowledgedAt = at;
                acknowledged.AcknowledgedById = e.UserId;
                break;

            case ZoneDeclared e:
                var zone = new Zone
                {
                    Id = e.ZoneId,
                    Name = e.Name,
                    Type = e.Type,
                    Area = ToGeometry(e.Boundary),
                    IncidentId = e.IncidentId,
                    EffectiveFrom = at,
                };
                _zones[e.ZoneId] = zone;
                if (e.IncidentId is { } zoneIncidentId && _incidents.TryGetValue(zoneIncidentId, out var zoneIncident))
                {
                    zone.Incident = zoneIncident;
                    zoneIncident.Zones.Add(zone);
                }
                break;

            case WeatherObserved e:
                Weather = new PerceivedWeather(e.Source, e.Location, e.WindFromDegrees, e.WindSpeedMps,
                    e.TemperatureC, e.RelativeHumidity, at);
                break;

            case ZoneLifted e when _zones.Remove(e.ZoneId, out var lifted):
                lifted.EffectiveUntil = at;
                lifted.Incident?.Zones.Remove(lifted);
                break;
        }
    }

    private void AddReport(Report report)
    {
        _reports[report.Id] = report;
        if (report.IncidentId is { } incidentId && _incidents.TryGetValue(incidentId, out var incident))
        {
            report.Incident = incident;
            incident.Reports.Add(report);
        }
    }

    private static void Unassign(Unit unit)
    {
        unit.AssignedIncident?.AssignedUnits.Remove(unit);
        unit.AssignedIncident = null;
        unit.AssignedIncidentId = null;
    }

    private static Geometry ToGeometry(IReadOnlyList<GeoPoint> boundary) => boundary.Count switch
    {
        >= 3 => Wgs84.CreatePolygon(boundary),
        2 => Wgs84.Factory.CreateLineString(boundary.Select(p => p.ToPoint().Coordinate).ToArray()),
        _ => throw new ArgumentException("A zone boundary needs at least two points.", nameof(boundary)),
    };

    private IReadOnlyList<T> Snapshot<T>(Dictionary<Guid, T> source)
    {
        lock (_lock) return source.Values.ToList();
    }
}
