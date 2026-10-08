using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// The parent object tying together resources, reports, alerts, zones and plans (Design Document §6.5).
/// This is what the organisation believes about the incident, not the simulation's ground truth.
/// </summary>
public class Incident
{
    public Guid Id { get; set; }

    /// <summary>Human-facing number, e.g. "INC-00241".</summary>
    public required string Number { get; set; }

    public string? Name { get; set; }
    public IncidentType Type { get; set; }
    public IncidentPriority Priority { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Reported;

    public required Point Location { get; set; }
    public string? Address { get; set; }

    public DateTimeOffset ReportedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }

    /// <summary>e.g. "Fire", "Smoke", "Possible chemical storage".</summary>
    public List<string> Threats { get; set; } = [];

    public int CasualtiesReported { get; set; }
    public int CasualtiesConfirmed { get; set; }
    public bool EvacuationRequired { get; set; }

    public Guid? IncidentCommanderId { get; set; }
    public User? IncidentCommander { get; set; }

    public List<Unit> AssignedUnits { get; set; } = [];
    public List<Report> Reports { get; set; } = [];
    public List<Alert> Alerts { get; set; } = [];
    public List<Zone> Zones { get; set; } = [];
    public List<OperationalPeriod> OperationalPeriods { get; set; } = [];
}
