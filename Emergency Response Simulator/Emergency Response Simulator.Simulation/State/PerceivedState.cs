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
    private readonly Dictionary<Guid, Crew> _crews = [];
    private readonly Dictionary<Guid, ParCheck> _pars = [];
    private readonly Dictionary<Guid, Mayday> _maydays = [];
    private readonly Dictionary<string, Jurisdiction> _jurisdictions = [];
    private readonly Dictionary<Guid, MutualAidAgreement> _agreements = [];
    private readonly List<UnifiedCommandInvitation> _invitations = [];
    private readonly Dictionary<Guid, AssistanceRequest> _assistance = [];
    private readonly Dictionary<Guid, AuthorityDispute> _disputes = [];
    private readonly EocStatus _eoc = new();
    private readonly List<PublicStatement> _statements = [];
    private readonly Dictionary<Guid, MediaEnquiry> _enquiries = [];
    private readonly Dictionary<Guid, PoliticalCall> _politicalCalls = [];
    private readonly Dictionary<Guid, PlumeProjection> _projections = [];
    private readonly PublicSituation _public = new();
    private readonly Dictionary<Guid, Participant> _participants = [];

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
                    var copy = new ChannelState { Info = c.Info, BusyUntil = c.BusyUntil, Talker = c.Talker, EmergencyTraffic = c.EmergencyTraffic };
                    copy.RecentAirtime.AddRange(c.RecentAirtime);
                    return copy;
                }).ToList();
            }
        }
    }

    public IReadOnlyList<ChannelPatch> Patches => Snapshot(_patches);
    public IReadOnlyList<MissedCall> MissedCalls => Snapshot(_missed);
    public IReadOnlyList<Crew> Crews => Snapshot(_crews);

    public IReadOnlyList<ParCheck> ParChecks
    {
        get { lock (_lock) return _pars.Values.OrderBy(p => p.RequestedAt).ToList(); }
    }

    public IReadOnlyList<Mayday> Maydays
    {
        get { lock (_lock) return _maydays.Values.OrderBy(m => m.DeclaredAt).ToList(); }
    }
    public IReadOnlyList<Order> Orders => Snapshot(_orders);
    public IReadOnlyList<ResourceRequest> ResourceRequests => Snapshot(_requests);
    public IReadOnlyList<ApprovalRequest> Approvals => Snapshot(_approvals);
    public IReadOnlyList<Notification> Notifications => Snapshot(_notifications);
    public IReadOnlyList<OperationalPeriod> OperationalPeriods => Snapshot(_periods);
    public IReadOnlyList<IncidentActionPlan> ActionPlans => Snapshot(_plans);

    public IReadOnlyList<Jurisdiction> Jurisdictions
    {
        get { lock (_lock) return _jurisdictions.Values.OrderBy(j => j.Level).ToList(); }
    }

    public IReadOnlyList<MutualAidAgreement> MutualAidAgreements => Snapshot(_agreements);

    public IReadOnlyList<UnifiedCommandInvitation> UnifiedCommandInvitations
    {
        get { lock (_lock) return _invitations.ToList(); }
    }

    public IReadOnlyList<AssistanceRequest> AssistanceRequests => Snapshot(_assistance);

    public IReadOnlyList<AuthorityDispute> Disputes
    {
        get { lock (_lock) return _disputes.Values.OrderBy(d => d.RaisedAt).ToList(); }
    }

    public EocStatus Eoc
    {
        get { lock (_lock) return _eoc.Copy(); }
    }

    public IReadOnlyList<PublicStatement> PublicStatements
    {
        get { lock (_lock) return _statements.ToList(); }
    }

    public IReadOnlyList<Participant> Participants
    {
        get { lock (_lock) return _participants.Values.OrderBy(p => p.JoinedAt).ToList(); }
    }

    public IReadOnlyList<PoliticalCall> PoliticalCalls
    {
        get { lock (_lock) return _politicalCalls.Values.OrderBy(c => c.ReceivedAt).ToList(); }
    }

    public IReadOnlyList<PlumeProjection> PlumeProjections
    {
        get { lock (_lock) return _projections.Values.OrderByDescending(p => p.RunAt).ToList(); }
    }

    public PublicSituation Public
    {
        get { lock (_lock) return _public.Copy(); }
    }

    public IReadOnlyList<MediaEnquiry> MediaEnquiries
    {
        get { lock (_lock) return _enquiries.Values.OrderBy(m => m.ReceivedAt).ToList(); }
    }

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

            ApplyPayload(simEvent.Payload, simEvent.SimTime, simEvent.ActorId);
            LastSequence = simEvent.Sequence;
            AsOf = simEvent.SimTime;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="actorId">Who gave the command that recorded it, when someone did (for the decision log's author).</param>
    private void ApplyPayload(DomainEvent payload, DateTimeOffset at, Guid? actorId = null)
    {
        switch (payload)
        {
            case AgencyRegistered e:
                _agencies[e.AgencyId] = new Agency
                {
                    Id = e.AgencyId, Name = e.Name, ShortName = e.ShortName, Type = e.Type, AiControlled = e.AiControlled,
                    Controller = e.AiControlled ? AgencyController.Simulation : AgencyController.Command,
                    RadioChannel = e.RadioChannel, Jurisdiction = e.Jurisdiction, Priority = e.Priority, PriorityTerms = [.. e.PriorityTerms ?? []],
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

            // ---- Crews and safety (Phase 8) ----

            case CrewRostered e when _units.TryGetValue(e.UnitId, out var crewed):
                Roster(crewed, e.Members, at - e.OnShiftFor, e.ShiftLength);
                break;

            case CrewRelieved e when _units.TryGetValue(e.UnitId, out var relievedUnit):
                var fresh = Roster(relievedUnit, e.Members, at, e.ShiftLength);
                fresh.RelievedAt = at;
                fresh.WorkingSince = relievedUnit.Status == UnitStatus.Operating ? at : null;
                break;

            case CrewConditionReported e when _crews.TryGetValue(e.UnitId, out var reporting):
                reporting.Condition = e.Condition;
                reporting.ConditionNote = e.Note;
                reporting.ConditionReportedAt = at;
                break;

            case CrewRehabOrdered e when _crews.TryGetValue(e.UnitId, out var resting):
                resting.RehabSince = at;
                break;

            case CrewRehabEnded e when _crews.TryGetValue(e.UnitId, out var rested):
                rested.RehabSince = null;
                rested.LastRehabEnded = at;
                rested.WorkingSince = at;
                rested.Condition = CrewCondition.Fine;
                rested.ConditionNote = e.Note;
                rested.ConditionReportedAt = at;
                break;

            case CrewReliefRequested e when _crews.TryGetValue(e.UnitId, out var awaitingRelief):
                awaitingRelief.ReliefRequestedAt = at;
                awaitingRelief.ReliefBriefing = e.FullBriefing;
                break;

            case CrewMemberStoodDown e when _crews.TryGetValue(e.UnitId, out var shortHanded)
                                            && shortHanded.Members.FirstOrDefault(m => m.Id == e.MemberId) is { } stoodDown:
                stoodDown.Status = MemberStatus.StoodDown;
                break;

            case PeerSupportArranged e when _crews.TryGetValue(e.UnitId, out var toSupport):
                toSupport.PeerSupportArrangedAt = at;
                toSupport.PeerSupportGivenAt = null;
                break;

            case PeerSupportGiven e when _crews.TryGetValue(e.UnitId, out var supported):
                supported.PeerSupportGivenAt = at;
                break;

            case ParRequested e:
                StartPar(e.ParId, e.IncidentId, e.Reason, at);
                break;

            case EvacuationSignalled e:
                StartPar(e.SignalId, e.IncidentId, "Evacuation signal", at);
                break;

            case ParReported e when _pars.TryGetValue(e.ParId, out var par):
                par.Responses[e.UnitId] = new ParResponse(e.UnitId, _units.GetValueOrDefault(e.UnitId)?.Callsign ?? "Unknown unit",
                    e.Accounted, e.Expected, e.Missing, at);
                par.Expected.TryAdd(e.UnitId, _units.GetValueOrDefault(e.UnitId)?.Callsign ?? "Unknown unit");
                if (_crews.TryGetValue(e.UnitId, out var counted))
                {
                    foreach (var member in counted.Members.Where(m => m.Status is MemberStatus.OnDuty or MemberStatus.Missing))
                        member.Status = e.Missing.Any(name => Names(member, name)) ? MemberStatus.Missing : MemberStatus.OnDuty;
                }
                break;

            case EmergencyTrafficDeclared e when _channels.TryGetValue(e.ChannelId, out var cleared):
                cleared.EmergencyTraffic = e.Active;
                break;

            // The same Mayday heard again (a repeat call, a relay): keep what is known about it, fill in a clearer account.
            case MaydayDeclared e when _maydays.TryGetValue(e.MaydayId, out var known):
                if (known.Unclear && !e.Unclear)
                {
                    known.Details = e.Details;
                    known.Unclear = false;
                    known.Member ??= e.Member;
                    if (e.Member is { } named && _crews.TryGetValue(e.UnitId, out var namedCrew))
                    {
                        foreach (var member in namedCrew.Members.Where(m => Names(m, named)))
                            member.Status = MemberStatus.InDistress;
                    }
                }
                break;

            case MaydayDeclared e:
                _maydays[e.MaydayId] = new Mayday
                {
                    Id = e.MaydayId, UnitId = e.UnitId, Callsign = _units.GetValueOrDefault(e.UnitId)?.Callsign ?? "Unknown unit",
                    Member = e.Member, IncidentId = _units.GetValueOrDefault(e.UnitId)?.AssignedIncidentId, Details = e.Details,
                    Unclear = e.Unclear, DeclaredAt = at,
                };
                if (e.Member is { } who && _crews.TryGetValue(e.UnitId, out var distressed))
                {
                    foreach (var member in distressed.Members.Where(m => Names(m, who)))
                        member.Status = MemberStatus.InDistress;
                }
                break;

            case RescueTeamDeployed e when _maydays.TryGetValue(e.MaydayId, out var rescue):
                rescue.RescueUnitId = e.UnitId;
                rescue.RescueCallsign = _units.GetValueOrDefault(e.UnitId)?.Callsign;
                rescue.RescueDeployedAt = at;
                if (_crews.TryGetValue(e.UnitId, out var rescuers))
                    rescuers.RescueFor = e.MaydayId;
                break;

            case MaydayResolved e when _maydays.TryGetValue(e.MaydayId, out var resolved):
                resolved.ResolvedAt = at;
                resolved.Outcome = e.Outcome;
                if (resolved.RescueUnitId is { } rescuerId && _crews.TryGetValue(rescuerId, out var rescuerCrew))
                    rescuerCrew.RescueFor = null;
                if (_crews.TryGetValue(resolved.UnitId, out var ownCrew))
                {
                    var hurt = e.Outcome.Contains("paramedic", StringComparison.OrdinalIgnoreCase);
                    var standing = e.Outcome.Contains("stood down", StringComparison.OrdinalIgnoreCase);
                    foreach (var member in ownCrew.Members.Where(m => m.Status is MemberStatus.InDistress or MemberStatus.Missing
                                                                      && (resolved.Member is null || Names(m, resolved.Member))))
                        member.Status = hurt ? MemberStatus.Injured : standing ? MemberStatus.StoodDown : MemberStatus.OnDuty;
                }
                break;

            // ---- Sessions and participants (Phase 11) ----

            case ParticipantJoined e:
                if (_participants.TryGetValue(e.ParticipantId, out var rejoining))
                {
                    rejoining.DisplayName = e.DisplayName;
                    rejoining.Role = e.Role;
                    rejoining.LeftAt = null;
                }
                else
                    _participants[e.ParticipantId] = new Participant { Id = e.ParticipantId, DisplayName = e.DisplayName, Role = e.Role, JoinedAt = at };
                break;

            case ParticipantLeft e when _participants.TryGetValue(e.ParticipantId, out var leaving):
                leaving.LeftAt = at;
                // An agency they ran goes back to the simulation (Phase 11 M5), as the world does.
                foreach (var run in _agencies.Values.Where(a => a.ControlledBy == e.ParticipantId))
                    (run.Controller, run.ControlledBy, run.ControllerName) = (AgencyController.Simulation, null, null);
                break;

            // ---- AI role players (Phase 11 M5) ----

            case ObserverTruthChanged e when _participants.TryGetValue(e.ParticipantId, out var observer):
                observer.TruthGranted = e.Granted;
                break;

            case AgencyControlChanged e when _agencies.TryGetValue(e.AgencyId, out var controlled):
                controlled.Controller = e.Controller;
                controlled.ControlledBy = e.Controller == AgencyController.Participant ? e.ParticipantId : null;
                controlled.ControllerName = e.Controller == AgencyController.Participant ? e.By : null;
                break;

            case PoliticalCallReceived e:
                _politicalCalls[e.CallId] = new PoliticalCall
                {
                    Id = e.CallId, IncidentId = e.IncidentId, Office = e.Office, Caller = e.Caller, Message = e.Message, Pressure = e.Pressure,
                    To = e.To, ReceivedAt = at,
                };
                break;

            case PoliticalCallAnswered e when _politicalCalls.TryGetValue(e.CallId, out var answered):
                answered.AnsweredAt = at;
                answered.Answer = e.Answer;
                break;

            case RoleAssigned e when _participants.TryGetValue(e.ParticipantId, out var assigned):
                assigned.Role = e.Role;
                break;

            case RoleHandedOver e when _participants.TryGetValue(e.ToParticipantId, out var taking):
                taking.Role = e.Role;
                if (_participants.TryGetValue(e.FromParticipantId, out var giving))
                {
                    giving.Role = e.FromNewRole;
                    giving.HandingOver = null;
                }
                break;

            case RoleHandoverBegun e when _participants.TryGetValue(e.FromParticipantId, out var handing):
                handing.HandingOver = new PendingHandover(e.HandoverId, e.ToParticipantId, e.FromNewRole, e.FullBriefing, e.CompletesAt);
                break;

            case RoleHandoverCancelled e:
                foreach (var participant in _participants.Values.Where(p => p.HandingOver?.HandoverId == e.HandoverId))
                    participant.HandingOver = null;
                break;

            // ---- Multi-agency (Phase 10) ----

            case JurisdictionDeclared e when e.Boundary.Count >= 3:
                _jurisdictions[e.JurisdictionId] = new Jurisdiction
                {
                    Id = e.JurisdictionId, Name = e.Name, Level = e.Level, Authority = e.Authority, AuthorityAgencyId = e.AuthorityAgencyId,
                    Boundary = Wgs84.CreatePolygon(e.Boundary), RequiresConsent = e.RequiresConsent, Powers = e.Powers,
                };
                break;

            case MutualAidAgreementRegistered e:
                _agreements[e.AgreementId] = new MutualAidAgreement
                {
                    Id = e.AgreementId, Provider = e.Provider, ProviderType = e.ProviderType, ProviderJurisdiction = e.ProviderJurisdiction,
                    UnitTypes = [.. e.UnitTypes], MaxUnits = e.MaxUnits, ApprovedBy = e.ApprovedBy, DecisionTime = e.DecisionTime,
                    LeadTime = e.LeadTime, NeedsMajorEmergency = e.NeedsMajorEmergency, Terms = e.Terms, ExcludedTasks = [.. e.ExcludedTasks],
                    RadioChannel = e.RadioChannel,
                };
                break;

            case UnifiedCommandInvited e:
                _invitations.RemoveAll(i => i.IncidentId == e.IncidentId && i.AgencyId == e.AgencyId && i.Accepted is null);
                _invitations.Add(new UnifiedCommandInvitation
                {
                    IncidentId = e.IncidentId, AgencyId = e.AgencyId, Agency = _agencies.GetValueOrDefault(e.AgencyId)?.Name ?? "Agency", InvitedAt = at,
                });
                break;

            case UnifiedCommandJoined e:
                if (_invitations.LastOrDefault(i => i.IncidentId == e.IncidentId && i.AgencyId == e.AgencyId) is { } accepted)
                {
                    accepted.Accepted = true;
                    accepted.AnsweredAt = at;
                }
                if (_incidents.TryGetValue(e.IncidentId, out var unified))
                {
                    unified.Command.Apply(e, at);
                    unified.LastUpdatedAt = at;
                }
                break;

            case UnifiedCommandDeclined e when _invitations.LastOrDefault(i => i.IncidentId == e.IncidentId && i.AgencyId == e.AgencyId) is { } declinedInvite:
                declinedInvite.Accepted = false;
                declinedInvite.Reason = e.Reason;
                declinedInvite.AnsweredAt = at;
                break;

            case LeadAgencyChanged e when _incidents.TryGetValue(e.IncidentId, out var led):
                led.Command.Apply(e, at);
                led.LastUpdatedAt = at;
                break;

            case AgencyAssistanceRequested e:
                _assistance[e.RequestId] = new AssistanceRequest
                {
                    Id = e.RequestId, AgencyId = e.AgencyId, Agency = _agencies.GetValueOrDefault(e.AgencyId)?.Name ?? "Agency",
                    IncidentId = e.IncidentId, Quantity = e.Quantity, Task = e.Task, RequestedAt = at,
                };
                break;

            case AgencyAssistanceDecided e when _assistance.TryGetValue(e.RequestId, out var assisted):
                assisted.Granted = e.Granted;
                assisted.UnitIds.AddRange(e.UnitIds);
                assisted.Reason = e.Reason;
                assisted.DecidedAt = at;
                foreach (var released in e.UnitIds)
                    if (_units.TryGetValue(released, out var attached))
                        attached.AttachedToCommand = true;
                break;

            case AgencyRelationChanged e when _agencies.TryGetValue(e.AgencyId, out var related):
                related.Standing = e.Standing;
                break;

            case DisputeRaised e:
                _disputes[e.DisputeId] = new AuthorityDispute
                {
                    Id = e.DisputeId, IncidentId = e.IncidentId, Kind = e.Kind, AgencyId = e.AgencyId, Agency = e.Agency, Subject = e.Subject,
                    Position = e.Position, Basis = e.Basis, RaisedAt = at, ZoneId = e.ZoneId, OrderId = e.OrderId,
                };
                break;

            case DisputeResolutionChosen e when _disputes.TryGetValue(e.DisputeId, out var weighing):
                weighing.Resolution = e.Resolution;
                weighing.ChosenAt = at;
                weighing.Status = e.Resolution switch
                {
                    DisputeResolution.Negotiate => DisputeStatus.Negotiating,
                    DisputeResolution.Escalate => DisputeStatus.Escalated,
                    _ => weighing.Status,
                };
                break;

            case DisputeResolved e when _disputes.TryGetValue(e.DisputeId, out var settled):
                settled.Resolution = e.Resolution;
                settled.Outcome = e.Outcome;
                settled.ResolvedAt = at;
                settled.Status = DisputeStatus.Resolved;
                break;

            case EocActivationRequested e:
                _eoc.Requested = e.Level == _eoc.Level ? null : e.Level;
                _eoc.RequestedAt = at;
                _eoc.Reason = e.Reason;
                break;

            case EocSeatStaffed e:
                if (!_eoc.Seats.Any(s => s.Function == e.Function))
                    _eoc.Seats.Add(new EocSeat
                    {
                        Function = e.Function, Agency = e.Agency, StaffedAt = at,
                        From = EocLevels.Seats.FirstOrDefault(s => s.Function == e.Function)?.From ?? EocLevel.Monitoring,
                    });
                break;

            case EocLevelReached e:
                _eoc.Level = e.Level;
                _eoc.LevelSince = at;
                if (_eoc.Requested == e.Level) _eoc.Requested = null;
                _eoc.Seats.RemoveAll(s => s.From > e.Level);
                if (EocLevels.SitrepInterval(e.Level) is null) _eoc.SitrepDue = null;
                break;

            // ---- Public, media, political & legal (Phase 14) ----
            case VulnerableResidentsReported e:
                // One report per zone and source; a later one from the same source replaces it.
                var register = _public.Vulnerable.FirstOrDefault(v => v.ZoneId == e.ZoneId && v.Source == e.Source);
                if (register is null)
                    _public.Vulnerable.Add(register = new VulnerableReport
                    {
                        ZoneId = e.ZoneId, Source = e.Source,
                        AssistanceRequestedAt = _public.AssistedEvacuations.TryGetValue(e.ZoneId, out var askedEarlier) ? askedEarlier : null,
                    });
                (register.Elderly, register.MobilityImpaired, register.PetOwners, register.Homeless, register.ReportedAt) =
                    (e.Elderly, e.MobilityImpaired, e.PetOwners, e.Homeless, at);
                break;
            case AssistedEvacuationRequested e:
                _public.AssistedEvacuations.TryAdd(e.ZoneId, at);
                foreach (var report in _public.Vulnerable.Where(v => v.ZoneId == e.ZoneId)) report.AssistanceRequestedAt ??= at;
                break;
            case AssistedEvacuationProgress e:
                foreach (var report in _public.Vulnerable.Where(v => v.ZoneId == e.ZoneId)) report.Helped = e.Helped;
                break;
            case VolunteersArrived e:
                if (_public.Volunteers.FirstOrDefault(v => v.IncidentId == e.IncidentId) is { } group)
                    group.Count += e.Count;
                else
                    _public.Volunteers.Add(new VolunteerGroup { IncidentId = e.IncidentId, Count = e.Count, Location = e.Location, ArrivedAt = at });
                break;
            case VolunteersManaged e when _public.Volunteers.FirstOrDefault(v => v.IncidentId == e.IncidentId) is { } managed:
                (managed.Decision, managed.DecidedAt) = (e.Decision, at);
                if (e.Decision == VolunteerDecision.Register && _public.AssistanceCentres.FirstOrDefault() is { } centre) centre.Volunteers += managed.Count;
                break;
            case VolunteersSelfDeployed e when _public.Volunteers.FirstOrDefault(v => v.IncidentId == e.IncidentId) is { } loose:
                loose.SelfDeployed += e.Count;
                break;
            case MissingPersonReported e:
                _public.MissingPersons.Add(new MissingPersonEnquiry
                {
                    Id = e.EnquiryId, IncidentId = e.IncidentId, Caller = e.Caller, Missing = e.Missing, LastKnown = e.LastKnown, ReceivedAt = at,
                });
                break;
            case FamilyAssistanceCentreOpened e:
                _public.AssistanceCentres.Add(new FamilyAssistanceCentre
                {
                    Id = e.CentreId, IncidentId = e.IncidentId, Name = e.Name, Location = e.Location, OpenedAt = at,
                    Volunteers = _public.Volunteers.Where(v => v.Decision == VolunteerDecision.Register).Sum(v => v.Count),
                });
                break;
            case MissingPersonTraced e when _public.MissingPersons.FirstOrDefault(m => m.Id == e.EnquiryId) is { } traced:
                (traced.TracedAt, traced.Outcome, traced.Detail, traced.Supported) = (at, e.Outcome, e.Detail, e.Supported);
                break;
            case RumourSpreading e:
                if (_public.Rumours.FirstOrDefault(r => r.Id == e.RumourId) is { } seen)
                    seen.ReachThousands = e.ReachThousands;
                else
                    _public.Rumours.Add(new Rumour
                    {
                        Id = e.RumourId, IncidentId = e.IncidentId, Claim = e.Claim, Topic = e.Topic, FirstSeenAt = at, ReachThousands = e.ReachThousands,
                    });
                break;
            case RumourRebutted e when _public.Rumours.FirstOrDefault(r => r.Id == e.RumourId) is { } rebutted:
                (rebutted.RebuttedAt, rebutted.Rebuttal) = (at, e.Text);
                break;
            case DenialExposed e when _public.Rumours.FirstOrDefault(r => r.Id == e.RumourId) is { } exposed:
                exposed.DenialExposedAt = at;
                break;
            case PressConferenceHeld e:
                _public.PressConferences.Add(at);
                // Every rumour the statement speaks to is dealt with, as a rebuttal would.
                foreach (var addressed in _public.Rumours.Where(r => r.Spreading && e.Statement.Contains(r.Topic, StringComparison.OrdinalIgnoreCase)))
                    (addressed.RebuttedAt, addressed.Rebuttal) = (at, e.Statement);
                break;
            case DecisionLogged e:
                _public.DecisionLog.Add(new DecisionLogEntry
                {
                    Id = e.EntryId, At = at, Decision = e.Decision, Rationale = e.Rationale, AboutSequence = e.AboutSequence,
                    By = actorId is { } author && _participants.TryGetValue(author, out var writer) ? writer.DisplayName : null,
                });
                break;

            // A plume projection command ran (Phase 13); it replaces the one it was run from.
            case PlumeProjected e:
                if (e.Supersedes is { } replaced && _projections.TryGetValue(replaced, out var old)) old.SupersededAt = at;
                _projections[e.ProjectionId] = new PlumeProjection
                {
                    Id = e.ProjectionId, IncidentId = e.IncidentId, ReleaseLocation = e.ReleaseLocation, Substance = e.Substance,
                    MaterialClass = e.MaterialClass, ReleaseRateKgPerSecond = e.ReleaseRateKgPerSecond, DurationMinutes = e.DurationMinutes,
                    WindFromDegrees = e.WindFromDegrees, WindSpeedMps = e.WindSpeedMps, StabilityClass = e.StabilityClass,
                    Zones = e.Zones, Impacts = e.Impacts, Model = e.Model, Advisory = e.Advisory, Automatic = e.Automatic, RunAt = at,
                };
                break;

            case SitrepRequested e:
                _eoc.SitrepDue = e.DueAt;
                break;

            case SitrepSubmitted:
                _eoc.LastSitrepAt = at;
                _eoc.SitrepsSubmitted++;
                break;

            case PublicStatementIssued e:
                _statements.Add(new PublicStatement
                {
                    Id = e.StatementId, IncidentId = e.IncidentId, Issuer = e.Issuer, Text = e.Text, IssuedAt = at, Joint = e.Joint,
                    Conflicting = e.Conflicting,
                });
                // One of our statements answers what the media were asking about that incident.
                if (!e.Conflicting)
                    foreach (var enquiry in _enquiries.Values.Where(m => m.AnsweredAt is null && (e.IncidentId is null || m.IncidentId == e.IncidentId)))
                    {
                        enquiry.AnsweredAt = at;
                        enquiry.AnsweredBy = e.Issuer;
                    }
                break;

            case MediaEnquiryReceived e:
                _enquiries[e.EnquiryId] = new MediaEnquiry
                {
                    Id = e.EnquiryId, IncidentId = e.IncidentId, Outlet = e.Outlet, Question = e.Question, ReceivedAt = at,
                };
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
                tasked.AttachedToCommand = false; // its own agency has it back
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

            case OrderDeclined e when _orders.TryGetValue(e.OrderId, out var declined):
                declined.Status = OrderStatus.Declined;
                declined.DeclineReason = e.Reason;
                declined.AcknowledgedAt = at;
                if (declined.TargetKind == OrderTargetKind.Unit && declined.TargetId is { } decliningUnit && _units.TryGetValue(decliningUnit, out var decliner))
                    Contact(decliner, at);
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
                decided.Granted = e.Quantity;
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
                unit.AttachedToCommand = false; // a released unit goes back to its own agency
                if (unit.Crew is { } stoodDownCrew)
                {
                    stoodDownCrew.WorkingSince = null;
                    stoodDownCrew.RehabSince = null;
                }
                break;

            case UnitStatusChanged e when _units.TryGetValue(e.UnitId, out var unit):
                Contact(unit, at);
                unit.Status = e.Status;
                if (unit.Crew is { } working)
                {
                    if (e.Status == UnitStatus.Operating && working.RehabSince is null)
                        working.WorkingSince ??= at;
                    else if (e.Status is not (UnitStatus.Operating or UnitStatus.OnScene))
                    {
                        working.WorkingSince = null;
                        working.RehabSince = null;
                    }
                }
                if (e.Status is UnitStatus.Available or UnitStatus.OutOfService)
                {
                    Unassign(unit);
                    unit.Tasking = null;
                    unit.AttachedToCommand = false;
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

    private Crew Roster(Unit unit, IReadOnlyList<CrewMemberInfo> members, DateTimeOffset shiftStart, TimeSpan length)
    {
        var crew = new Crew { UnitId = unit.Id, ShiftStart = shiftStart, ShiftEnd = shiftStart + length };
        crew.Members.AddRange(members.Select(m => new CrewMember
        {
            Id = m.MemberId, Name = m.Name, Role = m.Role, Qualifications = m.Qualifications, Lapsed = m.Lapsed ?? [],
        }));
        if (unit.Status == UnitStatus.Operating) crew.WorkingSince = shiftStart > AsOf ? shiftStart : AsOf;
        _crews[unit.Id] = crew;
        unit.Crew = crew;
        return crew;
    }

    /// <summary>Who should answer: our crews command believes are at the incident's scene.</summary>
    private void StartPar(Guid parId, Guid? incidentId, string reason, DateTimeOffset at)
    {
        var par = new ParCheck { Id = parId, IncidentId = incidentId, Reason = reason, RequestedAt = at };
        foreach (var unit in _units.Values.Where(u => u.Status is UnitStatus.OnScene or UnitStatus.Operating && u.Agency?.AiControlled != true
                                                      && u.AssignedIncidentId is not null && (incidentId is null || u.AssignedIncidentId == incidentId)))
            par.Expected[unit.Id] = unit.Callsign;
        _pars[parId] = par;
    }

    /// <summary>Whether a name on the radio ("Firefighter Byrne") is this member.</summary>
    private static bool Names(CrewMember member, string said) =>
        said.EndsWith(member.Name.Split(' ')[^1], StringComparison.OrdinalIgnoreCase);

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
