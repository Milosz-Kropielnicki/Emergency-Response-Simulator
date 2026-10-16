namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// IAP versioning (Phase 18): what changed from one version of a plan to the next, for the planner, the approver and the
/// briefing ("what's different from the last plan"), and the version history bound into the IAP package.
/// </summary>
public static class IapVersions
{
    /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>, one line each; empty when nothing changed.</summary>
    public static IReadOnlyList<string> Compare(IapContent before, IapContent after)
    {
        var changes = new List<string>();
        void Changed(string what, string? from, string? to)
        {
            if (Same(from, to)) return;
            changes.Add(string.IsNullOrWhiteSpace(from) ? $"{what} added: {Short(to)}"
                : string.IsNullOrWhiteSpace(to) ? $"{what} removed"
                : $"{what} changed: {Short(to)}");
        }

        Changed("Commander's intent", before.CommandersIntent, after.CommandersIntent);

        // Objectives keep their ids across versions, so they can be matched.
        var oldObjectives = before.OperationalObjectives.ToDictionary(o => o.Id);
        var newObjectives = after.OperationalObjectives.ToDictionary(o => o.Id);
        foreach (var (id, objective) in newObjectives)
        {
            var number = after.NumberOf(id);
            if (!oldObjectives.TryGetValue(id, out var old))
            {
                changes.Add($"Objective {number} added: {Short(objective.Statement)}");
                continue;
            }
            if (!Same(old.Statement, objective.Statement)) changes.Add($"Objective {number} reworded: {Short(objective.Statement)}");
            if (!Same(old.Responsible, objective.Responsible)) changes.Add($"Objective {number} now the responsibility of {objective.Responsible ?? "nobody"}");
            if (old.TargetTime != objective.TargetTime)
                changes.Add($"Objective {number} target " + (objective.TargetTime is { } due ? $"now {due.ToLocalTime():HH:mm}" : "time removed"));
            var tactics = objective.Tactics.Count(t => !old.Tactics.Any(o => o.Id == t.Id));
            if (tactics > 0) changes.Add($"Objective {number}: {tactics} tactic(s) added");
        }
        foreach (var (id, objective) in oldObjectives.Where(o => !newObjectives.ContainsKey(o.Key)))
            changes.Add($"Objective {before.NumberOf(id)} dropped: {Short(objective.Statement)}");

        foreach (var role in Enum.GetValues<IcsRole>())
            if (!Same(before.NameFor(role), after.NameFor(role)) && after.NameFor(role) is { } name)
                changes.Add($"{IapDocument.RoleName(role)}: {name}");
        foreach (var group in after.Groups.Where(g => !before.Groups.Any(o => Same(o.Name, g.Name))))
            changes.Add($"{group.Name} formed");
        foreach (var group in before.Groups.Where(g => !after.Groups.Any(o => Same(o.Name, g.Name))))
            changes.Add($"{group.Name} stood down");

        foreach (var assignment in after.Assignments)
        {
            var old = before.Assignments.FirstOrDefault(a => a.UnitId == assignment.UnitId);
            if (old is null) changes.Add($"{assignment.Callsign} assigned: {Short(assignment.Assignment)}");
            else if (!Same(old.Assignment, assignment.Assignment) || !Same(old.Group, assignment.Group))
                changes.Add($"{assignment.Callsign} reassigned: {Short(assignment.Assignment)}" + (assignment.Group is { Length: > 0 } g ? $" ({g})" : ""));
        }
        foreach (var assignment in before.Assignments.Where(a => !after.Assignments.Any(n => n.UnitId == a.UnitId)))
            changes.Add($"{assignment.Callsign} released from the plan");

        var channels = after.Communications.Channels.Count(c => !before.Communications.Channels.Any(o => Same(o.Channel, c.Channel) && Same(o.AssignedTo, c.AssignedTo)));
        if (channels > 0) changes.Add($"Radio plan: {channels} channel assignment(s) new or changed");

        foreach (var hospital in after.Medical.Hospitals.Where(h => !before.Medical.Hospitals.Any(o => Same(o.Name, h.Name))))
            changes.Add($"Receiving hospital added: {hospital.Name}");
        foreach (var destination in after.Medical.PatientDestinations.Except(before.Medical.PatientDestinations))
            changes.Add($"Patient destination: {destination}");
        Changed("Casualty clearing station", before.Medical.CasualtyClearingStation, after.Medical.CasualtyClearingStation);
        Changed("Temporary mortuary", before.Medical.Mortuary, after.Medical.Mortuary);

        foreach (var hazard in after.Safety.Hazards.Where(h => !string.IsNullOrWhiteSpace(h.Hazard) && !before.Safety.Hazards.Any(o => Same(o.Hazard, h.Hazard))))
            changes.Add($"New hazard: {Short(hazard.Hazard)}");
        foreach (var row in after.Safety.Analysis.Where(r => !before.Safety.Analysis.Any(o => Same(o.Area, r.Area) && Same(o.Hazards, r.Hazards))))
            changes.Add($"Safety analysis for {row.Area}: {Short(row.Hazards)}");
        Changed("Safety message", before.Safety.Message, after.Safety.Message);
        return changes;
    }

    /// <summary>A period's versions, oldest first, each with what happened to it: for the IAP package's cover.</summary>
    public static IReadOnlyList<string> History(IEnumerable<IncidentActionPlan> versions)
    {
        var lines = new List<string>();
        IncidentActionPlan? previous = null;
        foreach (var plan in versions.OrderBy(p => p.Version))
        {
            var steps = new List<string> { $"drafted {plan.CreatedAt.ToLocalTime():HH:mm}" + (plan.PreparedBy is { } by ? $" by {by}" : "") };
            if (plan.SubmittedAt is { } submitted) steps.Add($"submitted {submitted.ToLocalTime():HH:mm}" + (plan.SubmittedBy is { } s ? $" by {s}" : ""));
            if (plan.ReturnComments is { } comments) steps.Add($"returned by {plan.ReturnedBy}: \"{comments}\"");
            if (plan.ApprovedAt is { } approved) steps.Add($"approved {approved.ToLocalTime():HH:mm} by {plan.ApprovedBy}");
            if (plan.BriefedAt is { } briefed) steps.Add($"briefed {briefed.ToLocalTime():HH:mm}");
            if (plan.Status == IapStatus.Superseded) steps.Add("superseded");
            var changes = previous is null ? 0 : Compare(previous.Content, plan.Content).Count;
            lines.Add($"v{plan.Version}: {string.Join(", ", steps)}" + (previous is null ? "" : $" ({changes} change(s) from v{previous.Version})"));
            previous = plan;
        }
        return lines;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Short(string? text)
    {
        var t = (text ?? "").Trim().ReplaceLineEndings(" ");
        return t.Length <= 70 ? t : t[..67] + "…";
    }
}
