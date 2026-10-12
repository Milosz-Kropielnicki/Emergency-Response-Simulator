namespace Emergency_Response_Simulator.Core.Model;

/// <summary>Who in public life rings command (Phase 11 M5), in the order the pressure climbs.</summary>
public enum PoliticalOffice
{
    /// <summary>The local TD (member of the Dáil), on behalf of constituents.</summary>
    LocalTd = 1,

    /// <summary>The council's chief executive, for the local authority.</summary>
    CouncilChiefExecutive = 2,

    /// <summary>The minister's office, for the government.</summary>
    MinistersOffice = 3,
}

/// <summary>A politician's call to command, as the COP holds it.</summary>
public sealed class PoliticalCall
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public PoliticalOffice Office { get; init; }
    public required string Caller { get; init; }
    public required string Message { get; init; }

    /// <summary>1–3: how hard they are pressing, higher each time a call went unanswered.</summary>
    public int Pressure { get; init; }

    /// <summary>"Incident Commander" or "Public Information Officer".</summary>
    public required string To { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public string? Answer { get; set; }

    public bool Waiting => AnsweredAt is null;
}

/// <summary>How the media cover the response (Phase 12), from the public's confidence in it.</summary>
public enum MediaTone
{
    Supportive,
    Mixed,
    Critical,
}

/// <summary>Who runs an agency's control room (Phase 11 M5: the role-player registry).</summary>
public enum AgencyController
{
    /// <summary>Ours: command dispatches its units.</summary>
    Command,

    /// <summary>Another agency, played by the simulation.</summary>
    Simulation,

    /// <summary>Another agency, played by a participant (an agency chief).</summary>
    Participant,
}
