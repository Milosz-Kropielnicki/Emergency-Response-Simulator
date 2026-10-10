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
    private readonly Dictionary<Guid, Order> _orders = [];
    private readonly Dictionary<Guid, ResourceRequest> _requests = [];
    private readonly Dictionary<Guid, ApprovalRequest> _approvals = [];
    private readonly Dictionary<Guid, Notification> _notifications = [];
    private readonly Dictionary<Guid, OperationalPeriod> _periods = [];
    private readonly Dictionary<Guid, IncidentActionPlan> _plans = [];
    private readonly Dictionary<Guid, ObjectiveStatus> _progress = [];
    private readonly Dictionary<Guid, Hospital> _hospitals = [];
    private readonly List<CommsEntry> _comms = [];
    private readonly Dictionary<Guid, CommsEntry> _commsById = [];
    private readonly Dictionary<string, ChannelState> _channels =
        RadioPlan.Standard.ToDictionary(c => c.Id, c => new ChannelState { Info = c });
    private readonly Dictionary<Guid, ChannelPatch> _patches = [];
    private readonly Dictionary<Guid, MissedCall> _missed = [];

    /// <summary>The comms log keeps this many messages; older ones are in the event stream.</summary>
    private const int MaxCommsEntries = 1000;

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
    public IReadOnlyList<Hospital> Hospitals => Snapshot(_hospitals);

    public IReadOnlyList<CommsEntry> CommsLog
    {
        get { lock (_lock) return _comms.ToList(); }
    }

    public IReadOnlyList<ChannelState> Channels
    {
        get
        {
            lock (_lock)
            {
                return _channels.Values.Select(c =>
                {
                    var copy = new ChannelState { Info = c.Info, BusyUntil = c.BusyUntil, Talker = c.Talker };
                    copy.RecentAirtime.AddRange(c.RecentAirtime);
                    return copy;
                }).ToList();
            }
        }
    }

    public IReadOnlyList<ChannelPatch> Patches => Snapshot(_patches);
    public IReadOnlyList<MissedCall> MissedCalls => Snapshot(_missed);
    public IReadOnlyList<Order> Orders => Snapshot(_orders);
    public IReadOnlyList<ResourceRequest> ResourceRequests => Snapshot(_requests);
    public IReadOnlyList<ApprovalRequest> Approvals => Snapshot(_approvals);
    public IReadOnlyList<Notification> Notifications => Snapshot(_notifications);
    public IReadOnlyList<OperationalPeriod> OperationalPeriods => Snapshot(_periods);
    public IReadOnlyList<IncidentActionPlan> ActionPlans => Snapshot(_plans);

    public IReadOnlyDictionary<Guid, ObjectiveStatus> ObjectiveProgress
    {
        get { lock (_lock) return new Dictionary<Guid, ObjectiveStatus>(_progress); }
    }

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
                _agencies[e.AgencyId] = new Agency
                {
                    Id = e.AgencyId, Name = e.Name, ShortName = e.ShortName, Type = e.Type, AiControlled = e.AiControlled,
                    RadioChannel = e.RadioChannel,
                };
                // Another service's radio system: known, but not monitored until patched.
                if (e.RadioChannel is { } own && !_channels.ContainsKey(own))
                    _channels[own] = new ChannelState
                    {
                        Info = new ChannelInfo(own, $"{e.Name} radio", ChannelKind.Radio, e.Type, false, $"{e.Name}'s own radio system"),
                    };
                break;

            case UnitChannelAssigned e when _units.TryGetValue(e.UnitId, out var retuned):
                retuned.Channel = e.ChannelId;
                break;

            case CommsLogged e:
                var entry = new CommsEntry
                {
                    Id = e.MessageId, ChannelId = e.ChannelId, From = e.From, To = e.To, Text = e.Text, Quality = e.Quality,
                    At = at, Duration = TimeSpan.FromSeconds(e.DurationSeconds), UnitId = e.UnitId, FromControl = e.FromControl,
                    Notes = e.Notes ?? [],
                };
                _comms.Add(entry);
                _commsById[entry.Id] = entry;
                if (_comms.Count > MaxCommsEntries)
                {
                    _commsById.Remove(_comms[0].Id);
                    _comms.RemoveAt(0);
                }
                if (_channels.TryGetValue(e.ChannelId, out var channel) && channel.Info.Kind == ChannelKind.Radio)
                {
                    channel.RecentAirtime.Add((at, entry.Duration));
                    channel.RecentAirtime.RemoveAll(a => at - a.Start > TimeSpan.FromMinutes(5));
                    if (at + entry.Duration > channel.BusyUntil)
                    {
                        channel.BusyUntil = at + entry.Duration;
                        channel.Talker = e.From;
                    }
                }
                // Hearing a unit on the radio, even badly, is contact.
                if (e.UnitId is { } speaker && _units.TryGetValue(speaker, out var speaking))
                    Contact(speaking, at);
                break;

            case RepeatRequested e when _commsById.TryGetValue(e.MessageId, out var repeated):
                repeated.RepeatRequested = true;
                break;

            case ReadBackConfirmed e when _orders.TryGetValue(e.OrderId, out var looped):
                if (e.Correct)
                {
                    looped.ReadBackConfirmedAt = at;
                }
                else
                {
                    // Wrong read-back: the order goes out again and waits for a new one.
                    looped.Status = OrderStatus.Issued;
                    looped.AcknowledgedAt = null;
                    looped.ReadBack = null;
                    looped.ReadBackGarbled = false;
                }
                break;

            case ChannelPatchRequested e:
                _patches[e.PatchId] = new ChannelPatch { Id = e.PatchId, ChannelA = e.ChannelA, ChannelB = e.ChannelB, RequestedAt = at };
                break;

            case ChannelsPatched e when _patches.TryGetValue(e.PatchId, out var patch):
                patch.ActiveFrom = at;
                break;

            case ChannelPatchRemoved e:
                _patches.Remove(e.PatchId);
                break;

            case CallMissed e:
                _missed[e.CallId] = new MissedCall { Id = e.CallId, At = at, Waited = e.Waited, Location = e.Location, AccuracyMeters = e.AccuracyMeters };
                break;

            case CallbackMade e when _missed.TryGetValue(e.CallId, out var missed):
                missed.CalledBackAt = at;
                break;

            case HospitalRegistered e:
                _hospitals[e.HospitalId] = new Hospital
                {
                    Id = e.HospitalId, Name = e.Name, Location = e.Location, Capacity = e.EdCapacity, Occupied = e.Occupied,
                    ReportedAt = at,
                };
                break;

            case HospitalStatusReported e when _hospitals.TryGetValue(e.HospitalId, out var hospital):
                hospital.Occupied = e.Occupied;
                hospital.Capacity = e.Capacity;
                hospital.OnDiversion = e.OnDiversion;
                hospital.Note = e.Note;
                hospital.ReportedAt = at;
                break;

            case UnitTasked e when _units.TryGetValue(e.UnitId, out var tasked):
                // A job from the unit's own agency, not one of command's incidents.
                Unassign(tasked);
                Contact(tasked, at);
                tasked.Status = UnitStatus.Dispatched;
                tasked.Tasking = $"{e.TaskedBy}: {e.Task}";
                tasked.PlannedRoute = null;
                tasked.RouteDistanceMeters = null;
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
                    Channel = RadioPlan.DefaultChannel(e.Type, e.AgencyId is { } radioAgency ? _agencies.GetValueOrDefault(radioAgency)?.RadioChannel : null),
                    LastAvlUpdate = at,
                    LastContactAt = at,
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
                if (e.FromUnitId is { } reportingUnitId && _units.TryGetValue(reportingUnitId, out var reportingUnit))
                    Contact(reportingUnit, at);
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

            case ReportAssessed e when _reports.TryGetValue(e.ReportId, out var assessed):
                assessed.Verification = e.Verification;
                assessed.Confidence = e.Confidence;
                break;

            case ReportLinked e when _reports.TryGetValue(e.ReportId, out var linked)
                                     && _incidents.TryGetValue(e.IncidentId, out var linkedIncident):
                linked.Incident?.Reports.Remove(linked);
                linked.IncidentId = e.IncidentId;
                linked.Incident = linkedIncident;
                linkedIncident.Reports.Add(linked);
                break;

            case IncidentCommanderAssigned e when _incidents.TryGetValue(e.IncidentId, out var commanded):
                commanded.IncidentCommanderName = e.Name;
                commanded.IncidentCommanderId = e.UserId;
                commanded.Command.Apply(e);
                commanded.LastUpdatedAt = at;
                break;

            case IcsPositionAssigned e when _incidents.TryGetValue(e.IncidentId, out var staffed):
                staffed.Command.Apply(e);
                if (e.Role == IcsRole.IncidentCommander)
                    staffed.IncidentCommanderName = e.Name;
                staffed.LastUpdatedAt = at;
                break;

            case IcsGroupFormed e when _incidents.TryGetValue(e.IncidentId, out var organised):
                organised.Command.Apply(e);
                break;

            case IcsGroupDisbanded e when _incidents.TryGetValue(e.IncidentId, out var reorganised):
                reorganised.Command.Apply(e);
                break;

            case UnitAssignedToGroup e when _incidents.TryGetValue(e.IncidentId, out var grouped):
                grouped.Command.Apply(e);
                break;

            case OrderIssued e:
                _orders[e.OrderId] = new Order
                {
                    Id = e.OrderId, IncidentId = e.IncidentId, TargetKind = e.TargetKind, TargetId = e.TargetId,
                    TargetName = e.TargetName, Text = e.Text, IssuedAt = at,
                };
                break;

            case OrderAcknowledged e when _orders.TryGetValue(e.OrderId, out var acknowledgedOrder):
                acknowledgedOrder.Status = OrderStatus.Acknowledged;
                acknowledgedOrder.AcknowledgedAt = at;
                acknowledgedOrder.ReadBack = e.ReadBack;
                acknowledgedOrder.ReadBackGarbled = e.Garbled;
                if (acknowledgedOrder.TargetKind == OrderTargetKind.Unit && acknowledgedOrder.TargetId is { } ackUnit
                    && _units.TryGetValue(ackUnit, out var answering))
                {
                    Contact(answering, at);
                }
                break;

            case OrderClosed e when _orders.TryGetValue(e.OrderId, out var closedOrder):
                closedOrder.Status = e.Completed ? OrderStatus.Completed : OrderStatus.Cancelled;
                closedOrder.ClosedAt = at;
                break;

            case ResourceRequested e:
                _requests[e.RequestId] = new ResourceRequest
                {
                    Id = e.RequestId, IncidentId = e.IncidentId, Kind = e.Kind, UnitType = e.UnitType, Quantity = e.Quantity,
                    Description = e.Description, Justification = e.Justification, RequestedAt = at,
                };
                break;

            case ResourceRequestDecided e when _requests.TryGetValue(e.RequestId, out var decided):
                decided.Status = e.Approved ? ResourceRequestStatus.Approved : ResourceRequestStatus.Denied;
                decided.DecidedBy = e.DecidedBy;
                decided.DecisionReason = e.Reason;
                decided.DecidedAt = at;
                decided.ExpectedAt = e.ExpectedAt;
                break;

            case ResourceRequestFulfilled e when _requests.TryGetValue(e.RequestId, out var fulfilled):
                fulfilled.Status = ResourceRequestStatus.Fulfilled;
                fulfilled.FulfilledBy.AddRange(e.UnitIds);
                break;

            case ApprovalRequested e:
                _approvals[e.ApprovalId] = new ApprovalRequest
                {
                    Id = e.ApprovalId, IncidentId = e.IncidentId, Subject = e.Subject, Details = e.Details,
                    RequestedBy = e.RequestedBy, RequestedAt = at,
                };
                break;

            case ApprovalDecided e when _approvals.TryGetValue(e.ApprovalId, out var approval):
                approval.Status = e.Approved ? ApprovalStatus.Approved : ApprovalStatus.Denied;
                approval.Note = e.Note;
                approval.DecidedAt = at;
                break;

            case NotificationSent e:
                _notifications[e.NotificationId] = new Notification
                {
                    Id = e.NotificationId, IncidentId = e.IncidentId, Recipient = e.Recipient, Message = e.Message, SentAt = at,
                };
                break;

            case NotificationAnswered e when _notifications.TryGetValue(e.NotificationId, out var answered):
                answered.Reply = e.Reply;
                answered.RepliedAt = at;
                break;

            case OperationalPeriodStarted e when _incidents.TryGetValue(e.IncidentId, out var planned):
                // Starting a period early cuts the previous one short.
                foreach (var earlier in _periods.Values.Where(p => p.IncidentId == e.IncidentId && p.End > e.Start && p.Start <= e.Start))
                    earlier.End = e.Start;
                var period = new OperationalPeriod
                {
                    Id = e.PeriodId, IncidentId = e.IncidentId, Incident = planned, Number = e.Number,
                    Start = e.Start, End = e.End, Focus = e.Focus,
                };
                _periods[e.PeriodId] = period;
                planned.OperationalPeriods.Add(period);
                break;

            case IapDraftCreated e when _periods.TryGetValue(e.PeriodId, out var draftPeriod):
                _plans[e.PlanId] = new IncidentActionPlan
                {
                    Id = e.PlanId, IncidentId = e.IncidentId, Incident = _incidents.GetValueOrDefault(e.IncidentId),
                    OperationalPeriodId = e.PeriodId, OperationalPeriod = draftPeriod, Version = e.Version,
                    BasedOnId = e.BasedOnId, PreparedBy = e.PreparedBy, Content = e.Content.Clone(),
                    CreatedAt = at, LastSavedAt = at,
                };
                break;

            case IapDraftSaved e when _plans.TryGetValue(e.PlanId, out var saved):
                saved.Content = e.Content.Clone();
                saved.LastSavedAt = at;
                break;

            case IapSubmitted e when _plans.TryGetValue(e.PlanId, out var submitted):
                submitted.Status = IapStatus.PendingApproval;
                submitted.SubmittedBy = e.SubmittedBy;
                submitted.SubmittedAt = at;
                submitted.ReturnedBy = null;
                submitted.ReturnComments = null;
                break;

            case IapReturned e when _plans.TryGetValue(e.PlanId, out var returned):
                returned.Status = IapStatus.Draft;
                returned.ReturnedBy = e.ReturnedBy;
                returned.ReturnComments = e.Comments;
                break;

            case IapApproved e when _plans.TryGetValue(e.PlanId, out var approved):
                foreach (var older in _plans.Values.Where(p => p.OperationalPeriodId == approved.OperationalPeriodId
                                                               && p.Status == IapStatus.Approved))
                    older.Status = IapStatus.Superseded;
                approved.Status = IapStatus.Approved;
                approved.ApprovedBy = e.ApprovedBy;
                approved.ApprovedAt = at;
                break;

            case IapBriefed e when _plans.TryGetValue(e.PlanId, out var briefed):
                briefed.BriefedAt = at;
                break;

            case ObjectiveStatusChanged e:
                _progress[e.ObjectiveId] = e.Status;
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
                unit.Tasking = null;
                unit.PlannedRoute = null; // a new assignment means a new journey
                unit.RouteDistanceMeters = null;
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
                if (_incidents.TryGetValue(e.IncidentId, out var stoodDownFrom))
                    stoodDownFrom.Command.Apply(e);
                Unassign(unit);
                unit.Status = UnitStatus.Available;
                unit.Eta = null;
                break;

            case UnitStatusChanged e when _units.TryGetValue(e.UnitId, out var unit):
                Contact(unit, at);
                unit.Status = e.Status;
                if (e.Status is UnitStatus.Available or UnitStatus.OutOfService)
                {
                    Unassign(unit);
                    unit.Tasking = null;
                }
                if (e.Status is not (UnitStatus.Dispatched or UnitStatus.EnRoute))
                {
                    unit.PlannedRoute = null;
                    unit.RouteDistanceMeters = null;
                }
                break;

            case UnitPositionReported e when _units.TryGetValue(e.UnitId, out var unit):
                unit.Location = e.Location.ToPoint();
                unit.SpeedKph = e.SpeedKph;
                unit.Heading = e.Heading;
                unit.Eta = e.Eta;
                unit.LastAvlUpdate = at;
                Contact(unit, at);
                break;

            case RouteReported e when _units.TryGetValue(e.UnitId, out var routed):
                routed.PlannedRoute = e.Path.Count >= 2
                    ? Wgs84.Factory.CreateLineString(e.Path.Select(p => p.ToPoint().Coordinate).ToArray())
                    : null;
                routed.RouteDistanceMeters = e.DistanceMeters;
                routed.Eta = e.Eta;
                Contact(routed, at);
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
                if (e.Category == AlertCategory.CommunicationFailure && e.UnitId is { } silentId
                    && _units.TryGetValue(silentId, out var silent))
                {
                    silent.CommsConnected = false;
                }
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

    /// <summary>Anything heard from a unit restores its comms status.</summary>
    private static void Contact(Unit unit, DateTimeOffset at)
    {
        unit.LastContactAt = at;
        unit.CommsConnected = true;
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
