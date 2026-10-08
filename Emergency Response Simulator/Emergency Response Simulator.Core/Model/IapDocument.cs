using System.Text;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// The IAP as a readable document (Design Document Appendix C), for the review step, briefings and the AAR.
/// </summary>
public static class IapDocument
{
    public static string Render(IncidentActionPlan plan, Incident? incident = null,
        IReadOnlyDictionary<Guid, ObjectiveStatus>? progress = null)
    {
        var c = plan.Content;
        var text = new StringBuilder();
        void Line(string line = "") => text.AppendLine(line);
        void Heading(string heading)
        {
            Line();
            Line(heading);
            Line(new string('─', heading.Length));
        }

        Line("INCIDENT ACTION PLAN");
        if (incident is not null)
            Line($"INCIDENT: {incident.Name ?? Humanize(incident.Type.ToString())} ({incident.Number})" +
                 (incident.Address is { } address ? $", {address}" : ""));
        if (plan.OperationalPeriod is { } period)
            Line($"OPERATIONAL PERIOD {period.Number}: {period.Window}" + (period.Focus is { Length: > 0 } focus ? $" — {focus}" : ""));
        Line($"VERSION {plan.Version} · {StatusLine(plan)}");
        if (plan.PreparedBy is { } preparedBy)
            Line($"PREPARED BY: {preparedBy}");

        if (!string.IsNullOrWhiteSpace(c.CommandersIntent))
        {
            Heading("COMMANDER'S INTENT");
            Line(c.CommandersIntent.Trim());
        }
        if (!string.IsNullOrWhiteSpace(c.SituationSummary))
        {
            Heading("SITUATION");
            Line(c.SituationSummary.Trim());
        }

        Heading("OBJECTIVES (ICS 202)");
        if (c.Objectives.Count == 0) Line("None.");
        for (var s = 0; s < c.Objectives.Count; s++)
        {
            var strategic = c.Objectives[s];
            Line($"{s + 1}. {strategic.Statement}");
            for (var o = 0; o < strategic.Operational.Count; o++)
            {
                var operational = strategic.Operational[o];
                var status = progress?.GetValueOrDefault(operational.Id) is { } st and not ObjectiveStatus.Open ? $"  [{Humanize(st.ToString())}]" : "";
                Line($"   {s + 1}.{o + 1} {operational.Statement}{status}");
                if (!string.IsNullOrWhiteSpace(operational.Responsible))
                    Line($"       Responsible: {operational.Responsible}");
                foreach (var tactic in operational.Tactics.Where(t => !string.IsNullOrWhiteSpace(t.Description)))
                    Line($"       • {tactic.Description}");
                if (!string.IsNullOrWhiteSpace(operational.ResourcesSummary))
                    Line($"       Resources: {operational.ResourcesSummary}");
                var target = (operational.PerformanceTarget ?? "").Trim();
                if (operational.TargetTime is { } due)
                    target = (target.Length > 0 ? target + " " : "") + $"by {due.ToLocalTime():HH:mm}";
                if (target.Length > 0)
                    Line($"       Target: {target}");
            }
        }

        Heading("ORGANISATION (ICS 203)");
        foreach (var role in Enum.GetValues<IcsRole>())
        {
            if (c.NameFor(role) is { } name)
                Line($"{RoleName(role)}: {name}");
        }
        foreach (var group in c.Groups)
            Line($"{group.Name} ({group.Kind.ToString().ToLowerInvariant()}): {group.Supervisor ?? "no supervisor"}");

        Heading("RESOURCE ASSIGNMENTS (ICS 204)");
        if (c.Assignments.Count == 0) Line("None.");
        var width = c.Assignments.Count == 0 ? 0 : c.Assignments.Max(a => a.Callsign.Length);
        foreach (var assignment in c.Assignments.OrderBy(a => a.Group ?? "").ThenBy(a => a.Callsign, StringComparer.Ordinal))
        {
            var detail = new List<string>();
            if (assignment.Group is { Length: > 0 } group) detail.Add(group);
            if (assignment.ObjectiveId is { } objective && c.NumberOf(objective) is { } number) detail.Add($"objective {number}");
            Line($"{assignment.Callsign.PadRight(width)} → {assignment.Assignment}" + (detail.Count > 0 ? $"  [{string.Join(" · ", detail)}]" : ""));
        }

        Heading("COMMUNICATIONS (ICS 205)");
        foreach (var channel in c.Communications.Channels)
            Line($"{channel.Function}: {channel.Channel} — {channel.AssignedTo}" + (channel.Remarks is { Length: > 0 } r ? $" ({r})" : ""));
        if (!string.IsNullOrWhiteSpace(c.Communications.Notes))
            Line(c.Communications.Notes.Trim());

        Heading("MEDICAL PLAN (ICS 206)");
        var medical = c.Medical;
        if (medical.MedicalLead is { Length: > 0 } lead) Line($"Medical lead: {lead}");
        if (medical.CasualtyClearingStation is { Length: > 0 } ccs) Line($"Casualty clearing station: {ccs}");
        if (medical.AmbulanceLoadingPoint is { Length: > 0 } alp) Line($"Ambulance loading point: {alp}");
        foreach (var hospital in medical.Hospitals)
        {
            var facts = new List<string>();
            if (hospital.TravelMinutes is { } minutes) facts.Add($"{minutes:F0} min");
            if (hospital.DistanceKm is { } km) facts.Add($"{km:F1} km");
            if (hospital.Capabilities is { Length: > 0 } capabilities) facts.Add(capabilities);
            Line($"Hospital: {hospital.Name}" + (facts.Count > 0 ? $" ({string.Join(", ", facts)})" : ""));
        }
        if (medical.EmergencyProcedures is { Length: > 0 } procedures) Line($"Injured responder: {procedures}");

        Heading("SAFETY PLAN (ICS 208)");
        foreach (var hazard in c.Safety.Hazards.Where(h => !string.IsNullOrWhiteSpace(h.Hazard)))
            Line($"Hazard: {hazard.Hazard}" + (hazard.Mitigation is { Length: > 0 } m ? $" → {m}" : ""));
        foreach (var ppe in c.Safety.Ppe.Where(p => !string.IsNullOrWhiteSpace(p)))
            Line($"PPE: {ppe}");
        if (c.Safety.Accountability is { Length: > 0 } accountability) Line($"Accountability: {accountability}");
        if (c.Safety.Message is { Length: > 0 } message)
        {
            Line();
            Line($"SAFETY MESSAGE: {message.Trim()}");
        }

        return text.ToString().TrimEnd();
    }

