namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// A planning window; each one gets its own IAP (Design Document §8.7). At the end of a period command
/// reassesses the situation and the next period's plan reflects the new reality.
/// </summary>
public class OperationalPeriod
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public DateTimeOffset Start { get; set; }

    /// <summary>Planned end; brought forward if command starts the next period early.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>What the period is about, e.g. "Initial response, rescue, evacuation, fire containment".</summary>
    public string? Focus { get; set; }

    public Guid IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public bool Contains(DateTimeOffset time) => time >= Start && time < End;

    public string Window => $"{Start.ToLocalTime():HH:mm}–{End.ToLocalTime():HH:mm}";
}
