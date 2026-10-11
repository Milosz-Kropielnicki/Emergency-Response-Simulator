using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// The after-action review (Design Document §22), worked out entirely from a session's event stream: the decision
/// timeline (every command, with what prompted it, what the trainee knew and what was really happening), performance
/// metrics, resource commitment and delay analytics, branch points comparing each key decision with the recommended
/// response, and the session's outcome.
/// </summary>
/// <remarks>
/// One pass replays the events in order: perceived events rebuild the COP as it stood at each moment (so a decision
/// is judged on what was known then), truth events rebuild what was really happening. Everything is deterministic
/// for a given stream, so the same session always gives the same report.
/// </remarks>
public sealed partial class AarAnalyzer
{
    /// <summary>Reports within this distance of an incident are taken to be about it.</summary>
    public const double ReportMatchMeters = 600;

    private readonly IReadOnlyList<SimEvent> _events;
    private readonly Func<GeoPoint, string?> _placeName;
    private readonly PerceivedState _cop = new();
    private readonly TruthTracker _truth = new();

    // Collected during the pass.
    private readonly Dictionary<Guid, string> _callsigns = [];
    private readonly Dictionary<Guid, Guid?> _unitAgency = [];
    private readonly HashSet<Guid> _aiAgencies = [];
    private readonly Dictionary<Guid, string> _agencyNames = [];
    private readonly Dictionary<Guid, UnitType> _unitTypes = [];
    private readonly Dictionary<Guid, (string Number, GeoPoint Location, DateTimeOffset At)> _incidents = [];
    private readonly List<(DateTimeOffset At, GeoPoint? Location, string Source, string Claim, ReportSource Kind)> _reports = [];
    private readonly List<AarDecision> _decisions = [];
    private readonly List<AarTimelineEntry> _timeline = [];
    private readonly Dictionary<Guid, DateTimeOffset> _firstDispatch = [];

    private AarAnalyzer(IReadOnlyList<SimEvent> events, Func<GeoPoint, string?>? placeName)
    {
        _events = events;
        _placeName = placeName ?? (_ => null);
    }

    /// <param name="events">The whole session, truth and perceived, in sequence order.</param>
    /// <param name="placeName">Names a place (nearest road) for hotspots and delays; coordinates when null.</param>
    public static AarReport Analyze(Guid sessionId, IReadOnlyList<SimEvent> events, string? title = null, Func<GeoPoint, string?>? placeName = null)
    {
        var analyzer = new AarAnalyzer(events, placeName);
        return analyzer.Build(sessionId, title);
    }

    private DateTimeOffset Start => _events.Count > 0 ? _events[0].SimTime : default;
    private DateTimeOffset End => _events.Count > 0 ? _events[^1].SimTime : default;

    private AarReport Build(Guid sessionId, string? title)
    {
        foreach (var simEvent in _events)
            Step(simEvent);

        var usage = Usage();
        var delays = Delays();
        var metrics = Metrics(usage, delays);
        var branches = Branches();
        return new AarReport
        {
            SessionId = sessionId,
            Title = title ?? DefaultTitle(),
            From = Start,
            To = End,
            Decisions = _decisions,
            Timeline = _timeline,
            Units = usage,
            Delays = delays,
            Heatmaps = Heatmaps(usage),
            Metrics = metrics,
            Branches = branches,
            Outcomes = [SessionOutcome()],
            Findings = [.. Findings(metrics, branches), .. HandoverFindings(), .. PoliticalFindings()],
            Participants = Participants(),
            Instructor = _instructor,
        };
    }

    private string DefaultTitle() =>
        _truth.Incidents.Values.OrderBy(i => i.StartedAt).FirstOrDefault() is { } first
            ? $"{EventDescriber.Humanize(first.Type)} exercise"
            : "Training session";

    // ---- The pass ----

