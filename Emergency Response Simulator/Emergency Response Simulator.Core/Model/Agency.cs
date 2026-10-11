namespace Emergency_Response_Simulator.Core.Model;

public class Agency
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ShortName { get; set; }
    public AgencyType Type { get; set; }

    /// <summary>City, county, state or federal area this agency answers for (Design Document §14).</summary>
    public string? Jurisdiction { get; set; }

    /// <summary>
    /// Run by the simulation, not the trainee (Design Document §10.3): its units take their own jobs and may act
    /// on their own initiative. Command sees them but cannot dispatch them. COP state, not stored in the database.
    /// </summary>
    public bool AiControlled { get; set; }

    /// <summary>The agency's own radio system when it is not on ours (COP state, not stored in the database).</summary>
    public string? RadioChannel { get; set; }

    /// <summary>
    /// What the agency's commander will push for at a joint incident, e.g. "Preserve the scene for the investigation"
    /// (Design Document §14: competing priorities). COP state, not stored in the database.
    /// </summary>
    public string? Priority { get; set; }

    /// <summary>Words an Incident Action Plan objective must use for the agency to agree it covers its priority.</summary>
    public List<string> PriorityTerms { get; set; } = [];

    /// <summary>How the agency feels about command, as its people last showed it (COP state).</summary>
    public AgencyStanding Standing { get; set; } = AgencyStanding.Good;

    public List<Resource> Resources { get; set; } = [];
    public List<User> Users { get; set; } = [];
}
