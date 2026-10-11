namespace Emergency_Response_Simulator.Core.Model;

/// <summary>A person in a networked session, as the event stream records them (Phase 11 M2).</summary>
public sealed class Participant
{
    public Guid Id { get; init; }
    public required string DisplayName { get; set; }
    public UserRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>Set while they are away (left, or dropped and not back); cleared when they rejoin.</summary>
    public DateTimeOffset? LeftAt { get; set; }

    public bool Present => LeftAt is null;

    public string Label => $"{DisplayName} ({Roles.Name(Role)})";
}

/// <summary>Rules about who may hold which role.</summary>
public static class Roles
{
    /// <summary>
    /// Positions one person holds at a time: the IC, the command staff and the section chiefs. Dispatchers, agency
    /// chiefs, instructors and observers can be several.
    /// </summary>
    public static bool IsExclusive(UserRole role) => role is UserRole.IncidentCommander or UserRole.Operations or UserRole.Planning
        or UserRole.Logistics or UserRole.FinanceAdmin or UserRole.Safety or UserRole.Liaison or UserRole.PublicInformation;

    /// <summary>Roles that see ground truth (until observers can be granted it, Phase 11 M6).</summary>
    public static bool SeesTruth(UserRole role) => role == UserRole.Instructor;

    public static string Name(UserRole role) => role switch
    {
        UserRole.IncidentCommander => "Incident Commander",
        UserRole.FinanceAdmin => "Finance/Admin",
        UserRole.PublicInformation => "Public Information Officer",
        UserRole.AgencyChief => "Agency chief",
        UserRole.Operations => "Operations Section Chief",
        UserRole.Planning => "Planning Section Chief",
        UserRole.Logistics => "Logistics Section Chief",
        UserRole.Safety => "Safety Officer",
        UserRole.Liaison => "Liaison Officer",
        _ => role.ToString(),
    };
}
