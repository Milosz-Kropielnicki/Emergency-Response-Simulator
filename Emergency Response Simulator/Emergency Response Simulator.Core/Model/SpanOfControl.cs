namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// ICS span of control (Design Document §8.1): a supervisor should directly manage 3–7 resources,
/// ideally 5. Counts direct reports for every supervisor in an incident's structure.
/// </summary>
public static class SpanOfControl
{
    public const int Minimum = 3;
    public const int Maximum = 7;

    /// <param name="assignedUnits">Units committed to the incident.</param>
    public static IReadOnlyList<SpanEntry> Assess(IncidentCommand command, IReadOnlyCollection<Guid> assignedUnits)
    {
        var grouped = command.Groups.SelectMany(g => g.UnitIds).ToHashSet();
        var ungrouped = assignedUnits.Count(u => !grouped.Contains(u));
        var entries = new List<SpanEntry>();

        // With Operations staffed, groups and ungrouped units report to Operations; otherwise to the IC.
        var operationsReports = command.Groups.Count + ungrouped;
        var commandAndGeneralStaff = command.Positions.Keys.Count(r => r != IcsRole.IncidentCommander);

        if (command.IsStaffed(IcsRole.Operations))
        {
            entries.Add(new SpanEntry(SupervisorKey(IcsRole.Operations), "Operations", operationsReports));
            entries.Add(new SpanEntry(SupervisorKey(IcsRole.IncidentCommander), "Incident Commander", commandAndGeneralStaff));
        }
        else
        {
            entries.Add(new SpanEntry(SupervisorKey(IcsRole.IncidentCommander), "Incident Commander",
                commandAndGeneralStaff + operationsReports));
        }

        foreach (var group in command.Groups)
            entries.Add(new SpanEntry(SupervisorKey(group.Id), group.Name, group.UnitIds.Count(assignedUnits.Contains)));

        return entries;
    }

    /// <summary>The span of whoever directly supervises a unit (its group, Operations or the IC).</summary>
    public static SpanEntry SupervisorOf(Guid unitId, IncidentCommand command, IReadOnlyCollection<Guid> assignedUnits)
    {
        var all = Assess(command, assignedUnits);
        var key = command.GroupOf(unitId) is { } group ? SupervisorKey(group.Id)
            : command.IsStaffed(IcsRole.Operations) ? SupervisorKey(IcsRole.Operations)
            : SupervisorKey(IcsRole.IncidentCommander);
        return all.First(e => e.Key == key);
    }

    public static string SupervisorKey(IcsRole role) => $"role:{role}";
    public static string SupervisorKey(Guid groupId) => $"group:{groupId}";
}

/// <param name="Key">Stable identifier of the supervisor (role or group).</param>
public sealed record SpanEntry(string Key, string Supervisor, int DirectReports)
{
    public bool IsOverloaded => DirectReports > SpanOfControl.Maximum;

    /// <summary>Under-used only matters when there is someone to supervise at all.</summary>
    public bool IsUnderUsed => DirectReports is > 0 and < SpanOfControl.Minimum;

    /// <summary>
    /// The organisational penalty for an overloaded supervisor: how much slower they are to pass orders on
    /// (1 = no penalty) and the chance an order is lost in the noise.
    /// </summary>
    public double DelayFactor => IsOverloaded ? 1 + 0.5 * (DirectReports - SpanOfControl.Maximum) : 1;

    public double LossProbability => IsOverloaded ? Math.Min(0.5, 0.1 * (DirectReports - SpanOfControl.Maximum)) : 0;
}
