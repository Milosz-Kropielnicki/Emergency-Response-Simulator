using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

// Multi-agency command and interoperability (Design Document §14). Live COP state rebuilt from the event stream.

/// <summary>
/// Tiers of government an area belongs to: the design document's city, county, state and federal. Ireland has no
/// states, so the regional tier (the Major Emergency region) and the national government stand in for the last two.
/// </summary>
public enum JurisdictionLevel
{
    City,
    County,
    Regional,
    National,
}

/// <summary>An area some authority answers for. Areas overlap: a point in the city is also in the region and the state.</summary>
public sealed class Jurisdiction
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JurisdictionLevel Level { get; init; }

    /// <summary>Who answers for the area, e.g. "Dublin City Council".</summary>
    public required string Authority { get; init; }

    /// <summary>The agency in the session that speaks for the authority, if there is one.</summary>
    public Guid? AuthorityAgencyId { get; init; }

    public required Geometry Boundary { get; init; }

    /// <summary>
    /// Measures inside need the authority's agreement (a port estate, a canal basin): command declaring a zone that
    /// reaches in without it starts an authority dispute.
    /// </summary>
    public bool RequiresConsent { get; init; }

    /// <summary>What the authority controls there, e.g. "berths, moorings and access to the quays".</summary>
    public string? Powers { get; init; }
}

/// <summary>How an agency feels about command, judged from how its people talk (Design Document §14: friction).</summary>
public enum AgencyStanding
{
    Good,
    Strained,
    Hostile,
}

/// <summary>Another agency's commander sharing command of an incident (Unified Command, §14).</summary>
public sealed class UnifiedCommandMember
{
    public Guid? AgencyId { get; init; }
    public required string Agency { get; init; }
    public required string Commander { get; init; }
    public DateTimeOffset JoinedAt { get; init; }

    /// <summary>What the agency said it will push for when it joined.</summary>
    public string? Priority { get; init; }
}

