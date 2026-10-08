namespace Emergency_Response_Simulator.Core.Model;

/// <summary>A planning window; each one gets its own IAP (Design Document §8.7).</summary>
public class OperationalPeriod
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }

    public Guid IncidentId { get; set; }
    public Incident? Incident { get; set; }
}