    private void Step(SimEvent simEvent)
    {
        var payload = simEvent.Payload;
        var truth = simEvent.Visibility == EventVisibility.Truth;

        // Bookkeeping the analysis needs whichever way the event went.
        switch (payload)
        {
            case AgencyRegistered e:
                _agencyNames[e.AgencyId] = e.Name;
                if (e.AiControlled) _aiAgencies.Add(e.AgencyId);
                break;
            case UnitRegistered e:
                _callsigns[e.UnitId] = e.Callsign;
                _unitAgency[e.UnitId] = e.AgencyId;
                _unitTypes[e.UnitId] = e.Type;
                break;
            case IncidentCreated e:
                _incidents[e.IncidentId] = (e.Number, e.Location, simEvent.SimTime);
                break;
            case CallReceived e when !truth:
                _reports.Add((simEvent.SimTime, e.Location, e.Caller, e.Summary, ReportSource.EmergencyCall));
                break;
            case ReportReceived e when !truth:
                _reports.Add((simEvent.SimTime, e.Location, e.SourceName, e.Claim, e.Source));
                break;
            case PoliticalCallReceived or PoliticalCallAnswered or PoliticalFallout:
                TrackPolitical(simEvent);
                break;
            case InjectFired or InjectHeld or InjectReleased or InjectSkipped or InstructorRolePlay or AiRoleTakenOver or AiRoleHandedBack or InstructorBookmarked:
                TrackInstructor(simEvent);
                break;
            case ParticipantJoined or ParticipantLeft or RoleAssigned or RoleHandedOver:
                TrackParticipant(simEvent);
                break;
        }

        // A decision is judged on the picture just before it was made.
        var decision = IsDecision(simEvent);
        if (decision)
            _decisions.Add(Judge(simEvent) with { ActorId = simEvent.ActorId, Actor = ActorLabel(simEvent) });

        if (truth)
        {
            _truth.Apply(simEvent);
        }
        else
        {
            Collect(simEvent);
            _cop.Apply(simEvent);
        }

        var entry = EventDescriber.Describe(simEvent, id => _callsigns.GetValueOrDefault(id), id => _incidents.TryGetValue(id, out var i) ? i.Number : null,
            id => _agencyNames.GetValueOrDefault(id), id => _people.GetValueOrDefault(id)?.Name);
        var rolePlay = RolePlayOf(payload);
        if (entry is not null)
            _timeline.Add(new AarTimelineEntry(entry.Sequence, entry.SimTime, entry.Category, entry.Text, decision, truth, entry.Important || decision,
                simEvent.ActorId, rolePlay is { } playing ? $"Instructor role-play as {playing}" : ActorLabel(simEvent), rolePlay is not null));
    }

    /// <summary>Something the trainee did (through C2 or the IAP builder), as opposed to the world or the engine.</summary>
    private static bool IsDecision(SimEvent simEvent) =>
        simEvent.Visibility == EventVisibility.Perceived && simEvent.Source is EventSources.C2 or EventSources.Iap
        && simEvent.Payload is not (AlertAcknowledged or IapDraftSaved or IapDraftCreated);

    private bool OwnUnit(Guid unitId) =>
        !(_unitAgency.GetValueOrDefault(unitId) is { } agency && _aiAgencies.Contains(agency));

    private string Callsign(Guid unitId) => _callsigns.GetValueOrDefault(unitId) ?? "unknown unit";

    private string Place(GeoPoint point) => _placeName(point) ?? $"{point.Latitude:F4}, {point.Longitude:F4}";

    public static string Duration(TimeSpan span) => span.TotalSeconds < 0 ? "—"
        : span.TotalMinutes < 1 ? $"{span.TotalSeconds:F0} s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds:D2} s"
        : $"{(int)span.TotalHours} h {span.Minutes:D2} min";

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Good within <paramref name="good"/>, fair within <paramref name="fair"/>, poor after.</summary>
    private static Rating Within(TimeSpan? taken, TimeSpan good, TimeSpan fair) => taken is not { } t ? Rating.NotApplicable
        : t <= good ? Rating.Good : t <= fair ? Rating.Fair : Rating.Poor;

    // ---- Decisions ----

