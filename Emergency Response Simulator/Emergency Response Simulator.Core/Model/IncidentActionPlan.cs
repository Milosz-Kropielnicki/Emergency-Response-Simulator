namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// The formal plan for one operational period (Design Document §8.2–8.4).
/// Each revision is a new version; earlier versions are kept as <see cref="IapStatus.Superseded"/>.
/// </summary>
public class IncidentActionPlan
{
    public Guid Id { get; set; }
    public int Version { get; set; } = 1;
    public IapStatus Status { get; set; } = IapStatus.Draft;

    public string? CommandersIntent { get; set; }
    public List<IapObjective> Objectives { get; set; } = [];
    public List<IapPosition> Organization { get; set; } = [];
    public List<IapAssignment> Assignments { get; set; } = [];

    public string? CommunicationsPlan { get; set; }
    public string? MedicalPlan { get; set; }
    public string? SafetyMessage { get; set; }

    public Guid IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public Guid OperationalPeriodId { get; set; }
    public OperationalPeriod? OperationalPeriod { get; set; }

    public Guid? PreparedById { get; set; }
    public User? PreparedBy { get; set; }

    public Guid? ApprovedById { get; set; }
    public User? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

/// <summary>Strategic objective broken down into tactics, resources and a target (Design Document §8.2).</summary>
public class IapObjective
{
    public int Order { get; set; }
    public required string Statement { get; set; }
    public List<string> Tactics { get; set; } = [];
    public string? ResourcesSummary { get; set; }
    public string? PerformanceTarget { get; set; }
}

/// <summary>Who fills which ICS position for this period.</summary>
public class IapPosition
{
    public UserRole Role { get; set; }
    public required string Name { get; set; }
    public Guid? UserId { get; set; }
}

/// <summary>Resource → assignment, e.g. "Engine 12 → Building A".</summary>
public class IapAssignment
{
    public Guid ResourceId { get; set; }
    public required string Assignment { get; set; }

    /// <summary>ICS group or division the resource works under, e.g. "Medical Group".</summary>
    public string? Group { get; set; }
}
