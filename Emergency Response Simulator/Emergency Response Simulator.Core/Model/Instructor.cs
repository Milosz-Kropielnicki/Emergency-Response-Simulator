namespace Emergency_Response_Simulator.Core.Model;

public enum InjectStatus
{
    /// <summary>Waiting for its time (and its condition, if it has one).</summary>
    Pending,
    Held,
    Fired,
    Skipped,

    /// <summary>Its window passed without its condition being met.</summary>
    Expired,
}

/// <summary>A scripted inject as the instructor console lists it.</summary>
/// <param name="At">Time after the exercise starts.</param>
/// <param name="Conditional">Waits for something in the world as well as its time (e.g. "once a release has run 4 min").</param>
public sealed record InjectInfo(int Number, TimeSpan At, string Description, InjectStatus Status, bool Conditional, DateTimeOffset? FiredAt);

public enum InjectParameterKind
{
    Text,
    Number,
    Choice,
}

public sealed record InjectParameter(string Key, string Label, InjectParameterKind Kind, string Default, IReadOnlyList<string>? Choices = null);

/// <summary>An ad-hoc inject the instructor can fill in and fire: a wind shift, a secondary explosion, a 999 call…</summary>
public sealed record InjectTemplate(string Key, string Name, string Description, IReadOnlyList<InjectParameter> Parameters);

public enum RolePlayKind
{
    /// <summary>A crew or anyone else on a radio channel.</summary>
    Radio,

    /// <summary>Another agency's control room, a utility or a hospital, by phone or message.</summary>
    Agency,

    /// <summary>A member of the public ringing 999.</summary>
    Caller,

    /// <summary>A journalist ringing command with a question.</summary>
    Journalist,

    /// <summary>An official asking command for a decision (e.g. authority to evacuate).</summary>
    Official,
}

/// <summary>What the instructor says, and as whom.</summary>
/// <param name="Channel">The radio channel, for <see cref="RolePlayKind.Radio"/>.</param>
/// <param name="IncidentId">The incident it is about, for a journalist or an official.</param>
/// <param name="Location">Where a caller is, roughly.</param>
public sealed record RolePlayRequest(RolePlayKind Kind, string As, string Text, string? Channel = null, Guid? IncidentId = null,
    Geo.GeoPoint? Location = null);

/// <summary>A role the simulation plays that the instructor can take over (the role-player registry).</summary>
/// <param name="System">The simulation system that plays it when nobody has taken it over.</param>
/// <param name="Waiting">Decisions waiting for the instructor while they have it.</param>
public sealed record AiRoleInfo(string Key, string Name, string System, string Covers, bool TakenOver, int Waiting);

public enum AiRoleItemKind
{
    /// <summary>A resource request: approve (all or some) or decline.</summary>
    ResourceRequest,

    /// <summary>A notification: reply.</summary>
    Notification,

    /// <summary>An invitation to unified command: join or decline.</summary>
    Invitation,

    /// <summary>A request for another agency's units: grant some, or refuse.</summary>
    Assistance,
}

/// <summary>Something waiting for the instructor in a role they have taken over.</summary>
/// <param name="Quantity">How many were asked for, where that applies.</param>
public sealed record AiRoleItem(Guid Id, string RoleKey, string Role, AiRoleItemKind Kind, string Summary, DateTimeOffset Since, int? Quantity = null);

/// <summary>The instructor's answer, as the role: approve or not, how many, and in what words.</summary>
public sealed record AiRoleDecision(Guid ItemId, bool Approve, int? Quantity = null, string? Text = null);