/// <summary>Command asked an agency to send its commander to the command post and share command.</summary>
public sealed class UnifiedCommandInvitation
{
    public Guid IncidentId { get; init; }
    public Guid AgencyId { get; init; }
    public required string Agency { get; init; }
    public DateTimeOffset InvitedAt { get; init; }
    public bool? Accepted { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}

/// <summary>Command asked another agency to commit some of its own units to an incident.</summary>
public sealed class AssistanceRequest
{
    public Guid Id { get; init; }
    public Guid AgencyId { get; init; }
    public required string Agency { get; init; }
    public Guid IncidentId { get; init; }
    public int Quantity { get; init; }
    public required string Task { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public int? Granted { get; set; }
    public List<Guid> UnitIds { get; } = [];
    public string? Reason { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

// ---- Emergency Operations Centre ----

/// <summary>
/// How far the Emergency Operations Centre is stood up (Design Document §14): from normal operations (a duty officer
/// on call) through monitoring and partial activation to full activation, each with more people and more reach.
/// </summary>
public enum EocLevel
{
    Normal,
    Monitoring,
    Partial,
    Full,
}

/// <param name="From">The lowest level at which the seat is filled.</param>
public sealed record EocSeatInfo(string Function, string? Agency, EocLevel From);

/// <summary>Who staffs the EOC at each level, how long it takes, and what each level brings.</summary>
public static class EocLevels
{
    public static readonly IReadOnlyList<EocSeatInfo> Seats =
    [
        new("Duty emergency management officer", "Dublin City Council", EocLevel.Monitoring),
        new("Fire service liaison", "City Fire Brigade", EocLevel.Monitoring),
        new("EOC manager", "Dublin City Council", EocLevel.Partial),
        new("Garda liaison", "Garda Síochána", EocLevel.Partial),
        new("Health service liaison", "HSE", EocLevel.Partial),
        new("Information officer (joint information centre)", null, EocLevel.Partial),
        new("Situation unit", null, EocLevel.Partial),
        new("Logistics", "Dublin City Council", EocLevel.Full),
        new("Welfare: rest centres and buses", "Dublin City Council", EocLevel.Full),
        new("Utilities and transport liaison", null, EocLevel.Full),
        new("Finance and administration", "Dublin City Council", EocLevel.Full),
        new("Government liaison (national coordination)", "Department of Housing, Local Government and Heritage", EocLevel.Full),
    ];

    public static IReadOnlyList<EocSeatInfo> SeatsAt(EocLevel level) => Seats.Where(s => s.From <= level && s.From != EocLevel.Normal).ToList();

    /// <summary>How long it takes to get everyone in from normal operations.</summary>
    public static TimeSpan StaffingTime(EocLevel level) => level switch
    {
        EocLevel.Monitoring => TimeSpan.FromMinutes(5),
        EocLevel.Partial => TimeSpan.FromMinutes(15),
        EocLevel.Full => TimeSpan.FromMinutes(30),
        _ => TimeSpan.Zero,
    };

    /// <summary>How often the EOC wants a situation report, or null when it isn't watching.</summary>
    public static TimeSpan? SitrepInterval(EocLevel level) => level switch
    {
        EocLevel.Monitoring => TimeSpan.FromMinutes(60),
        EocLevel.Partial => TimeSpan.FromMinutes(30),
        EocLevel.Full => TimeSpan.FromMinutes(20),
        _ => null,
    };

    public static string Label(EocLevel level) => level switch
    {
        EocLevel.Normal => "Normal operations",
        EocLevel.Monitoring => "Monitoring",
        EocLevel.Partial => "Partial activation",
        _ => "Full activation",
    };

    /// <summary>What the level brings, and what it costs.</summary>
    public static string Implications(EocLevel level) => level switch
    {
        EocLevel.Normal => "A duty officer on call. Disputes can't be escalated; agencies keep their own reserves.",
        EocLevel.Monitoring => "2 staff watch the incident and want an hourly situation report. No extra reach yet.",
        EocLevel.Partial => "7 staff. Declares a major emergency: agencies release half their reserves, mutual-aid decisions come 40% sooner, " +
                            "disputes can be escalated, the joint information centre coordinates public messages, and agencies' " +
                            "information is relayed. Situation reports every 30 min.",
        _ => "12 staff, with overtime and national coordination. Agencies release all their reserves, and the council can provide rest " +
             "centres and buses. Situation reports every 20 min.",
    };
}

public sealed class EocSeat
{
    public required string Function { get; init; }
    public string? Agency { get; init; }
    public EocLevel From { get; init; }
    public DateTimeOffset StaffedAt { get; init; }
}

/// <summary>The Emergency Operations Centre as command knows it.</summary>
public sealed class EocStatus
{
    /// <summary>The level the EOC has reached (fully staffed).</summary>
    public EocLevel Level { get; set; }

    public DateTimeOffset? LevelSince { get; set; }

    /// <summary>The level asked for, while the EOC is still staffing up to it.</summary>
    public EocLevel? Requested { get; set; }

    public DateTimeOffset? RequestedAt { get; set; }
    public string? Reason { get; set; }
    public List<EocSeat> Seats { get; } = [];
    public DateTimeOffset? SitrepDue { get; set; }
    public DateTimeOffset? LastSitrepAt { get; set; }
    public int SitrepsSubmitted { get; set; }

    public EocStatus Copy()
    {
        var copy = new EocStatus
        {
            Level = Level, LevelSince = LevelSince, Requested = Requested, RequestedAt = RequestedAt, Reason = Reason,
            SitrepDue = SitrepDue, LastSitrepAt = LastSitrepAt, SitrepsSubmitted = SitrepsSubmitted,
        };
        copy.Seats.AddRange(Seats);
        return copy;
    }
}

// ---- Authority disputes ----

public enum DisputeKind
{
    /// <summary>Another agency claims the lead, e.g. the police for a suspected crime scene.</summary>
    LeadAgency,

    /// <summary>A measure reaches into an area another authority answers for.</summary>
    Jurisdiction,

    /// <summary>Mutual-aid crews asked to do something their agreement doesn't cover.</summary>
    MutualAidTerms,
}

/// <summary>How command answers an objection.</summary>
public enum DisputeResolution
{
    /// <summary>Let the other agency have its way.</summary>
    Concede,

    /// <summary>Talk it through to a joint arrangement: takes time, keeps relations.</summary>
    Negotiate,

    /// <summary>Overrule them now: quick, but they remember it.</summary>
    Assert,

    /// <summary>Refer it to the EOC to decide (needs partial activation or above).</summary>
    Escalate,
}

public enum DisputeStatus
{
    Open,
    Negotiating,
    Escalated,
    Resolved,
}

public sealed class AuthorityDispute
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public DisputeKind Kind { get; init; }
    public Guid? AgencyId { get; init; }
    public required string Agency { get; init; }
    public required string Subject { get; init; }

    /// <summary>What the agency wants.</summary>
    public required string Position { get; init; }

    /// <summary>Why it thinks it has the right: a statute, an agreement, its jurisdiction.</summary>
    public required string Basis { get; init; }

    public DateTimeOffset RaisedAt { get; init; }
    public Guid? ZoneId { get; init; }
    public Guid? OrderId { get; init; }
    public DisputeStatus Status { get; set; } = DisputeStatus.Open;
    public DisputeResolution? Resolution { get; set; }
    public DateTimeOffset? ChosenAt { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public bool IsOpen => Status != DisputeStatus.Resolved;
}

/// <summary>What each way of answering a dispute is likely to do, for the buttons' tooltips.</summary>
public static class DisputeAdvice
{
    public static string Consequence(DisputeKind kind, DisputeResolution resolution) => (kind, resolution) switch
    {
        (DisputeKind.LeadAgency, DisputeResolution.Concede) => "They take the lead now. While the fire still burns, firefighting goes defensive to preserve the scene.",
        (DisputeKind.LeadAgency, DisputeResolution.Negotiate) => "A few minutes at the command post: usually a staged handover once the fire is out and casualties are clear.",
        (DisputeKind.LeadAgency, DisputeResolution.Assert) => "You keep the lead at once; the agency stops cooperating as readily and keeps what it knows to itself.",
        (DisputeKind.Jurisdiction, DisputeResolution.Concede) => "They handle their own area: people there are warned later, by them.",
        (DisputeKind.Jurisdiction, DisputeResolution.Negotiate) => "A joint measure across the boundary in a few minutes; until then the zone stops at their boundary.",
        (DisputeKind.Jurisdiction, DisputeResolution.Assert) => "The zone applies across the boundary at once; relations with the authority suffer.",
        (DisputeKind.MutualAidTerms, DisputeResolution.Concede) => "The order is withdrawn; the crews stay within their agreement.",
        (DisputeKind.MutualAidTerms, DisputeResolution.Negotiate) => "Their officer may agree to the task with conditions, after a few minutes.",
        (DisputeKind.MutualAidTerms, DisputeResolution.Assert) => "They do it under protest, and their service is slower to help next time.",
        (_, DisputeResolution.Escalate) => "The EOC decides after a few minutes (needs partial activation or above).",
        _ => "",
    };
}

// ---- Mutual aid ----

/// <summary>A standing agreement with a neighbouring service: what it will send, who approves, and on what terms.</summary>
public sealed class MutualAidAgreement
{
    public Guid Id { get; init; }
    public required string Provider { get; init; }
    public AgencyType ProviderType { get; init; }
    public string? ProviderJurisdiction { get; init; }
    public List<UnitType> UnitTypes { get; init; } = [];
    public int MaxUnits { get; init; }
    public required string ApprovedBy { get; init; }
    public TimeSpan DecisionTime { get; init; }
    public TimeSpan LeadTime { get; init; }

    /// <summary>Only once a major emergency has been declared (the EOC at partial activation or above).</summary>
    public bool NeedsMajorEmergency { get; init; }

    public required string Terms { get; init; }

    /// <summary>Tasks the agreement doesn't cover, as words that would appear in an order.</summary>
    public List<string> ExcludedTasks { get; init; } = [];

    public string? RadioChannel { get; init; }

    /// <summary>Units already sent under the agreement this session.</summary>
    public int Provided { get; set; }

    public bool Covers(UnitType type) => UnitTypes.Contains(type);
}

// ---- Public information ----

/// <summary>A statement to the public: ours (through the PIO or the IC) or another agency's.</summary>
public sealed class PublicStatement
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Issuer { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>Agreed through a joint information centre, so every agency says the same thing.</summary>
    public bool Joint { get; init; }

    /// <summary>Another agency's statement that contradicts the official line.</summary>
    public bool Conflicting { get; init; }

    public bool Ours => !Conflicting;
}

public sealed class MediaEnquiry
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Outlet { get; init; }
    public required string Question { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public string? AnsweredBy { get; set; }
}

/// <summary>Shared rules for the Phase 10 friction model, used by C2, the simulation and the bot.</summary>
public static class Interagency
{
    /// <summary>Words that make a public statement a warning (tell people what to do).</summary>
    public static bool IsWarning(string text) =>
        new[] { "evacuat", "shelter", "stay indoors", "close windows", "keep windows", "avoid the area", "leave the area" }
            .Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static AgencyStanding Worse(AgencyStanding standing) => standing == AgencyStanding.Good ? AgencyStanding.Strained : AgencyStanding.Hostile;

    public static AgencyStanding Better(AgencyStanding standing) => standing == AgencyStanding.Hostile ? AgencyStanding.Strained : AgencyStanding.Good;

    /// <summary>
    /// Unified command members whose priority no objective in the plan covers (Design Document §14: shared command means
    /// every member signs the plan, and none will sign one that ignores what it is there for).
    /// </summary>
    public static IReadOnlyList<UnifiedCommandMember> UncoveredPriorities(IapContent content, IncidentCommand command, IEnumerable<Agency> agencies)
    {
        var text = string.Join(' ', new[] { content.CommandersIntent ?? "" }
            .Concat(content.Objectives.Select(o => o.Statement))
            .Concat(content.OperationalObjectives.SelectMany(o => new[] { o.Statement, o.PerformanceTarget ?? "" })));
        var byId = agencies.Where(a => a.PriorityTerms.Count > 0).ToDictionary(a => a.Id);
        return command.Unified
            .Where(m => m.AgencyId is { } id && byId.TryGetValue(id, out var agency)
                        && !agency.PriorityTerms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Zones that restrict or move people, and so need the area authority's agreement.</summary>
    public static bool NeedsConsent(ZoneType type) => type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace or ZoneType.HotZone
        or ZoneType.WarmZone or ZoneType.ColdZone or ZoneType.PoliceCordon or ZoneType.RoadClosure or ZoneType.FireExclusion
        or ZoneType.IncidentPerimeter;
}
