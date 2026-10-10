using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
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

    public Dictionary<Guid, WorldAgency> Agencies { get; } = [];

    // ---- Phase 6: hazards, people, infrastructure (Design Document §10.3–10.6) ----

    public Dictionary<Guid, WorldHazard> Hazards { get; } = [];
    public Dictionary<Guid, HazardSite> Sites { get; } = [];
    public Dictionary<Guid, PowerOutage> Outages { get; } = [];
    public Dictionary<Guid, WorldCasualty> Casualties { get; } = [];
    public Dictionary<Guid, WorldHospital> Hospitals { get; } = [];
    public List<Civilian> Civilians { get; } = [];
    public Dictionary<Guid, WorldVehicle> Vehicles { get; } = [];

    /// <summary>Evacuation and shelter-in-place orders in force, which civilians respond to.</summary>
    public Dictionary<Guid, ProtectiveAction> ProtectiveActions { get; } = [];

    /// <summary>Who command has notified, and when (a utility told early restores power sooner).</summary>
    public Dictionary<string, DateTimeOffset> Notified { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Knock-on effects so far, oldest first.</summary>
    public List<CascadeRecord> Cascades { get; } = [];

    /// <summary>Jobs AI-run agencies have given their units, by task id: where to go and when the job is done.</summary>
    public Dictionary<Guid, AgencyTask> AgencyTasks { get; } = [];

    public bool InOutage(GeoPoint point) => Outages.Values.Any(o => o.Covers(point));

    // ---- Phase 7: communications (Design Document §13) ----

    public RadioWorld Radio { get; } = new();

    /// <summary>Every order issued, by id, so a wrong read-back can send it again.</summary>
    public Dictionary<Guid, OrderIssued> Orders { get; } = [];

    // ---- Phase 8: crews and safety (Design Document §12) ----

    /// <summary>Firefighters really in trouble, by id (the id of the first Mayday about them, if one is heard).</summary>
    public Dictionary<Guid, WorldDistress> Distress { get; } = [];

    /// <summary>Which unit each Mayday command knows of is about.</summary>
    public Dictionary<Guid, Guid> MaydayUnits { get; } = [];

    public Dictionary<Guid, WorldPar> Pars { get; } = [];
    public Dictionary<Guid, ReliefState> Reliefs { get; } = [];

    /// <summary>Incidents where an evacuation signal has sent operations defensive: crews arriving work from outside.</summary>
    public HashSet<Guid> DefensiveIncidents { get; } = [];

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
                // Everyone hurt so far: the count only grows, so a stale count from the same tick can't undo a newer one.
                incident.ActualCasualties = Math.Max(incident.ActualCasualties, e.ActualCasualties);
                incident.Extinguished = e.Extinguished;
                break;

            case WeatherChanged e:
                Weather = new WorldWeather(e.WindFromDegrees, e.WindSpeedMps, e.TemperatureC, e.RelativeHumidity);
                break;

            case UnitRegistered e:
                Units[e.UnitId] = new WorldUnit
                {
                    Id = e.UnitId, Callsign = e.Callsign, Type = e.Type, Location = e.Location, Home = e.Location,
                    AgencyId = e.AgencyId, CrewSize = e.CrewSize, Capabilities = e.Capabilities,
                    Channel = RadioPlan.DefaultChannel(e.Type, e.AgencyId is { } agency ? Agencies.GetValueOrDefault(agency)?.RadioChannel : null),
                };
                break;

            case UnitChannelAssigned e when Units.TryGetValue(e.UnitId, out var retuned):
                retuned.Channel = e.ChannelId;
                break;

            case RadioBatteryChanged e when Units.TryGetValue(e.UnitId, out var powered):
                powered.Battery = Math.Clamp(e.Level, 0, 1);
                break;

            case RadioCallMade e when Radio.Active:
                Radio.Outbox.Add(new Transmission
                {
                    Id = e.MessageId, Channel = e.ChannelId, From = "Control", To = e.To, Text = e.Text, FromControl = true,
                    HeldSeconds = e.HeldSeconds, Keyed = true, Priority = 5, QueuedAt = simEvent.SimTime, NotBefore = simEvent.SimTime,
                });
                break;

            case RepeatRequested e:
                Radio.RepeatRequests.Add(e.MessageId);
                break;

            case ReadBackConfirmed { Correct: false } e when Orders.TryGetValue(e.OrderId, out var repeatOrder):
                // Wrong read-back: the order goes out again.
                PendingOrders.Add(new PendingOrder(repeatOrder, simEvent.SimTime));
                SendOrder(repeatOrder, simEvent.SimTime);
                break;

            case ChannelPatchRequested e:
                Radio.Patches[e.PatchId] = new PatchState { Id = e.PatchId, ChannelA = e.ChannelA, ChannelB = e.ChannelB, RequestedAt = simEvent.SimTime };
                break;

            case ChannelsPatched e when Radio.Patches.TryGetValue(e.PatchId, out var patched):
                patched.Active = true;
                break;

            case ChannelPatchRemoved e:
                Radio.Patches.Remove(e.PatchId);
                break;

            case CallbackMade e:
                Radio.Callbacks[e.CallId] = simEvent.SimTime;
                break;

            case RadioDeadZonePlaced e:
                Radio.DeadZones.Add(new DeadZone(e.ZoneId, e.Centre, e.RadiusMeters, e.Description));
                break;

            case CellTowerFailed e:
                if (!Radio.Masts.TryGetValue(e.SiteId, out var failing))
                    Radio.Masts[e.SiteId] = failing = new MastState { SiteId = e.SiteId };
                failing.Down = true;
                failing.DownCause = e.Cause;
                break;

            case CellTowerRestored e when Radio.Masts.TryGetValue(e.SiteId, out var restored):
                restored.Down = false;
                restored.OnBatterySince = null;
                break;

            // ---- Crews (Phase 8) ----

            case CrewRostered e when Units.TryGetValue(e.UnitId, out var rostered):
                rostered.Crew.Roster(e.Members, simEvent.SimTime - e.OnShiftFor, e.ShiftLength, simEvent.SimTime);
                break;

            case CrewRelieved e when Units.TryGetValue(e.UnitId, out var relieved):
                // CrewSystem puts the new crew on the unit as it hands over; a scripted change arrives here first.
                if (relieved.Crew.Members.FirstOrDefault()?.Id != e.Members.FirstOrDefault()?.MemberId)
                    relieved.Crew.Roster(e.Members, simEvent.SimTime, e.ShiftLength, simEvent.SimTime);
                relieved.Crew.RehabSince = null;
                relieved.Crew.WorkingSince = relieved.Phase == ResponsePhase.Operating ? simEvent.SimTime : null;
                Reliefs.Remove(e.ReliefId);
                break;

            case CrewRehabOrdered e when Units.TryGetValue(e.UnitId, out var resting):
                resting.Crew.RehabSince = simEvent.SimTime;
                if (resting.Phase is ResponsePhase.OnScene or ResponsePhase.Operating)
                {
                    resting.Phase = ResponsePhase.Rehab;
                    resting.PhaseStartedAt = simEvent.SimTime;
                }
                break;

            case CrewReliefRequested e:
                Reliefs[e.ReliefId] = new ReliefState { Id = e.ReliefId, UnitId = e.UnitId, Briefing = e.FullBriefing, RequestedAt = simEvent.SimTime };
                break;

            case PeerSupportArranged e when Units.TryGetValue(e.UnitId, out var supported):
                supported.Crew.PeerSupportArrangedAt ??= simEvent.SimTime;
                break;

            case ParRequested e:
                StartPar(e.ParId, e.IncidentId, evacuation: false, simEvent.SimTime,
                    $"Control: PAR, PAR, PAR. All crews{At(e.IncidentId)}, report personnel accountability.");
                break;

            case EvacuationSignalled e:
                DefensiveIncidents.Add(e.IncidentId);
                StartPar(e.SignalId, e.IncidentId, evacuation: true, simEvent.SimTime,
                    $"Control: EVACUATE, EVACUATE, EVACUATE. All crews{At(e.IncidentId)}, withdraw from the building now. PAR when you are out.");
                break;

            case EmergencyTrafficDeclared e:
                if (e.Active) Radio.EmergencyTraffic.Add(e.ChannelId);
                else Radio.EmergencyTraffic.Remove(e.ChannelId);
                if (Radio.Active)
                {
                    Radio.Outbox.Add(new Transmission
                    {
                        Channel = e.ChannelId, From = "Control", Text = e.Active
                            ? $"All units on {e.ChannelId}, Control: emergency traffic, emergency traffic. Priority traffic only until further notice."
                            : $"All units on {e.ChannelId}, Control: emergency traffic over. Resume normal traffic.",
                        FromControl = true, Priority = 9, QueuedAt = simEvent.SimTime, NotBefore = simEvent.SimTime,
                    });
                }
                break;

            case MaydayDeclared e:
                MaydayUnits[e.MaydayId] = e.UnitId;
                var distress = Distress.GetValueOrDefault(e.MaydayId)
                               ?? Distress.Values.Where(d => d.UnitId == e.UnitId && !d.Ended).OrderBy(d => d.StartedAt).FirstOrDefault();
                if (distress is not null)
                {
                    distress.Heard = true;
                    distress.MaydayIds.Add(e.MaydayId);
                }
                break;

            case RescueTeamDeployed e when MaydayUnits.TryGetValue(e.MaydayId, out var inTrouble):
                // The rescue team goes for everyone from that crew who is in trouble and has nobody coming yet.
                foreach (var rescue in Distress.Values.Where(d => d.UnitId == inTrouble && !d.Ended && d.RescueUnitId is null))
                {
                    rescue.RescueUnitId = e.UnitId;
                    if (Units.TryGetValue(e.UnitId, out var rescuers))
                        rescuers.Crew.Rescuing = rescue.Id;
                }
                break;

            case FirefighterInDistress e when Units.TryGetValue(e.UnitId, out var caught):
                // Usually CrewSystem has already set this up; a scripted inject arrives here first.
                if (!Distress.ContainsKey(e.DistressId) && caught.Crew.Members.FirstOrDefault(m => m.Id == e.MemberId) is { } member)
                {
                    var trapped = !e.Cause.Contains("lost", StringComparison.OrdinalIgnoreCase);
                    member.State = trapped ? ResponderState.Trapped : ResponderState.Lost;
                    Distress[e.DistressId] = new WorldDistress
                    {
                        Id = e.DistressId, UnitId = e.UnitId, MemberId = e.MemberId, Cause = e.Cause, Location = e.Location,
                        Trapped = trapped, StartedAt = simEvent.SimTime, AirRunsOutAt = simEvent.SimTime + TimeSpan.FromMinutes(e.AirMinutes),
                    };
                }
                break;

            case UnitTasked e when Units.TryGetValue(e.UnitId, out var tasked):
                ReportedIncidentLocations[e.TaskId] = e.Location;
                AgencyTasks.TryAdd(e.TaskId, new AgencyTask(e.TaskId, e.UnitId, e.Task, e.Location));
                tasked.OrderedIncidentId = e.TaskId;
                tasked.Phase = ResponsePhase.TurningOut;
                tasked.PhaseStartedAt = simEvent.SimTime;
                break;

            case HazardStarted e:
                Hazards.TryAdd(e.HazardId, new WorldHazard
                {
                    Id = e.HazardId, Kind = e.Kind, IncidentId = e.WorldIncidentId, Origin = e.Origin, Description = e.Description,
                    Rate = e.Rate, Substance = e.Substance, Inventory = e.Inventory, StartedAt = simEvent.SimTime,
                });
                break;

            case HazardRateChanged e when Hazards.TryGetValue(e.HazardId, out var rated):
                rated.Rate = e.Rate;
                break;

            case HazardEnded e when Hazards.TryGetValue(e.HazardId, out var ended):
                ended.Ended = true;
                break;

            case HazardSitePlaced e:
                Sites.TryAdd(e.SiteId, new HazardSite
                {
                    Id = e.SiteId, Kind = e.Kind, Name = e.Name, Location = e.Location, Substance = e.Substance,
                    Quantity = e.Quantity, ServiceRadiusMeters = e.ServiceRadiusMeters,
                });
                break;

            case PowerOutageStarted e:
                Outages.TryAdd(e.OutageId, new PowerOutage
                {
                    Id = e.OutageId, Centre = e.Centre, RadiusMeters = e.RadiusMeters, Cause = e.Cause, StartedAt = simEvent.SimTime,
                    RestoreAt = simEvent.SimTime + PowerOutage.DefaultRepairTime,
                });
                break;

            case PowerRestored e:
                Outages.Remove(e.OutageId);
                break;

            case CasualtyInjured e:
                Casualties.TryAdd(e.CasualtyId, new WorldCasualty
                {
                    Id = e.CasualtyId, IncidentId = e.WorldIncidentId, Location = e.Location, Triage = e.Triage,
                    State = e.Triage == Triage.Deceased ? CasualtyState.Deceased : CasualtyState.AwaitingTreatment,
                    Cause = e.Cause, InjuredAt = simEvent.SimTime,
                });
                break;

            case CasualtyChanged e when Casualties.TryGetValue(e.CasualtyId, out var casualty):
                casualty.Triage = e.Triage;
                casualty.State = e.State;
                casualty.HospitalId = e.HospitalId ?? casualty.HospitalId;
                break;

            case HospitalRegistered e:
                Hospitals.TryAdd(e.HospitalId, new WorldHospital
                {
                    Id = e.HospitalId, Name = e.Name, Location = e.Location, Capacity = e.EdCapacity, NormalCapacity = e.EdCapacity,
                    Baseline = e.Occupied, ReportedOccupied = e.Occupied, ReportedAt = simEvent.SimTime,
                });
                break;

            case HospitalCapacityChanged e when Hospitals.TryGetValue(e.HospitalId, out var hospital):
                hospital.Capacity = e.Capacity;
                break;

            case CascadeOccurred e:
                Cascades.Add(new CascadeRecord(simEvent.SimTime, e.Cause, e.Effect, e.UnitId));
                break;

            case NotificationSent e:
                PendingNotifications.Add(new PendingNotification(e, simEvent.SimTime));
                Notified.TryAdd(e.Recipient, simEvent.SimTime);
                break;

            case ZoneDeclared e when e.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace && e.Boundary.Count >= 3:
                ProtectiveActions[e.ZoneId] = new ProtectiveAction(e.ZoneId, e.Type, Wgs84.CreatePolygon(e.Boundary), simEvent.SimTime);
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
                Agencies[e.AgencyId] = new WorldAgency(e.Name, e.Type, e.AiControlled, e.RadioChannel);
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
                Orders[e.OrderId] = e;
                PendingOrders.Add(new PendingOrder(e, simEvent.SimTime));
                SendOrder(e, simEvent.SimTime);
                break;
            case ResourceRequested e:
                PendingRequests.Add(new PendingRequest(e, simEvent.SimTime));
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
            // A crew reporting that it is transporting a patient is carrying on with its own work, not being stopped.
            case UnitStatusChanged e when Units.TryGetValue(e.UnitId, out var unit)
                                          && e.Status is UnitStatus.Available or UnitStatus.Transporting
                                              or UnitStatus.OutOfService or UnitStatus.Cancelled
                                          && !(e.Status == UnitStatus.Transporting && simEvent.Source == EventSources.Comms):
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

            case ZoneLifted e:
                ProtectiveActions.Remove(e.ZoneId);
                if (DeclaredNoGoAreas.Remove(e.ZoneId))
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

    private string At(Guid? incidentId) =>
        incidentId is { } id && Commands.ContainsKey(id) ? " at the incident" : "";

    /// <summary>
    /// A PAR or evacuation signal: every crew at the scene should answer. It goes out on each channel they work on;
    /// without simulated comms everyone hears it at once.
    /// </summary>
    private void StartPar(Guid parId, Guid? incidentId, bool evacuation, DateTimeOffset at, string text)
    {
        var par = new WorldPar { Id = parId, IncidentId = incidentId, RequestedAt = at, Evacuation = evacuation };
        foreach (var unit in Units.Values.Where(u => u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating or ResponsePhase.Rehab
                                                     && u.OrderedIncidentId is { } ordered && !AgencyTasks.ContainsKey(ordered)
                                                     && (incidentId is null || ordered == incidentId)
                                                     && !(u.AgencyId is { } agency && Agencies.GetValueOrDefault(agency)?.AiControlled == true)))
        {
            par.Involved.Add(unit.Id);
            if (!Radio.Active) par.HeardBy[unit.Id] = at;
        }
        Pars[parId] = par;
        if (!Radio.Active) return;

        foreach (var channel in par.Involved.Select(id => Units[id].Channel).Distinct())
        {
            Radio.Outbox.Add(new Transmission
            {
                Channel = channel, From = "Control", Text = text, FromControl = true, ParId = parId,
                Priority = evacuation ? 10 : 7, QueuedAt = at, NotBefore = at,
            });
        }
    }

    /// <summary>Puts an order on the air: the unit's channel, or the fire command channel for supervisors and staff.</summary>
    private void SendOrder(OrderIssued order, DateTimeOffset at)
    {
        if (!Radio.Active) return;
        var channel = order.TargetKind == OrderTargetKind.Unit && order.TargetId is { } unitId && Units.TryGetValue(unitId, out var target)
            ? target.Channel
            : RadioPlan.FireCommand;
        Radio.Outbox.Add(new Transmission
        {
            Channel = channel, From = "Control", To = order.TargetName, Text = $"{order.TargetName}, Control: {order.Text}",
            FromControl = true, OrderId = order.OrderId, Priority = 5, QueuedAt = at, NotBefore = at,
        });
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

    public Guid? AgencyId { get; init; }

    /// <summary>Seats on the appliance and its equipment, from the roster (for making up its crew).</summary>
    public int CrewSize { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>The radio channel the crew works on.</summary>
    public string Channel { get; set; } = RadioPlan.FireCommand;

    /// <summary>Handheld radio charge, 0–1. Flat means the crew can't be heard (or hear) until they swap batteries.</summary>
    public double Battery { get; set; } = 1;

    /// <summary>When the crew reported a low battery; they swap it a few minutes later.</summary>
    public DateTimeOffset? LowBatteryReportedAt { get; set; }

    /// <summary>Status messages waiting for mobile-data coverage (store and forward).</summary>
    public List<(DomainEvent Payload, string Source)> PendingData { get; } = [];

    /// <summary>
    /// Set by another system that wants this unit driven somewhere (e.g. an ambulance leaving for hospital);
    /// <c>UnitResponseSystem</c> plans the route on its next tick and reports it with this reason.
    /// </summary>
    public string? PendingPlanReason { get; set; }

    /// <summary>When the crew last compared its progress with what its navigation promised.</summary>
    public DateTimeOffset LastTrafficCheckAt { get; set; }

    public bool BrokenDown { get; set; }
    public string? BreakdownFault { get; set; }
    public DateTimeOffset? BrokeDownAt { get; set; }
    public bool BreakdownReported { get; set; }

    /// <summary>The unit keeps working, but nothing it says reaches command.</summary>
    public bool RadioFailed { get; set; }

    /// <summary>The people riding it, as they really are (Phase 8). Empty when no crew model is running.</summary>
    public WorldCrew Crew { get; } = new();

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

    /// <summary>Ambulance carrying patients to hospital (MedicalSystem).</summary>
    Transporting,

    /// <summary>Ambulance handing patients over at the hospital.</summary>
    AtHospital,

    /// <summary>At the scene but resting in the rehab area (CrewSystem): not working.</summary>
    Rehab,
}

/// <summary>A job an AI-run agency gave one of its units (AgencyAiSystem).</summary>
public sealed record AgencyTask(Guid TaskId, Guid UnitId, string Task, GeoPoint Location)
{
    public DateTimeOffset? ClearAt { get; set; }
}

/// <summary>An order on its way to its recipient, and whether it has been heard (Phase 7).</summary>
public sealed class PendingOrder(OrderIssued order, DateTimeOffset issuedAt)
{
    public OrderIssued Order { get; } = order;
    public DateTimeOffset IssuedAt { get; } = issuedAt;

    /// <summary>When the recipient heard it; read-back follows from then.</summary>
    public DateTimeOffset? HeardAt { get; set; }

    /// <summary>Heard, but not clearly: the read-back will be wrong.</summary>
    public bool HeardGarbled { get; set; }
}

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