    public static string StatusLine(IncidentActionPlan plan) => plan.Status switch
    {
        IapStatus.Approved => $"APPROVED by {plan.ApprovedBy} at {plan.ApprovedAt?.ToLocalTime():HH:mm}" +
                              (plan.BriefedAt is { } briefed ? $", briefed {briefed.ToLocalTime():HH:mm}" : ", not yet briefed"),
        IapStatus.PendingApproval => $"AWAITING APPROVAL (submitted by {plan.SubmittedBy} at {plan.SubmittedAt?.ToLocalTime():HH:mm})",
        IapStatus.Superseded => "SUPERSEDED",
        _ => plan.ReturnComments is { } comments ? $"DRAFT (returned by {plan.ReturnedBy}: \"{comments}\")" : "DRAFT",
    };

    public static string RoleName(IcsRole role) => role switch
    {
        IcsRole.IncidentCommander => "Incident Commander",
        IcsRole.Safety => "Safety Officer",
        IcsRole.Liaison => "Liaison Officer",
        IcsRole.PublicInformation => "Public Information Officer",
        IcsRole.Operations => "Operations Section Chief",
        IcsRole.Planning => "Planning Section Chief",
        IcsRole.Logistics => "Logistics Section Chief",
        IcsRole.FinanceAdmin => "Finance/Admin Section Chief",
        _ => role.ToString(),
    };

