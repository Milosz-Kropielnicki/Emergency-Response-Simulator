namespace Emergency_Response_Simulator.Core.Model;

/// <summary>A trainee, instructor or observer taking part in a session.</summary>
public class User
{
    public Guid Id { get; set; }
    public required string DisplayName { get; set; }
    public UserRole Role { get; set; }

    public Guid? AgencyId { get; set; }
    public Agency? Agency { get; set; }
}
