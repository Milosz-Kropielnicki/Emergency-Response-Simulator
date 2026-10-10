namespace Emergency_Response_Simulator.Core.Model;

public enum IapSection
{
    Period,
    Objectives,
    Organisation,
    Assignments,
    Communications,
    Medical,
    Safety,
}

public enum IssueSeverity
{
    /// <summary>The plan cannot be submitted or approved until this is fixed.</summary>
    Error,

    /// <summary>Allowed, but the approver should know.</summary>
    Warning,
}

public sealed record ComplianceIssue(IssueSeverity Severity, IapSection Section, string Message);

/// <summary>
/// ICS completeness and consistency checks for an IAP (Design Document §8.3, §19): the parts every plan
/// needs, objectives that are measurable, and assignments that match what the COP shows. Errors block
/// submission and approval; warnings are shown to the approver.
/// </summary>
public static class IapCompliance
{
    /// <param name="findUnit">Looks a unit up on the COP roster, so units that left the incident are described exactly.</param>
    public static IReadOnlyList<ComplianceIssue> Check(IapContent plan, OperationalPeriod? period = null, Incident? incident = null,
        Func<Guid, Unit?>? findUnit = null)
    {
        var issues = new List<ComplianceIssue>();
        void Error(IapSection section, string message) => issues.Add(new(IssueSeverity.Error, section, message));
        void Warn(IapSection section, string message) => issues.Add(new(IssueSeverity.Warning, section, message));

        // ---- Objectives: strategic → operational → tactical ----
        if (plan.Objectives.Count == 0)
            Error(IapSection.Objectives, "No objectives. Add at least one strategic objective.");
        for (var s = 0; s < plan.Objectives.Count; s++)
        {
            var strategic = plan.Objectives[s];
            var label = $"Objective {s + 1}";
            if (string.IsNullOrWhiteSpace(strategic.Statement))
                Error(IapSection.Objectives, $"{label} has no statement.");
            if (strategic.Operational.Count == 0)
                Error(IapSection.Objectives, $"{label} ({Short(strategic.Statement)}) has no operational objectives: say how it will be achieved this period.");

            for (var o = 0; o < strategic.Operational.Count; o++)
            {
                var operational = strategic.Operational[o];
                var number = $"Objective {s + 1}.{o + 1}";
                if (string.IsNullOrWhiteSpace(operational.Statement))
                {
                    Error(IapSection.Objectives, $"{number} has no statement.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(operational.PerformanceTarget) && operational.TargetTime is null)
                    Warn(IapSection.Objectives, $"{number} ({Short(operational.Statement)}) has no performance target, so it can't be measured.");
                if (operational.Tactics.Count(t => !string.IsNullOrWhiteSpace(t.Description)) == 0)
                    Warn(IapSection.Objectives, $"{number} ({Short(operational.Statement)}) has no tactics.");
                if (operational.TargetTime is { } due && period is not null && (due < period.Start || due > period.End))
                    Warn(IapSection.Objectives, $"{number} is due at {due.ToLocalTime():HH:mm}, outside the operational period ({period.Window}).");
                if (plan.Assignments.All(a => a.ObjectiveId != operational.Id))
                    Warn(IapSection.Assignments, $"No unit is assigned to objective {s + 1}.{o + 1} ({Short(operational.Statement)}).");
            }
        }

        // ---- Organisation (ICS 203) ----
        if (plan.NameFor(IcsRole.IncidentCommander) is null)
            Error(IapSection.Organisation, "No Incident Commander named.");
        if (plan.NameFor(IcsRole.Safety) is null)
            Warn(IapSection.Organisation, "No Safety Officer named; the IC keeps that responsibility.");
        foreach (var duplicate in plan.Groups.GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error(IapSection.Organisation, $"Two groups are called \"{duplicate.Key}\".");
        foreach (var group in plan.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                Error(IapSection.Organisation, "A group has no name.");
            else if (string.IsNullOrWhiteSpace(group.Supervisor))
                Warn(IapSection.Organisation, $"{group.Name} has no supervisor.");
        }

        var command = ToCommand(plan);
        var assigned = plan.Assignments.Select(a => a.UnitId).Distinct().ToList();
        foreach (var span in SpanOfControl.Assess(command, assigned).Where(s => s.IsOverloaded))
            Warn(IapSection.Organisation, $"{span.Supervisor} would have {span.DirectReports} direct reports (recommended {SpanOfControl.Minimum}–{SpanOfControl.Maximum}).");

        // ---- Assignments (ICS 204) ----
        var objectiveIds = plan.OperationalObjectives.Select(o => o.Id).ToHashSet();
        foreach (var assignment in plan.Assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Assignment))
                Error(IapSection.Assignments, $"{assignment.Callsign} has no assignment.");
            if (assignment.Group is { Length: > 0 } groupName
                && !plan.Groups.Any(g => string.Equals(g.Name.Trim(), groupName.Trim(), StringComparison.OrdinalIgnoreCase)))
                Error(IapSection.Assignments, $"{assignment.Callsign} is assigned to \"{groupName}\", which is not in the organisation.");
            if (assignment.ObjectiveId is { } objectiveId && !objectiveIds.Contains(objectiveId))
                Error(IapSection.Assignments, $"{assignment.Callsign}'s objective no longer exists in the plan.");
        }
        foreach (var duplicate in plan.Assignments.GroupBy(a => a.UnitId).Where(g => g.Count() > 1))
            Error(IapSection.Assignments, $"{duplicate.First().Callsign} is assigned more than once.");

        // Plan against reality: what the COP says about the incident's resources (§8.6).
        if (incident is not null)
        {
            foreach (var unit in incident.AssignedUnits.Where(u => assigned.All(id => id != u.Id)).OrderBy(u => u.Callsign, StringComparer.Ordinal))
                Warn(IapSection.Assignments, $"{unit.Callsign} is committed to {incident.Number} but has no assignment in the plan.");
            foreach (var assignment in plan.Assignments)
            {
                // Units not yet committed are fine here: briefing the plan dispatches them.
                var unit = findUnit is not null ? findUnit(assignment.UnitId) : incident.AssignedUnits.FirstOrDefault(u => u.Id == assignment.UnitId);
                var problem = findUnit is null && unit is null
                    ? $"is no longer committed to {incident.Number}"
                    : ViabilityProblem(assignment, incident, unit, expectCommitted: false);
                if (problem is not null)
                    Warn(IapSection.Assignments, $"{assignment.Callsign} {problem}: \"{Short(assignment.Assignment)}\" is not viable.");
            }
        }

        // ---- Communications (ICS 205) ----
        var channels = plan.Communications.Channels.Where(c => !string.IsNullOrWhiteSpace(c.Channel)).ToList();
        if (channels.Count == 0)
            Error(IapSection.Communications, "No radio channels in the communications plan.");
        else if (!channels.Any(c => c.Function.Contains("command", StringComparison.OrdinalIgnoreCase)))
            Warn(IapSection.Communications, "No command channel.");
        foreach (var group in plan.Groups.Where(g => g.Name.Length > 0
                     && !channels.Any(c => c.AssignedTo.Contains(g.Name, StringComparison.OrdinalIgnoreCase))))
            Warn(IapSection.Communications, $"{group.Name} has no channel assigned.");

        // ---- Medical (ICS 206) ----
        if (plan.Medical.Hospitals.All(h => string.IsNullOrWhiteSpace(h.Name)))
            Error(IapSection.Medical, "No receiving hospital in the medical plan.");
        if (incident is { CasualtiesReported: > 0 } && string.IsNullOrWhiteSpace(plan.Medical.CasualtyClearingStation))
            Warn(IapSection.Medical, $"{incident.CasualtiesReported} casualties reported but no casualty clearing station is set.");
        if (string.IsNullOrWhiteSpace(plan.Medical.EmergencyProcedures))
            Warn(IapSection.Medical, "No procedure for an injured responder.");

        // ---- Safety (ICS 208) ----
        if (string.IsNullOrWhiteSpace(plan.Safety.Message))
            Error(IapSection.Safety, "No safety message.");
        if (plan.Safety.Hazards.All(h => string.IsNullOrWhiteSpace(h.Hazard)))
        {
            if (incident is { Threats.Count: > 0 })
                Error(IapSection.Safety, $"The COP reports threats ({string.Join(", ", incident.Threats)}) but the safety plan lists no hazards.");
            else
                Warn(IapSection.Safety, "The safety plan lists no hazards.");
        }
        foreach (var hazard in plan.Safety.Hazards.Where(h => !string.IsNullOrWhiteSpace(h.Hazard) && string.IsNullOrWhiteSpace(h.Mitigation)))
            Warn(IapSection.Safety, $"No mitigation for \"{hazard.Hazard}\".");
        if (plan.Safety.Ppe.All(string.IsNullOrWhiteSpace))
            Warn(IapSection.Safety, "No PPE requirements.");

        // ---- Period ----
        if (period is not null && period.End <= period.Start)
            Error(IapSection.Period, "The operational period ends before it starts.");

        return issues;
    }

    /// <summary>Why an assignment can't be carried out as planned, according to the COP; null if it can (§8.6).</summary>
    /// <param name="unit">The unit as the COP knows it, or null if it is not on the roster.</param>
    /// <param name="expectCommitted">After briefing, a unit that is no longer at the incident is a problem.</param>
    public static string? ViabilityProblem(IapAssignment assignment, Incident incident, Unit? unit, bool expectCommitted = true)
    {
        if (unit is null)
            return "is not on the roster";
        if (unit.Status == UnitStatus.OutOfService)
            return "is out of service";
        // Not every crew can do every task (§12): the assignment's wording says what it needs.
        if (unit.Crew is { } crew && Qualifications.RequiredFor(assignment.Assignment) is { } needed && !crew.Meets(needed))
            return $"has {(crew.Holding(needed.Qualification) == 0 ? "no" : $"only {crew.Holding(needed.Qualification)}")} " +
                   $"{Qualifications.Name(needed.Qualification).ToLowerInvariant()} qualified (needs {needed.Members})";
        if (unit.AssignedIncidentId == incident.Id)
            return unit.Status == UnitStatus.Cancelled ? "has been stood down" : null;
        if (unit.AssignedIncident is { } other)
            return $"is committed to {other.Number}";
        return expectCommitted ? $"has been released from {incident.Number}" : null;
    }

    /// <summary>The command structure the plan describes, for span-of-control checks and the org chart.</summary>
    public static IncidentCommand ToCommand(IapContent plan)
    {
        var command = new IncidentCommand();
        foreach (var position in plan.Organization.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            command.Positions[position.Role] = position.Name;
        foreach (var group in plan.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
        {
            var ics = new IcsGroup { Id = Guid.NewGuid(), Name = group.Name, Kind = group.Kind, Supervisor = group.Supervisor };
            ics.UnitIds.AddRange(plan.Assignments
                .Where(a => string.Equals(a.Group?.Trim(), group.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(a => a.UnitId));
            command.Groups.Add(ics);
        }
        return command;
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..37] + "…";
}