    private static string Humanize(string name) =>
        string.Concat(name.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));
}

/// <summary>
/// End-of-period reassessment (Design Document §8.6–8.7): how the plan in force compares with what the COP
/// now shows, and the starting point for the next period's plan.
/// </summary>
public static class IapReassessment
{
    public static IReadOnlyList<string> Findings(IncidentActionPlan plan, Incident incident,
        IReadOnlyDictionary<Guid, ObjectiveStatus> progress, IReadOnlyList<Zone> zones, DateTimeOffset now,
        Func<Guid, Unit?>? findUnit = null)
    {
        var findings = new List<string>();
        var objectives = plan.Content.OperationalObjectives.ToList();
        var achieved = objectives.Count(o => progress.GetValueOrDefault(o.Id) == ObjectiveStatus.Achieved);
        findings.Add($"{achieved} of {objectives.Count} operational objective(s) achieved.");

        foreach (var objective in objectives.Where(o => progress.GetValueOrDefault(o.Id) != ObjectiveStatus.Achieved))
        {
            var number = plan.Content.NumberOf(objective.Id);
            var overdue = objective.TargetTime is { } due && due < now ? $" — overdue since {due.ToLocalTime():HH:mm}" : "";
            findings.Add($"Open: {number} {objective.Statement}{overdue}");
        }

        foreach (var assignment in plan.Content.Assignments)
        {
            var unit = findUnit?.Invoke(assignment.UnitId) ?? incident.AssignedUnits.FirstOrDefault(u => u.Id == assignment.UnitId);
            if (IapCompliance.ViabilityProblem(assignment, incident, unit, expectCommitted: plan.BriefedAt is not null) is { } problem)
                findings.Add($"Plan not viable: {assignment.Callsign} {problem} (planned: {assignment.Assignment}).");
        }

        var planned = plan.Content.Assignments.Select(a => a.UnitId).ToHashSet();
        var unplanned = incident.AssignedUnits.Where(u => !planned.Contains(u.Id)).Select(u => u.Callsign).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (unplanned.Count > 0)
            findings.Add($"Not in the plan: {string.Join(", ", unplanned)}.");

        var listed = plan.Content.Safety.Hazards.Select(h => h.Hazard).ToList();
        foreach (var threat in incident.Threats.Where(t => !listed.Any(h => h.Contains(t, StringComparison.OrdinalIgnoreCase))))
            findings.Add($"New threat not in the safety plan: {threat}.");

        var since = plan.ApprovedAt ?? plan.CreatedAt;
        foreach (var zone in zones.Where(z => z.IncidentId == incident.Id && z.EffectiveFrom > since))
            findings.Add($"Zone declared since the plan was approved: {zone.Name}.");

        if (incident.CasualtiesReported > 0)
            findings.Add($"Casualties: {incident.CasualtiesReported} reported, {incident.CasualtiesConfirmed} confirmed.");

        return findings;
    }

    /// <summary>
    /// The next period's starting draft: achieved objectives are dropped (with any strategic objective left
    /// empty), the rest carry forward without their old deadlines, and assignments to dropped objectives are
    /// unlinked so the planner reassigns them.
    /// </summary>
    public static IapContent CarryForward(IapContent current, IReadOnlyDictionary<Guid, ObjectiveStatus> progress)
    {
        var next = current.Clone();
        foreach (var strategic in next.Objectives)
        {
            strategic.Operational.RemoveAll(o => progress.GetValueOrDefault(o.Id) == ObjectiveStatus.Achieved);
            foreach (var operational in strategic.Operational)
                operational.TargetTime = null;
        }
        next.Objectives.RemoveAll(s => s.Operational.Count == 0 && current.Objectives.First(o => o.Id == s.Id).Operational.Count > 0);

        var remaining = next.OperationalObjectives.Select(o => o.Id).ToHashSet();
        foreach (var assignment in next.Assignments.Where(a => a.ObjectiveId is { } id && !remaining.Contains(id)))
            assignment.ObjectiveId = null;
        return next;
    }
}