    private AarDecision Judge(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        string? trigger = null;
        TimeSpan? taken = null;
        var rating = Rating.NotApplicable;
        string? note = null;

        void From(DateTimeOffset? when, string what, TimeSpan good, TimeSpan fair)
        {
            if (when is not { } since) return;
            trigger = $"{what} at {Clock(since)}";
            taken = at - since;
            rating = Within(taken, good, fair);
        }

        var (category, text) = Describe(simEvent);
        switch (simEvent.Payload)
        {
            case IncidentCreated e:
                var first = _reports.Where(r => r.At <= at && r.Location is { } l && GeoMath.DistanceMeters(l, e.Location) <= ReportMatchMeters
                                                && !_incidents.Any(i => i.Key != e.IncidentId && i.Value.At < at
                                                                        && GeoMath.DistanceMeters(i.Value.Location, e.Location) <= ReportMatchMeters))
                    .OrderBy(r => r.At).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
                From(first, "First report nearby", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));
                break;

            case PoliticalCallAnswered e when _politicalCalls.TryGetValue(e.CallId, out var rang):
                From(rang.At, $"{rang.Call.Caller} rang", TimeSpan.FromMinutes(3), Systems.PoliticalPressureSystem.AnswerWindow);
                break;

            // Only the first dispatch to an incident is timed: the rest build on it.
            case UnitDispatched e when _incidents.TryGetValue(e.IncidentId, out var incident) && _firstDispatch.TryAdd(e.IncidentId, at):
                From(incident.At, $"{incident.Number} opened", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3));
                break;

            case ApprovalDecided e when _cop.Approvals.FirstOrDefault(a => a.Id == e.ApprovalId) is { } approval:
                From(approval.RequestedAt, $"Request from {approval.RequestedBy}", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
                break;

            case EvacuationSignalled:
                var warning = _reports.Where(r => r.At <= at && r.Kind == ReportSource.FieldUnit && IsCollapseWarning(r.Claim))
                    .Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
                From(warning, "First warning of collapse", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2.5));
                if (_truth.Collapses.FirstOrDefault() is { } collapse && collapse < at)
                {
                    rating = Rating.Poor;
                    note = "After the building had already come down.";
                }
                break;

