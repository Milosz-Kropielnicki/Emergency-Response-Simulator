namespace Emergency_Response_Simulator.Core.Model;

/// <summary>"Something requires your attention" (Design Document §6.9).</summary>
public class Alert
{
    public Guid Id { get; set; }
    public AlertCategory Category { get; set; }
    public AlertSeverity Severity { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }

    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public Guid? AcknowledgedById { get; set; }
    public User? AcknowledgedBy { get; set; }

    public Guid? IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public Guid? UnitId { get; set; }
    public Unit? Unit { get; set; }
}
