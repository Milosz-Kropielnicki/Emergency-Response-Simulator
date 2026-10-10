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

    public List<Resource> Resources { get; set; } = [];
    public List<User> Users { get; set; } = [];
}