            case RescueTeamDeployed e when _cop.Maydays.FirstOrDefault(m => m.Id == e.MaydayId) is { } mayday:
                From(mayday.DeclaredAt, $"Mayday from {mayday.Callsign}", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3));
                break;

            case EmergencyTrafficDeclared { Active: true }:
                From(LatestMayday(at), "Mayday", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
                break;

            case ParRequested when LatestMayday(at) is { } maydayAt && at - maydayAt < TimeSpan.FromMinutes(10):
                From(maydayAt, "Mayday", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
                break;

            case MaydayDeclared e:
                var missing = _cop.ParChecks.SelectMany(p => p.Responses.Values).Where(r => r.UnitId == e.UnitId && r.Missing.Count > 0)
                    .Select(r => (DateTimeOffset?)r.At).LastOrDefault();
                From(missing, "Member missing at the PAR", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
                break;

            case CallbackMade e when _cop.MissedCalls.FirstOrDefault(m => m.Id == e.CallId) is { } missed:
                From(missed.At, "Missed 999 call", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
                break;

            case ReadBackConfirmed e when _cop.Orders.FirstOrDefault(o => o.Id == e.OrderId) is { AcknowledgedAt: { } readBack } order:
                From(readBack, $"Read-back from {order.TargetName}", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
                break;

            case ChannelPatchRequested:
                var nudge = _reports.Where(r => r.At <= at && r.Claim.Contains("can't raise you", StringComparison.OrdinalIgnoreCase))
                    .Select(r => (DateTimeOffset?)r.At).LastOrDefault();
                From(nudge, "Request for a patch", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
                break;

            case ZoneDeclared { Type: ZoneType.HotZone or ZoneType.WarmZone or ZoneType.ColdZone or ZoneType.PlumeLow or ZoneType.PlumeModerate or ZoneType.PlumeHigh } zone
                when !_decisions.Any(d => d.Category == "Zone" && d.Trigger?.Contains("chemical") == true):
                var chemical = _reports.Where(r => r.At <= at && IsChemicalReport(r.Claim)).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
                From(chemical, "First report of chemicals", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6));
                break;

            case CrewRehabOrdered e when _cop.FindUnit(e.UnitId)?.Crew is { } crew:
                var said = crew.ConditionReportedAt is { } reported && crew.Condition is CrewCondition.Exhausted or CrewCondition.Tired ? reported : (DateTimeOffset?)null;
                if (said is not null)
                    From(said, $"{Callsign(e.UnitId)} said it was {crew.Condition.ToString().ToLowerInvariant()}", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
                else if (crew.WorkingSince is { } working)
                    note = $"After {Duration(at - working)} on task.";
                break;

            case CrewReliefRequested e when _cop.FindUnit(e.UnitId)?.Crew is { } crew:
                if (crew.PastShiftEnd(at)) From(crew.ShiftEnd, $"{Callsign(e.UnitId)}'s shift ended", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));
                if (!e.FullBriefing) note = "Quick changeover: information may have been lost.";
                break;

            // ---- Multi-agency (Phase 10) ----

            case IcsPositionAssigned { Role: IcsRole.Liaison or IcsRole.PublicInformation } e when _incidents.TryGetValue(e.IncidentId, out var staffedFor):
                From(staffedFor.At, $"{staffedFor.Number} opened", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20));
                break;

            case UnifiedCommandInvited e when _incidents.TryGetValue(e.IncidentId, out var shared)
                                              && !_decisions.Any(d => d.Category == "Multi-agency" && d.Decision.Contains("invited")):
                From(shared.At, $"{shared.Number} opened", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20));
                break;

            case EocActivationRequested e when e.Level > _cop.Eoc.Level:
                From(EocWarrantedAt(at), "Incident became a major emergency", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));
                if (e.Level == EocLevel.Full && _cop.Incidents.All(i => i.Priority < IncidentPriority.High))
                {
                    rating = Rating.Fair;
                    note = "Full activation (12 staff, overtime) for an incident never above Medium priority.";
                }
                break;

            case DisputeResolutionChosen e when _cop.Disputes.FirstOrDefault(d => d.Id == e.DisputeId) is { } dispute:
                From(dispute.RaisedAt, $"Objection from {dispute.Agency}", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(8));
                if (e.Resolution == DisputeResolution.Assert)
                {
                    rating = rating == Rating.Poor ? Rating.Poor : Rating.Fair;
                    note = $"Overruled {dispute.Agency}: quick, but relations suffered.";
                }
                else if (e.Resolution == DisputeResolution.Concede && dispute.Kind == DisputeKind.LeadAgency && _truth.FireBurning.Values.Sum() > 0)
                {
                    rating = Rating.Poor;
                    note = "Handed the lead over while the fire still burned: firefighting went defensive.";
                }
                break;

            case SitrepSubmitted when All<SitrepRequested>().Where(x => x.Event.SimTime <= at).Select(x => x.Payload).LastOrDefault() is { } asked:
                trigger = $"EOC wanted it by {Clock(asked.DueAt)}";
                rating = at <= asked.DueAt ? Rating.Good : at - asked.DueAt <= TimeSpan.FromMinutes(10) ? Rating.Fair : Rating.Poor;
                break;

            case PublicStatementIssued e when Interagency.IsWarning(e.Text):
                var zoneAt = All<ZoneDeclared>().Where(x => x.Event.SimTime <= at && x.Payload.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace)
                    .Select(x => (DateTimeOffset?)x.Event.SimTime).LastOrDefault();
                From(zoneAt, "Protective zone declared", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
                if (!e.Joint) note = "Not agreed through a joint information centre: other agencies may say something different.";
                break;

            case OrderIssued e when Qualifications.RequiredFor(e.Text) is { } needed && e.TargetKind == OrderTargetKind.Unit
                                    && e.TargetId is { } target && _cop.FindUnit(target)?.Crew is { } crew && !crew.Meets(needed):
                rating = Rating.Poor;
                note = $"{e.TargetName} has nobody {Qualifications.Name(needed.Qualification).ToLowerInvariant()} qualified (needs {needed.Members}).";
                break;
        }

        return new AarDecision(simEvent.Sequence, at, category, text, trigger, taken, Known(at), _truth.Describe(at), rating, note);
    }

    private DateTimeOffset? LatestMayday(DateTimeOffset at) =>
        _cop.Maydays.Where(m => m.DeclaredAt <= at).Select(m => (DateTimeOffset?)m.DeclaredAt).LastOrDefault();

    public static bool IsCollapseWarning(string claim) =>
        claim.Contains("Roof", StringComparison.OrdinalIgnoreCase) && claim.Contains("out", StringComparison.OrdinalIgnoreCase);

    public static bool IsChemicalReport(string claim) =>
        new[] { "chlorine", "chemical", "hazmat", "fumes", "placard" }.Any(w => claim.Contains(w, StringComparison.OrdinalIgnoreCase));

    private (string Category, string Text) Describe(SimEvent simEvent)
    {
        var entry = EventDescriber.Describe(simEvent, id => _callsigns.GetValueOrDefault(id), id => _incidents.TryGetValue(id, out var i) ? i.Number : null,
            id => _agencyNames.GetValueOrDefault(id), id => _people.GetValueOrDefault(id)?.Name);
        var category = simEvent.Payload switch
        {
            IncidentCreated or IncidentUpdated or IncidentCommanderAssigned => "Incident",
            UnitDispatched or UnitDispatchCancelled or UnitStatusChanged or ResourceRequested => "Dispatch",
            ZoneDeclared or ZoneLifted => "Zone",
            OrderIssued or OrderClosed or ReadBackConfirmed => "Order",
            ApprovalDecided or NotificationSent => "Liaison",
            ReportAssessed or ReportLinked => "Assessment",
            RadioCallMade or RepeatRequested or UnitChannelAssigned or ChannelPatchRequested or ChannelPatchRemoved or CallbackMade => "Comms",
            ParRequested or EvacuationSignalled or EmergencyTrafficDeclared or MaydayDeclared or RescueTeamDeployed => "Safety",
            CrewRehabOrdered or CrewReliefRequested or PeerSupportArranged => "Crews",
            UnifiedCommandInvited or AgencyAssistanceRequested or AgencyBriefingRequested or DisputeResolutionChosen or EocActivationRequested
                or SitrepSubmitted or PublicStatementIssued or AgencyRequestAnswered => "Multi-agency",
            PoliticalCallAnswered => "Political",
            IcsPositionAssigned { Role: IcsRole.Liaison or IcsRole.PublicInformation } => "Multi-agency",
            IcsPositionAssigned or IcsGroupFormed or IcsGroupDisbanded or UnitAssignedToGroup => "Command",
            _ when simEvent.Source == EventSources.Iap => "Planning",
            _ => "Other",
        };
        return (category, entry?.Text ?? simEvent.Type);
    }

    /// <summary>What the COP showed at the moment of a decision, in a line.</summary>
    private string Known(DateTimeOffset at)
    {
        var own = _cop.Units.Where(u => u.Agency?.AiControlled != true).ToList();
        var committed = own.Count(u => UnitStatusRules.IsCommitted(u.Status));
        var incidents = _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).ToList();
        var casualties = incidents.Sum(i => Math.Max(i.CasualtiesReported, i.CasualtiesConfirmed));
        var alerts = _cop.Alerts.Count(a => a.AcknowledgedAt is null);
        var parts = new List<string>
        {
            incidents.Count == 0 ? $"No incident open; {_reports.Count(r => r.At <= at)} reports in" : $"{incidents.Count} incident(s) open",
            $"{committed} of {own.Count} units committed",
        };
        if (casualties > 0) parts.Add($"{casualties} casualties reported");
        if (_cop.Maydays.Any(m => m.Active)) parts.Add($"{_cop.Maydays.Count(m => m.Active)} Mayday active");
        if (alerts > 0) parts.Add($"{alerts} alerts unacknowledged");
        return string.Join("; ", parts);
    }

    // ---- Truth, as it stood at each moment ----

    /// <summary>What was really happening, rebuilt from truth events as the pass goes.</summary>
    private sealed class TruthTracker
    {
        public Dictionary<Guid, (IncidentType Type, GeoPoint Location, DateTimeOffset StartedAt, double Severity, int Casualties)> Incidents { get; } = [];
        public Dictionary<Guid, (Triage Triage, CasualtyState State, DateTimeOffset InjuredAt, string Cause, GeoPoint Location)> Casualties { get; } = [];
        public List<(DateTimeOffset At, Guid CasualtyId, Triage From, Triage To)> Worsened { get; } = [];
        public Dictionary<Guid, double> FireBurning { get; } = [];
        public double PeakFire { get; private set; }
        public List<DateTimeOffset> Collapses { get; } = [];
        public Dictionary<Guid, (Guid UnitId, DateTimeOffset At, double Air, string Cause)> Distress { get; } = [];
        public List<(DateTimeOffset At, Guid Id, string Outcome)> DistressEnds { get; } = [];
        public List<(DateTimeOffset At, string From, string Reason)> Lost { get; } = [];

        public void Apply(SimEvent simEvent)
        {
            var at = simEvent.SimTime;
            switch (simEvent.Payload)
            {
                case WorldIncidentStarted e:
                    Incidents[e.WorldIncidentId] = (e.Type, e.Location, at, e.Severity, e.ActualCasualties);
                    break;
                case WorldIncidentChanged e when Incidents.TryGetValue(e.WorldIncidentId, out var incident):
                    Incidents[e.WorldIncidentId] = incident with { Severity = e.Severity, Casualties = Math.Max(incident.Casualties, e.ActualCasualties) };
                    break;
                case CasualtyInjured e:
                    Casualties.TryAdd(e.CasualtyId, (e.Triage, e.Triage == Triage.Deceased ? CasualtyState.Deceased : CasualtyState.AwaitingTreatment, at, e.Cause, e.Location));
                    break;
                case CasualtyChanged e when Casualties.TryGetValue(e.CasualtyId, out var casualty):
                    if (e.Triage < casualty.Triage || e.Triage == Triage.Deceased && casualty.Triage != Triage.Deceased)
                        Worsened.Add((at, e.CasualtyId, casualty.Triage, e.Triage));
                    Casualties[e.CasualtyId] = casualty with { Triage = e.Triage, State = e.State };
                    break;
                case HazardStarted { Kind: HazardKind.Fire } e:
                    FireBurning.TryAdd(e.HazardId, 0);
                    break;
                case HazardFootprintChanged e when FireBurning.ContainsKey(e.HazardId):
                    FireBurning[e.HazardId] = e.Intensity;
                    PeakFire = Math.Max(PeakFire, FireBurning.Values.Sum());
                    break;
                case HazardEnded e when FireBurning.ContainsKey(e.HazardId):
                    FireBurning[e.HazardId] = 0;
                    break;
                case StructureCollapsed:
                    Collapses.Add(at);
                    break;
                case FirefighterInDistress e:
                    Distress[e.DistressId] = (e.UnitId, at, e.AirMinutes, e.Cause);
                    break;
                case DistressEnded e:
                    DistressEnds.Add((at, e.DistressId, e.Outcome));
                    break;
                case TransmissionLost e:
                    Lost.Add((at, e.From, e.Reason));
                    break;
            }
        }

        public int Hurt => Casualties.Count;
        public int Dead => Casualties.Values.Count(c => c.Triage == Triage.Deceased);

        public string Describe(DateTimeOffset at)
        {
            var parts = new List<string>();
            var burning = FireBurning.Values.Sum();
            if (burning > 0) parts.Add($"fire burning over {burning:N0} m²");
            var alive = Casualties.Values.Where(c => c.Triage != Triage.Deceased).ToList();
            if (Casualties.Count > 0)
                parts.Add($"{alive.Count} hurt ({alive.Count(c => c.State == CasualtyState.AwaitingTreatment)} untreated), {Dead} dead");
            else if (Incidents.Values.Sum(i => i.Casualties) is > 0 and var declared)
                parts.Add($"{declared} hurt");
            var trapped = Distress.Keys.Count(id => DistressEnds.All(d => d.Id != id));
            if (trapped > 0) parts.Add($"{trapped} firefighter(s) in distress");
            if (Collapses.Count > 0) parts.Add("building has partly collapsed");
            return parts.Count == 0 ? (Incidents.Count == 0 ? "Nothing happening yet" : "Incident under way") : char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts)[1..];
        }
    }
}
