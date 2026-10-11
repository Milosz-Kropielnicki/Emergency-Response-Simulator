using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Multi-agency measures (Design Document §14, §22): liaison and PIO, unified command, how long other agencies' knowledge
/// took to reach command, authority disputes, EOC activation and situation reports, the media and conflicting messages.
/// </summary>
public sealed partial class AarAnalyzer
{
    private void InteragencyMetrics(Action<string, string, double?, string, string, Rating, string> add, IReadOnlyList<(SimEvent Event, IncidentCreated Payload)> opened)
    {
        const string category = "Multi-agency";
        var major = opened.Count > 0 && (_cop.Incidents.Any(i => i.Priority >= IncidentPriority.High) || EocWarrantedAt(End) is not null)
                    && Duration() >= TimeSpan.FromMinutes(15);
        var firstOpened = opened.Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();

        // Liaison officer and PIO.
        foreach (var (role, name, why) in new[]
                 {
                     (IcsRole.Liaison, "Liaison officer appointed", "Brings other agencies' knowledge in, and speeds unified command and negotiations."),
                     (IcsRole.PublicInformation, "PIO appointed", "Answers the media and passes protective advice to the public."),
                 })
        {
            var staffed = All<IcsPositionAssigned>().Where(x => x.Payload.Role == role).Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
            if (firstOpened is not { } since) continue;
            if (staffed is { } at)
                add(category, name, (at - since).TotalSeconds, $"{Duration(at - since)} after opening", "within 10 min",
                    Within(at - since, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)), why);
            else
                add(category, name, null, "Never", "within 10 min", major ? Rating.Poor : Rating.NotApplicable, why);
        }

