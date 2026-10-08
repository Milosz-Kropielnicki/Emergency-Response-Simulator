using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>A mobile resource tracked by AVL (Design Document §6.4, §7.4).</summary>
public class Unit : Resource
{
    public Unit() => Kind = ResourceKind.Unit;

    public required string Callsign { get; set; }
    public UnitType Type { get; set; }
    public UnitStatus Status { get; set; } = UnitStatus.Available;

    public int CrewSize { get; set; }

    /// <summary>Equipment and qualifications, e.g. "BreathingApparatus", "ALS", "Swiftwater".</summary>
    public List<string> Capabilities { get; set; } = [];

    public string? HomeStation { get; set; }

    public double SpeedKph { get; set; }

    /// <summary>Degrees clockwise from north.</summary>
    public double Heading { get; set; }

    public DateTimeOffset? LastAvlUpdate { get; set; }

    /// <summary>Last time anything was heard from the unit: status change, AVL fix or report.</summary>
    public DateTimeOffset? LastContactAt { get; set; }

    /// <summary>False after a communication-failure alert, until the unit is heard from again.</summary>
    public bool CommsConnected { get; set; } = true;
    public TimeSpan? Eta { get; set; }

    /// <summary>The route the unit last reported it is following (COP state, not stored in the database).</summary>
    public LineString? PlannedRoute { get; set; }

    public double? RouteDistanceMeters { get; set; }

    public Guid? AssignedIncidentId { get; set; }
    public Incident? AssignedIncident { get; set; }
}
