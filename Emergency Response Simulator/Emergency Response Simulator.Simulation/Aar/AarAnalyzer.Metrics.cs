using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Performance metrics (Design Document §22: time to dispatch, resource utilisation, communication errors, objective
/// completion), plus safety, command and outcome measures.
/// </summary>
public sealed partial class AarAnalyzer
{
    private IEnumerable<(SimEvent Event, T Payload)> All<T>() where T : DomainEvent =>
        _events.Where(e => e.Payload is T).Select(e => (e, (T)e.Payload));

    private static string Percent(double share) => $"{share:P0}";

    private List<AarMetric> Metrics(IReadOnlyList<AarUnitUsage> usage, IReadOnlyList<AarDelay> delays)
    {
        var metrics = new List<AarMetric>();
        void Add(string category, string name, double? value, string display, string standard, Rating rating, string explanation) =>
            metrics.Add(new AarMetric(category, name, value, display, standard, rating, explanation));

        // ---- Dispatch ----
        var opened = All<IncidentCreated>().Where(x => x.Event.Source == EventSources.C2).ToList();
        if (opened.Count == 0)
        {
            Add("Dispatch", "Incident opened", null, "None", "Open an incident for each real emergency",
                _truth.Incidents.Count > 0 && _reports.Count > 0 ? Rating.Poor : Rating.NotApplicable,
                _truth.Incidents.Count > 0 ? "Reports came in but no incident was opened, so nothing was dispatched." : "No emergency in this session.");
        }
        else
        {
            var toOpen = opened.Select(x => FirstReport(x.Payload.Location, x.Event.SimTime) is { } first ? x.Event.SimTime - first : (TimeSpan?)null)
                .OfType<TimeSpan>().ToList();
            if (toOpen.Count > 0)
            {
                var mean = TimeSpan.FromSeconds(toOpen.Average(t => t.TotalSeconds));
                Add("Dispatch", "First report to incident opened", mean.TotalSeconds, Duration(mean), "under 2 min",
                    Within(mean, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)), $"Average over {toOpen.Count} incident(s).");
            }

            var toDispatch = opened.Where(x => _firstDispatch.ContainsKey(x.Payload.IncidentId))
                .Select(x => _firstDispatch[x.Payload.IncidentId] - x.Event.SimTime).ToList();
            var undispatched = opened.Count(x => !_firstDispatch.ContainsKey(x.Payload.IncidentId));
            if (toDispatch.Count > 0)
            {
                var mean = TimeSpan.FromSeconds(toDispatch.Average(t => t.TotalSeconds));
                Add("Dispatch", "Incident to first dispatch", mean.TotalSeconds, Duration(mean), "under 1 min",
                    undispatched > 0 ? Rating.Poor : Within(mean, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)),
                    undispatched > 0 ? $"{undispatched} incident(s) never had anything dispatched." : $"Average over {toDispatch.Count} incident(s).");
            }

            var onScene = opened.Select(x => FirstOnScene(x.Payload.IncidentId) is { } arrived && FirstReport(x.Payload.Location, x.Event.SimTime) is { } first
                ? arrived - first : (TimeSpan?)null).OfType<TimeSpan>().ToList();
            if (onScene.Count > 0)
            {
                var mean = TimeSpan.FromSeconds(onScene.Average(t => t.TotalSeconds));
                Add("Dispatch", "First report to first unit on scene", mean.TotalSeconds, Duration(mean), "under 10 min",
                    Within(mean, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)), "Includes travel; road blockages and breakdowns count against it.");
            }
        }
        if (_truth.Incidents.Values.OrderBy(i => i.StartedAt).FirstOrDefault() is { } real
            && _reports.Where(r => r.At >= real.StartedAt).Select(r => (DateTimeOffset?)r.At).FirstOrDefault() is { } heard)
        {
            Add("Dispatch", "Incident to first report reaching control", (heard - real.StartedAt).TotalSeconds, Duration(heard - real.StartedAt),
                "context", Rating.NotApplicable, "How long the emergency went unreported. Not in the trainee's hands.");
        }

        // ---- Resources ----
        var firstIncident = opened.Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        if (usage.Count > 0 && firstIncident is { } since && End > since)
        {
            var window = (End - since).TotalSeconds;
            var used = usage.Sum(u => u.Spans.Where(s => UnitStatusRules.IsCommitted(s.Status))
                .Sum(s => Math.Max(0, ((s.To < End ? s.To : End) - (s.From > since ? s.From : since)).TotalSeconds)));
            var share = used / (window * usage.Count);
            Add("Resources", "Resource utilisation", share, Percent(share), "context", Rating.NotApplicable,
                $"Share of your {usage.Count} units' time spent committed since the first incident opened. Peak: {PeakCommitted(usage)} at once.");
            var idle = usage.Count(u => u.Committed == 0);
            Add("Resources", "Units never committed", idle, $"{idle} of {usage.Count}", "context", Rating.NotApplicable,
                "Held in reserve, or forgotten: check the commitment chart.");
        }
        if (delays.Count > 0)
        {
            var lost = TimeSpan.FromSeconds(delays.Sum(d => d.Lost.TotalSeconds));
            Add("Resources", "Time lost en route", lost.TotalSeconds, Duration(lost), "context", Rating.NotApplicable,
                $"{delays.Count} hold-up(s); the worst: {delays[0].Callsign} {Duration(delays[0].Lost)} ({delays[0].Cause}) at {delays[0].Where}.");
        }

        var longest = LongestOnTask();
        if (longest is { } stint && stint.Span >= TimeSpan.FromMinutes(20))
        {
            Add("Resources", "Longest spell working without rehab", stint.Span.TotalMinutes, $"{Duration(stint.Span)} ({Callsign(stint.Unit)})",
                "under 45 min", Within(stint.Span, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(60)),
                "Crews tire fast working inside; rotate them through rehab after about 40 minutes.");
        }
        var overruns = ShiftOverruns();
        if (overruns is { } over)
        {
            Add("Resources", "Crews kept on past their shift without relief", over.Count, over.Count == 0 ? "None" : string.Join(", ", over),
                "none", over.Count == 0 ? Rating.Good : over.Count == 1 ? Rating.Fair : Rating.Poor,
                "Committed more than 30 minutes past shift end with no relief crew asked for.");
        }

        // ---- Communications ----
        var heardRadio = All<CommsLogged>().Where(x => !x.Payload.FromControl && x.Payload.ChannelId is not (RadioPlan.Calls or RadioPlan.Chat)).ToList();
        var unclear = heardRadio.Where(x => x.Payload.Quality != CommsQuality.Clear).ToList();
        if (heardRadio.Count > 0)
        {
            Add("Communications", "Field messages heard broken or garbled", unclear.Count, $"{unclear.Count} of {heardRadio.Count}", "context",
                Rating.NotApplicable, "Busy channels, weak batteries, black spots and tired crews.");
        }
        if (unclear.Count > 0)
        {
            var repeats = All<RepeatRequested>().Select(x => x.Payload.MessageId).ToHashSet();
            var asked = unclear.Count(x => repeats.Contains(x.Payload.MessageId));
            var share = (double)asked / unclear.Count;
            Add("Communications", "\"Say again\" on unclear messages", share, $"{asked} of {unclear.Count}", "at least half",
                share >= 0.5 ? Rating.Good : share >= 0.25 ? Rating.Fair : Rating.Poor, "An unclear message left alone is information lost.");
        }
        var lostCount = _truth.Lost.Count;
        if (lostCount > 0)
        {
            var reasons = string.Join(", ", _truth.Lost.GroupBy(l => l.Reason).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count()} {g.Key}"));
            Add("Communications", "Transmissions never heard", lostCount, lostCount.ToString(), "context", Rating.NotApplicable, $"Ground truth: {reasons}.");
        }
        var garbledAcks = All<OrderAcknowledged>().Where(x => x.Payload.Garbled).Select(x => x.Payload.OrderId).Distinct().ToList();
        if (garbledAcks.Count > 0)
        {
            var settled = All<ReadBackConfirmed>().Select(x => x.Payload.OrderId).ToHashSet();
            var share = (double)garbledAcks.Count(settled.Contains) / garbledAcks.Count;
            Add("Communications", "Unclear read-backs resolved", share, $"{garbledAcks.Count(settled.Contains)} of {garbledAcks.Count}", "at least 80 %",
                share >= 0.8 ? Rating.Good : share >= 0.5 ? Rating.Fair : Rating.Poor, "Closed loop: confirm the read-back or repeat the order.");
        }
        var orders = _cop.Orders;
        if (orders.Count > 0)
        {
            var unanswered = orders.Count(o => o.Status == OrderStatus.Issued && End - o.IssuedAt > TimeSpan.FromMinutes(2));
            Add("Communications", "Orders never read back", unanswered, $"{unanswered} of {orders.Count}", "none",
                unanswered == 0 ? Rating.Good : unanswered == 1 ? Rating.Fair : Rating.Poor, "Sent but never acknowledged, and not followed up.");
            var declined = orders.Count(o => o.Status == OrderStatus.Declined);
            Add("Communications", "Orders a crew wasn't qualified for", declined, declined.ToString(), "none",
                declined == 0 ? Rating.Good : declined == 1 ? Rating.Fair : Rating.Poor, "Check qualifications before tasking: Hazmat, USAR, Swiftwater, BA, ALS.");
        }
        var ownCalls = All<CommsLogged>().Where(x => x.Payload.FromControl && x.Payload.ChannelId is not (RadioPlan.Calls or RadioPlan.Chat)).ToList();
        var keyed = All<RadioCallMade>().Count();
        if (keyed > 0)
        {
            var noted = ownCalls.Count(x => x.Payload.Notes is { Count: > 0 });
            var share = (double)noted / keyed;
            Add("Communications", "Radio discipline", 1 - share, $"{keyed - noted} of {keyed} clean", "90 % clean",
                share <= 0.1 ? Rating.Good : share <= 0.3 ? Rating.Fair : Rating.Poor,
                "Call sign first, plain language, under 30 words, wait for a clear channel, hold the button until done.");
        }
        var missed = _cop.MissedCalls;
        if (missed.Count > 0)
        {
            var back = missed.Count(m => m.CalledBackAt is { } called && called - m.At <= TimeSpan.FromMinutes(3));
            var share = (double)back / missed.Count;
            Add("Communications", "Missed 999 calls rung back within 3 min", share, $"{back} of {missed.Count}", "all",
                share >= 0.9 ? Rating.Good : share >= 0.5 ? Rating.Fair : Rating.Poor, "A caller who hung up may still need help.");
        }

        // ---- Command and planning ----
        var alerts = _cop.Alerts;
        if (alerts.Count > 0)
        {
            var acked = alerts.Where(a => a.AcknowledgedAt is not null).ToList();
            var share = (double)acked.Count / alerts.Count;
            var mean = acked.Count == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(acked.Average(a => (a.AcknowledgedAt!.Value - a.RaisedAt).TotalSeconds));
            Add("Command", "Alerts acknowledged", share, $"{acked.Count} of {alerts.Count}", "at least 80 %",
                share >= 0.8 ? Rating.Good : share >= 0.5 ? Rating.Fair : Rating.Poor,
                mean is { } m ? $"Average {Duration(m)} to acknowledge." : "None acknowledged.");
        }
        var approvals = _cop.Approvals;
        if (approvals.Count > 0)
        {
            var decided = approvals.Where(a => a.DecidedAt is not null).ToList();
            var waiting = approvals.Count - decided.Count;
            var mean = decided.Count == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(decided.Average(a => (a.DecidedAt!.Value - a.RequestedAt).TotalSeconds));
            Add("Command", "Time to decide requests", mean?.TotalSeconds, mean is { } m ? Duration(m) : "never decided", "under 2 min",
                waiting > 0 ? Rating.Poor : Within(mean, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)),
                waiting > 0 ? $"{waiting} request(s) never decided." : $"{decided.Count} request(s).");
        }
        var plans = _cop.ActionPlans.Where(p => p.Status is IapStatus.Approved or IapStatus.Superseded).ToList();
        var objectives = plans.Where(p => p.Status == IapStatus.Approved).SelectMany(p => p.Content.OperationalObjectives).ToList();
        if (objectives.Count > 0)
        {
            var progress = _cop.ObjectiveProgress;
            var achieved = objectives.Count(o => progress.GetValueOrDefault(o.Id) == ObjectiveStatus.Achieved);
            var share = (double)achieved / objectives.Count;
            Add("Command", "IAP objectives achieved", share, $"{achieved} of {objectives.Count}", "at least 75 %",
                share >= 0.75 ? Rating.Good : share >= 0.4 ? Rating.Fair : Rating.Poor, "Operational objectives in the plan in force, marked achieved.");
        }
        else if (opened.Count > 0)
        {
            var big = _cop.Incidents.Any(i => i.AssignedUnits.Count >= 4) || usage.Count(u => u.Committed > 0) >= 4;
            Add("Command", "Incident Action Plan", 0, plans.Count > 0 ? "No objectives" : "None approved", "an approved plan with objectives",
                big && Duration() >= TimeSpan.FromMinutes(30) ? Rating.Poor : Rating.NotApplicable,
                "A major incident running for half an hour needs an approved IAP with measurable objectives.");
        }
        if (opened.Count > 0)
        {
            var spans = _cop.Alerts.Count(a => a.Title.Contains("direct reports"));
            Add("Command", "Span of control exceeded", spans, spans.ToString(), "never", spans == 0 ? Rating.Good : spans == 1 ? Rating.Fair : Rating.Poor,
                "More than 7 direct reports slows orders and loses some. Form groups or staff Operations.");
        }

        // ---- Safety ----
        if (ParGap() is { } gap)
        {
            Add("Safety", "Longest gap between PARs", gap.TotalMinutes, Duration(gap), "under 25 min",
                Within(gap, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(35)), "While two or more crews were working; PARs every 20 minutes.");
        }
        var maydays = _cop.Maydays;
        if (maydays.Count > 0)
        {
            var sent = maydays.Where(m => m.RescueDeployedAt is not null).ToList();
            var mean = sent.Count == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(sent.Average(m => (m.RescueDeployedAt!.Value - m.DeclaredAt).TotalSeconds));
            Add("Safety", "Mayday to rescue team", mean?.TotalSeconds, mean is { } m ? Duration(m) : "never sent", "under 1 min",
                sent.Count < maydays.Count ? Rating.Poor : Within(mean, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)),
                $"{maydays.Count} Mayday(s), {sent.Count} with a rescue team.");
            var cleared = maydays.Count(m => All<EmergencyTrafficDeclared>().Any(x => x.Payload.Active && x.Event.SimTime >= m.DeclaredAt
                                                                                      && x.Event.SimTime - m.DeclaredAt <= TimeSpan.FromMinutes(2)));
            Add("Safety", "Emergency traffic declared for Maydays", cleared, $"{cleared} of {maydays.Count}", "all, within 2 min",
                cleared == maydays.Count ? Rating.Good : cleared > 0 ? Rating.Fair : Rating.Poor, "Clear the channel so the Mayday and the rescue can be heard.");
        }
        if (_truth.Distress.Count > 0)
        {
            var hurt = FirefightersHurt();
            Add("Safety", "Firefighters in distress", _truth.Distress.Count, $"{_truth.Distress.Count} ({hurt} hurt)", "none", Rating.NotApplicable,
                "Ground truth: trapped, lost or out of air.");
        }

        // ---- Outcome (ground truth: context for the branch comparison) ----
        if (_truth.Hurt > 0)
        {
            var untreated = UntreatedDeaths();
            Add("Outcome", "Casualties", _truth.Hurt, $"{_truth.Hurt} hurt, {_truth.Dead} dead", "context", Rating.NotApplicable,
                untreated > 0 ? $"{untreated} died before anyone treated them." : "Nobody died waiting for treatment.");
        }
        if (_truth.PeakFire > 0)
        {
            Add("Outcome", "Largest area burning at once", _truth.PeakFire, $"{_truth.PeakFire:N0} m²", "context", Rating.NotApplicable,
                "Ground truth from the fire model.");
        }

        return metrics;
    }

    private TimeSpan Duration() => End - Start;

    /// <summary>The first call or report from near a place, up to a time.</summary>
    private DateTimeOffset? FirstReport(GeoPoint location, DateTimeOffset before) =>
        _reports.Where(r => r.At <= before && r.Location is { } l && GeoMath.DistanceMeters(l, location) <= ReportMatchMeters)
            .Select(r => (DateTimeOffset?)r.At).FirstOrDefault();

    /// <summary>When the first unit dispatched to an incident reported on scene.</summary>
    private DateTimeOffset? FirstOnScene(Guid incidentId)
    {
        var dispatched = All<UnitDispatched>().Where(x => x.Payload.IncidentId == incidentId).Select(x => x.Payload.UnitId).ToHashSet();
        return All<UnitStatusChanged>().Where(x => x.Payload.Status == UnitStatus.OnScene && dispatched.Contains(x.Payload.UnitId))
            .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
    }

    private static int PeakCommitted(IReadOnlyList<AarUnitUsage> usage)
    {
        var changes = usage.SelectMany(u => u.Spans.Where(s => UnitStatusRules.IsCommitted(s.Status)))
            .SelectMany(s => new[] { (At: s.From, Delta: 1), (At: s.To, Delta: -1) })
            .OrderBy(c => c.At).ThenBy(c => c.Delta);
        int current = 0, peak = 0;
        foreach (var change in changes)
            peak = Math.Max(peak, current += change.Delta);
        return peak;
    }

    /// <summary>The longest unbroken spell any of our crews spent operating (rehab breaks it: the crew reports on scene).</summary>
    private (Guid Unit, TimeSpan Span)? LongestOnTask() =>
        _statuses.Where(s => OwnUnit(s.Key))
            .SelectMany(s => s.Value.Select((change, i) => (Unit: s.Key, change.Status,
                Span: (i + 1 < s.Value.Count ? s.Value[i + 1].At : End) - change.At)))
            .Where(x => x.Status == UnitStatus.Operating)
            .OrderByDescending(x => x.Span)
            .Select(x => ((Guid, TimeSpan)?)(x.Unit, x.Span))
            .FirstOrDefault();

    /// <summary>Our crews still committed 30 minutes after their shift ended with no relief asked for; null if no shift ended.</summary>
    private List<string>? ShiftOverruns()
    {
        var ends = new Dictionary<Guid, DateTimeOffset>();
        foreach (var (e, rostered) in All<CrewRostered>()) ends[rostered.UnitId] = e.SimTime - rostered.OnShiftFor + rostered.ShiftLength;
        var relief = All<CrewReliefRequested>().ToList();
        var relieved = All<CrewRelieved>().ToList();
        var overruns = new List<string>();
        var any = false;
        foreach (var (unitId, end) in ends.Where(e => OwnUnit(e.Key)))
        {
            var check = end + TimeSpan.FromMinutes(30);
            if (check > End) continue;
            if (relieved.Any(r => r.Payload.UnitId == unitId && r.Event.SimTime <= check)) continue;
            if (!UnitStatusRules.IsCommitted(StatusAt(unitId, check))) continue;
            any = true;
            if (!relief.Any(r => r.Payload.UnitId == unitId && r.Event.SimTime <= check))
                overruns.Add(Callsign(unitId));
        }
        return any ? overruns : null;
    }

    /// <summary>The longest stretch with two or more of our fire crews working and no PAR; null if that never lasted 20 minutes.</summary>
    private TimeSpan? ParGap()
    {
        var pars = All<ParRequested>().Select(x => x.Event.SimTime).Concat(All<EvacuationSignalled>().Select(x => x.Event.SimTime)).Order().ToList();
        var fireCrews = _statuses.Keys.Where(u => OwnUnit(u) && ResourceGroups.AgencyFor(_unitTypes.GetValueOrDefault(u)) == AgencyType.Fire).ToList();
        TimeSpan? worst = null;
        DateTimeOffset? since = null;
        for (var t = Start; t <= End; t += TimeSpan.FromSeconds(30))
        {
            var working = fireCrews.Count(u => StatusAt(u, t) == UnitStatus.Operating) >= 2;
            if (working) since ??= t;
            else since = null;
            if (since is not { } from) continue;
            var lastPar = pars.LastOrDefault(p => p <= t);
            var gap = t - (lastPar > from ? lastPar : from);
            if (gap >= TimeSpan.FromMinutes(20) && (worst is null || gap > worst)) worst = gap;
        }
        return worst;
    }

    private int FirefightersHurt() =>
        _truth.DistressEnds.Count(d => d.Outcome.Contains("paramedic", StringComparison.OrdinalIgnoreCase));

    /// <summary>Casualties who died while still waiting for anyone to treat them.</summary>
    private int UntreatedDeaths() =>
        All<CasualtyChanged>().Count(x => x.Payload.Triage == Triage.Deceased && x.Payload.State == CasualtyState.Deceased
                                          && !All<CasualtyChanged>().Any(y => y.Payload.CasualtyId == x.Payload.CasualtyId
                                                                             && y.Payload.State is CasualtyState.TreatedOnScene or CasualtyState.Transporting
                                                                             && y.Event.Sequence < x.Event.Sequence));

    // ---- Outcome and findings ----

    private AarOutcome SessionOutcome()
    {
        var real = _truth.Incidents.Values.OrderBy(i => i.StartedAt).Select(i => (DateTimeOffset?)i.StartedAt).FirstOrDefault();
        double? Minutes(DateTimeOffset? at) => at is { } t && real is { } r ? (t - r).TotalMinutes : null;
        var firstDispatch = All<UnitDispatched>().Where(x => OwnUnit(x.Payload.UnitId)).Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var firstOnScene = All<UnitStatusChanged>().Where(x => x.Payload.Status == UnitStatus.OnScene && OwnUnit(x.Payload.UnitId)
                                                               && _statuses.TryGetValue(x.Payload.UnitId, out var s) && s.Any(c => c.Status == UnitStatus.Dispatched))
            .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        // Key actions: everything except routine bookkeeping, timed from the start of the emergency (or the session).
        var origin = real ?? Start;
        var actions = _decisions.Where(d => d.Category is not ("Assessment" or "Other") && !d.Decision.StartsWith("Alert")
                                            && !d.Decision.EndsWith("say again", StringComparison.Ordinal))
            .Select(d => $"+{(int)(d.At - origin).TotalMinutes}:{(d.At - origin).Seconds:D2} {d.Decision}").ToList();
        return new AarOutcome("This session", 1, _truth.Hurt, _truth.Dead, _truth.Distress.Count, _truth.PeakFire, Minutes(firstOnScene), Minutes(firstDispatch),
            $"{Duration(Duration())} of simulated time", actions);
    }

    private static List<AarFinding> Findings(IReadOnlyList<AarMetric> metrics, IReadOnlyList<AarBranchPoint> branches)
    {
        var findings = new List<AarFinding>();
        findings.AddRange(metrics.Where(m => m.Rating == Rating.Good).GroupBy(m => m.Category).Select(g => g.First()).Take(4)
            .Select(m => new AarFinding(true, $"{m.Name}: {m.Display} (standard {m.Standard}).")));
        findings.AddRange(branches.Where(b => b.Rating == Rating.Good).Take(2).Select(b => new AarFinding(true, $"{b.Topic}: {b.Yours}")));
        findings.AddRange(branches.Where(b => b.Rating == Rating.Poor).Select(b => new AarFinding(false, $"{b.Topic}: {b.Effect}")));
        findings.AddRange(metrics.Where(m => m.Rating == Rating.Poor).Take(6)
            .Select(m => new AarFinding(false, $"{m.Name}: {m.Display} (standard {m.Standard}). {m.Explanation}")));
        return findings;
    }
}