        // Unified command.
        var joined = All<UnifiedCommandJoined>().ToList();
        if (firstOpened is { } open)
        {
            if (joined.Count > 0)
            {
                var first = joined[0].Event.SimTime - open;
                var members = joined.Select(x => x.Payload.Agency).Distinct().ToList();
                add(category, "Unified command", first.TotalSeconds, $"{members.Count} agenc{(members.Count == 1 ? "y" : "ies")}, first after {Duration(first)}",
                    "agencies with a stake within 15 min", Within(first, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)),
                    $"Shared command with {string.Join(", ", members)}. Members' units can be tasked and what they know arrives at once.");
            }
            else
            {
                add(category, "Unified command", null, "Never formed", "agencies with a stake within 15 min",
                    major && _aiAgencies.Count > 0 ? Rating.Poor : Rating.NotApplicable,
                    "Other agencies worked alongside, not with you: their own priorities, their own information.");
            }
        }

        // Information silos.
        var held = _events.Where(e => e.Payload is InformationHeld).ToDictionary(e => ((InformationHeld)e.Payload).ItemId, e => (e.SimTime, (InformationHeld)e.Payload));
        if (held.Count > 0)
        {
            var shared = All<InformationShared>().Where(x => held.ContainsKey(x.Payload.ItemId))
                .ToDictionary(x => x.Payload.ItemId, x => (Delay: x.Event.SimTime - held[x.Payload.ItemId].SimTime, x.Payload.Via));
            var never = held.Count - shared.Count;
            var urgentLate = held.Count(h => h.Value.Item2.Urgent && (!shared.TryGetValue(h.Key, out var s) || s.Delay > TimeSpan.FromMinutes(10)));
            var mean = shared.Count == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(shared.Values.Average(s => s.Delay.TotalSeconds));
            var via = string.Join(", ", shared.Values.GroupBy(s => s.Via).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} via {g.Key}"));
            add(category, "Other agencies' information reaching command", mean?.TotalSeconds,
                mean is { } m ? $"{Duration(m)} on average" + (never > 0 ? $", {never} never" : "") : "none of it",
                "under 5 min", urgentLate > 0 || never > 0 && mean is null ? Rating.Poor : Within(mean, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
                $"{held.Count} things other agencies knew{(via.Length > 0 ? $" ({via})" : "")}." +
                (urgentLate > 0 ? $" {urgentLate} life-safety item(s) took over 10 minutes or never came." : ""));
        }

        // Authority disputes.
        var disputes = _cop.Disputes;
        if (disputes.Count > 0)
        {
            var asserted = disputes.Count(d => d.Resolution == DisputeResolution.Assert);
            var unsettled = disputes.Count(d => d.IsOpen);
            var badConcede = _decisions.Any(d => d.Note?.StartsWith("Handed the lead over") == true);
            var settled = disputes.Where(d => d.ResolvedAt is not null).ToList();
            var mean = settled.Count == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(settled.Average(d => (d.ResolvedAt!.Value - d.RaisedAt).TotalSeconds));
            var mix = string.Join(", ", disputes.Where(d => d.Resolution is not null).GroupBy(d => d.Resolution!.Value)
                .Select(g => $"{g.Count()} {EventDescriber.Humanize(g.Key).ToLowerInvariant()}"));
            add(category, "Authority disputes", disputes.Count, $"{disputes.Count} raised" + (mix.Length > 0 ? $": {mix}" : "") + (unsettled > 0 ? $", {unsettled} unsettled" : ""),
                "settled by agreement", unsettled > 0 || badConcede || asserted >= 2 ? Rating.Poor : asserted == 1 ? Rating.Fair : Rating.Good,
                (mean is { } m ? $"Settled in {Duration(m)} on average. " : "") +
                "Negotiating or escalating to the EOC keeps relations; overruling an agency costs cooperation later.");
        }

        // EOC activation.
        var warranted = EocWarrantedAt(End);
        var raised = All<EocActivationRequested>().Where(x => x.Payload.Level >= EocLevel.Partial).Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var peak = All<EocLevelReached>().Select(x => x.Payload.Level).DefaultIfEmpty(EocLevel.Normal).Max();
        if (warranted is { } needed)
        {
            add(category, "EOC activation", raised is { } r ? (r - needed).TotalSeconds : null,
                raised is { } asked ? $"{EocLevels.Label(peak)} asked for {Duration(asked > needed ? asked - needed : TimeSpan.Zero)} after it was warranted" : "Not activated",
                "partial activation within 15 min of a major emergency", raised is { } at ? Within(at > needed ? at - needed : TimeSpan.Zero, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)) : Rating.Poor,
                "A major emergency (protective zones, chemicals, a critical incident) needs the EOC: reserves released, disputes decided, one public message.");
        }
        else if (peak >= EocLevel.Partial)
        {
            add(category, "EOC activation", null, EocLevels.Label(peak), "matched to the incident", peak == EocLevel.Full ? Rating.Fair : Rating.NotApplicable,
                "Activated for an incident that never became a major emergency: staff and overtime the situation didn't need.");
        }

        var asks = All<SitrepRequested>().Select(x => x.Payload.DueAt).Where(d => d <= End).ToList();
        if (asks.Count > 0)
        {
            var sent = All<SitrepSubmitted>().Select(x => x.Event.SimTime).ToList();
            var onTime = asks.Count(due => sent.Any(s => s <= due && s >= due - TimeSpan.FromMinutes(30)));
            var share = (double)onTime / asks.Count;
            add(category, "Situation reports on time", share, $"{onTime} of {asks.Count}", "all", share >= 0.99 ? Rating.Good : share >= 0.5 ? Rating.Fair : Rating.Poor,
                "Without them the EOC works blind, and decides escalated disputes more slowly.");
        }

        // The media and the public.
        var enquiries = _cop.MediaEnquiries;
        if (enquiries.Count > 0)
        {
            var due = enquiries.Where(m => End - m.ReceivedAt >= TimeSpan.FromMinutes(10)).ToList();
            if (due.Count > 0)
            {
                var answered = due.Count(m => m.AnsweredAt is { } a && a - m.ReceivedAt <= TimeSpan.FromMinutes(10));
                var share = (double)answered / due.Count;
                add(category, "Media enquiries answered within 10 min", share, $"{answered} of {due.Count}", "at least 80 %",
                    share >= 0.8 ? Rating.Good : share >= 0.4 ? Rating.Fair : Rating.Poor,
                    "Unanswered, speculation fills the gap and worried people ring 999.");
            }
        }
        var protective = All<ZoneDeclared>().Any(x => x.Payload.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace);
        var conflicting = _cop.PublicStatements.Count(s => s.Conflicting);
        if (protective || conflicting > 0)
        {
            add(category, "Conflicting public messages", conflicting, conflicting.ToString(), "none", conflicting == 0 ? Rating.Good : Rating.Poor,
                "A joint information centre (a PIO, with unified command or the EOC) keeps every agency on one message; a contradiction persuades fewer people to act.");
        }

        // Resources from other agencies (context).
        var help = _cop.AssistanceRequests.Where(r => r.Granted is not null).ToList();
        if (help.Count > 0)
        {
            add(category, "Units released by other agencies", help.Sum(r => r.Granted!.Value), $"{help.Sum(r => r.Granted!.Value)} of {help.Sum(r => r.Quantity)} asked",
                "context", Rating.NotApplicable, "Agencies keep a reserve for their own area unless they share command or the EOC releases it.");
        }
    }


    // ---- Branch points (Phase 10) ----

    /// <summary>The most urgent thing another agency knew: when it reached command, against unified command or a liaison officer.</summary>
    private void InformationSiloBranch(List<AarBranchPoint> branches)
    {
        var item = _events.Where(e => e.Payload is InformationHeld { Urgent: true }).Select(e => (e.SimTime, Held: (InformationHeld)e.Payload)).FirstOrDefault();
        if (item.Held is null) return;
        var agency = _agencyNames.GetValueOrDefault(item.Held.AgencyId) ?? "Another agency";
        var known = item.SimTime;
        var shared = All<InformationShared>().FirstOrDefault(x => x.Payload.ItemId == item.Held.ItemId);
        var claim = item.Held.Claim.Length > 90 ? item.Held.Claim[..90] + "…" : item.Held.Claim;
        var recommended = known + TimeSpan.FromSeconds(30);

        string yours, effect;
        Rating rating;
        if (shared.Payload is { } s)
        {
            var delay = shared.Event.SimTime - known;
            yours = $"Reached you at {Clock(shared.Event.SimTime)} via {s.Via}, {Duration(delay)} after {agency} knew.";
            effect = delay <= TimeSpan.FromMinutes(1)
                ? "You heard it as soon as they did."
                : $"{Duration(shared.Event.SimTime - recommended)} later than through unified command: \"{claim}\"";
            rating = Within(delay, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        }
        else
        {
            yours = $"Never reached you. {agency} knew from {Clock(known)}.";
            effect = $"You never heard: \"{claim}\"";
            rating = Rating.Poor;
        }
        branches.Add(new AarBranchPoint("Information held by other agencies", known, yours,
            $"{agency} in unified command (it would have told you by {Clock(recommended)}), or a liaison officer doing the rounds every 4 minutes.",
            $"Left to them: volunteered at about {Clock(known + TimeSpan.FromMinutes(20))}, if relations were good.", effect, rating));
    }

    /// <summary>Another agency's claim to the lead: how it was answered, against a negotiated, staged handover.</summary>
    private void LeadAgencyBranch(List<AarBranchPoint> branches)
    {
        if (_cop.Disputes.FirstOrDefault(d => d.Kind == DisputeKind.LeadAgency) is not { } dispute) return;
        var settledAt = dispute.ResolvedAt;
        var (yours, effect, rating) = dispute.Resolution switch
        {
            DisputeResolution.Concede when _decisions.Any(d => d.Note?.StartsWith("Handed the lead over") == true) =>
                ($"Conceded: {dispute.Agency} took the lead at {Clock(settledAt ?? dispute.RaisedAt)}.",
                 $"Firefighting went defensive while the fire still burned; it went on to a peak of {_truth.PeakFire:N0} m².", Rating.Poor),
            DisputeResolution.Concede => ($"Conceded: {dispute.Agency} took the lead.", "The fire was already out, so nothing was lost.", Rating.Good),
            DisputeResolution.Assert => ("Overruled them: you kept the lead.",
                $"Quick, but {dispute.Agency} cooperated less afterwards: slower to share, quicker to object.", Rating.Fair),
            DisputeResolution.Negotiate or DisputeResolution.Escalate =>
                ($"{EventDescriber.Humanize(dispute.Resolution!.Value)}d: {dispute.Outcome}", "Both priorities kept: the fire fought, the scene preserved.", Rating.Good),
            _ => ("Never answered.", "The question of who leads stayed open, with both agencies giving orders at the scene.", Rating.Poor),
        };
        branches.Add(new AarBranchPoint("Who leads", dispute.RaisedAt, yours,
            "Negotiate a staged handover: fire leads until the fire is out and casualties are clear, then the investigators take the scene.",
            "Concede at once: the investigators lead, and firefighting goes defensive to preserve the scene.", effect, rating));
    }

    /// <summary>
    /// When the incident became a major emergency that warranted the EOC (partial activation): a protective zone, a report
    /// of chemicals once an incident is open, or a Critical incident. Null if it never did by <paramref name="upTo"/>.
    /// </summary>
    private DateTimeOffset? EocWarrantedAt(DateTimeOffset upTo)
    {
        var zone = All<ZoneDeclared>().Where(x => x.Event.SimTime <= upTo && x.Payload.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace)
            .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var firstIncident = _incidents.Values.Select(i => (DateTimeOffset?)i.At).Min();
        var chemical = firstIncident is { } h ? _reports.Where(r => r.At <= upTo && IsChemicalReport(r.Claim)).Select(r => (DateTimeOffset?)(r.At > h ? r.At : h)).FirstOrDefault() : null;
        var critical = All<IncidentCreated>().Where(x => x.Payload.Priority == IncidentPriority.Critical).Select(x => (DateTimeOffset?)x.Event.SimTime)
            .Concat(All<IncidentUpdated>().Where(x => x.Payload.Priority == IncidentPriority.Critical).Select(x => (DateTimeOffset?)x.Event.SimTime))
            .Where(t => t <= upTo).Min();
        return new[] { zone, chemical, critical }.Where(t => t is not null).Min();
    }
}
