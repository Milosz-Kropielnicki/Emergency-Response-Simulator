namespace Emergency_Response_Simulator.Simulation.Instructor;

/// <summary>
/// Keys of the roles the simulation plays that the instructor can take over (Phase 11 M4). While a role is taken over
/// (<see cref="State.WorldState.HumanRoles"/>), the system that plays it leaves its decisions waiting for the instructor.
/// </summary>
public static class AiRoles
{
    /// <summary>Mutual-aid approvals (<c>CommandResponseSystem</c>).</summary>
    public const string RegionalDutyOfficer = "regional-duty-officer";

    /// <summary>Specialist teams, and mutual aid outside every agreement (<c>CommandResponseSystem</c>).</summary>
    public const string NationalDirectorate = "national-directorate";

    /// <summary>Replies to command's notifications: hospitals, utilities, the council… (<c>CommandResponseSystem</c>).</summary>
    public const string OutsideOrganisations = "outside-organisations";

    /// <summary>Journalists ringing with questions (<c>InteragencySystem</c>).</summary>
    public const string Media = "media";

    /// <summary>
    /// Another agency's control room: its routine jobs and initiative (<c>AgencyAiSystem</c>), and its answers to
    /// invitations to unified command and requests for units (<c>InteragencySystem</c>).
    /// </summary>
    public static string Agency(Guid agencyId) => $"agency:{agencyId}";

    public static Guid? AgencyOf(string key) =>
        key.StartsWith("agency:", StringComparison.Ordinal) && Guid.TryParse(key["agency:".Length..], out var id) ? id : null;
}
