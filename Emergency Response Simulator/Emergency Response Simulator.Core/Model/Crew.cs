using System.Text.RegularExpressions;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>What a responder does on their crew (Design Document §12).</summary>
public enum CrewRole
{
    Officer,
    Driver,
    Firefighter,
    Paramedic,
    Emt,
    Garda,
    Technician,
}

/// <summary>A crew's condition as its officer reports it. Crews tend to say they are fine for longer than they are.</summary>
public enum CrewCondition
{
    Fine,
    Tired,
    Exhausted,

    /// <summary>Upset by what they have seen or been through (a death, a Mayday, a child casualty).</summary>
    Shaken,
}

/// <summary>Where a responder is, as far as command knows (Design Document §12: accountability).</summary>
public enum MemberStatus
{
    OnDuty,

    /// <summary>Not accounted for at a PAR.</summary>
    Missing,

    /// <summary>Called a Mayday, or a Mayday was called for them.</summary>
    InDistress,
    Injured,

    /// <summary>Taken off the crew: an acute stress reaction, exhaustion, an injury treated on scene.</summary>
    StoodDown,
}

/// <summary>A qualification a task can need, and how many qualified members it takes.</summary>
public sealed record TaskRequirement(string Qualification, int Members, string Task);

/// <summary>
/// Skills and certifications (Design Document §12): not every crew can do every task. Each member holds some of these;
/// a certificate that has lapsed doesn't count.
/// </summary>
public static partial class Qualifications
{
    public const string BreathingApparatus = "BA";
    public const string Hazmat = "Hazmat";
    public const string Usar = "USAR";
    public const string Swiftwater = "Swiftwater";

    /// <summary>Advanced life support: a registered paramedic.</summary>
    public const string Als = "ALS";

    /// <summary>Basic life support: an emergency medical technician.</summary>
    public const string Bls = "BLS";

    public static IReadOnlyList<(string Code, string Name)> All { get; } =
    [
        (BreathingApparatus, "Breathing apparatus wearer"),
        (Hazmat, "Hazardous materials technician"),
        (Usar, "Urban search and rescue"),
        (Swiftwater, "Swiftwater and flood rescue"),
        (Als, "Paramedic (advanced life support)"),
        (Bls, "EMT (basic life support)"),
    ];

    public static string Name(string code) => All.FirstOrDefault(q => q.Code == code).Name ?? code;

    /// <summary>
    /// What an order asks for, from its wording: "decontaminate", "chlorine" need two Hazmat technicians; "water
    /// rescue" two Swiftwater; "collapse", "shoring" two USAR; "interior attack", "search the building" a BA team;
    /// "ALS", "paramedic" one paramedic. Null when the task needs nothing special.
    /// </summary>
    public static TaskRequirement? RequiredFor(string task)
    {
        if (HazmatTask().Match(task) is { Success: true } hazmat) return new(Hazmat, 2, hazmat.Value);
        if (SwiftwaterTask().Match(task) is { Success: true } water) return new(Swiftwater, 2, water.Value);
        if (UsarTask().Match(task) is { Success: true } usar) return new(Usar, 2, usar.Value);
        if (BaTask().Match(task) is { Success: true } ba) return new(BreathingApparatus, 2, ba.Value);
        if (AlsTask().Match(task) is { Success: true } als) return new(Als, 1, als.Value);
        return null;
    }

    [GeneratedRegex(@"\b(hazmat|decon\w*|chemical\w*|chlorine|leak\w*|gas[- ]tight|entry team|plug the)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HazmatTask();

    [GeneratedRegex(@"\b(water rescue|swift ?water|flood rescue|from the (canal|river|water)|boat)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SwiftwaterTask();

    [GeneratedRegex(@"\b(usar|collapse\w*|rubble|shor(e|ing)|tunnel\w*|void search)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsarTask();

    [GeneratedRegex(@"\b(interior|internal attack|offensive attack|breathing apparatus|BA team|BA entry|search the (building|warehouse|block|flats?)|rescue team|RIT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BaTask();

    [GeneratedRegex(@"\b(ALS|advanced life support|paramedic|intubat\w*|cardiac arrest)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlsTask();
}

// ---- COP side: the crews command knows it has ----

/// <summary>One responder on a crew, as the roster has them.</summary>
public sealed class CrewMember
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public CrewRole Role { get; init; }

    /// <summary>Current certificates.</summary>
    public IReadOnlyList<string> Qualifications { get; init; } = [];

    /// <summary>Certificates that have lapsed: trained once, but not allowed to do it now.</summary>
    public IReadOnlyList<string> Lapsed { get; init; } = [];

    public MemberStatus Status { get; set; } = MemberStatus.OnDuty;

    public bool Available => Status == MemberStatus.OnDuty;
}

/// <summary>
/// The people on a unit as command knows them (Design Document §12): who they are and what they can do, how long they
/// have been on shift and working, what they last said about their condition, rehab, relief and peer support.
/// Their real fatigue and stress are ground truth; command sees only time and what the crew tells it.
/// </summary>
public sealed class Crew
{
    public Guid UnitId { get; init; }
    public List<CrewMember> Members { get; } = [];

    public DateTimeOffset ShiftStart { get; set; }
    public DateTimeOffset ShiftEnd { get; set; }

    /// <summary>When the crew started its current spell of work at a scene (on task), or null when not working.</summary>
    public DateTimeOffset? WorkingSince { get; set; }

    /// <summary>When the crew went to rehab, or null.</summary>
    public DateTimeOffset? RehabSince { get; set; }

    public DateTimeOffset? LastRehabEnded { get; set; }

    public CrewCondition Condition { get; set; } = CrewCondition.Fine;
    public string? ConditionNote { get; set; }
    public DateTimeOffset? ConditionReportedAt { get; set; }

    /// <summary>A relief crew has been asked for and is on its way.</summary>
    public DateTimeOffset? ReliefRequestedAt { get; set; }
    public bool ReliefBriefing { get; set; }
    public DateTimeOffset? RelievedAt { get; set; }

    public DateTimeOffset? PeerSupportArrangedAt { get; set; }
    public DateTimeOffset? PeerSupportGivenAt { get; set; }

    /// <summary>The Mayday this crew has been sent to as the rescue team, if any.</summary>
    public Guid? RescueFor { get; set; }

    public int OnDuty => Members.Count(m => m.Available);

    /// <summary>Members holding a current certificate.</summary>
    public int Holding(string qualification) => Members.Count(m => m.Available && m.Qualifications.Contains(qualification));

    public bool Meets(TaskRequirement requirement) => Holding(requirement.Qualification) >= requirement.Members;

    public TimeSpan OnShift(DateTimeOffset now) => now - ShiftStart;
    public bool PastShiftEnd(DateTimeOffset now) => now >= ShiftEnd;

    /// <summary>Time working since the crew last rested (rehab) or arrived.</summary>
    public TimeSpan OnTask(DateTimeOffset now) => WorkingSince is { } since && RehabSince is null ? now - since : TimeSpan.Zero;

    public bool InRehab => RehabSince is not null;
}

/// <summary>A Personnel Accountability Report: every crew at the incident says how many of its people it can account for.</summary>
public sealed class ParCheck
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>Units expected to answer, by id, with their call signs.</summary>
    public Dictionary<Guid, string> Expected { get; } = [];

    public Dictionary<Guid, ParResponse> Responses { get; } = [];

    public bool Complete => Expected.Keys.All(Responses.ContainsKey);
    public IEnumerable<string> Outstanding => Expected.Where(e => !Responses.ContainsKey(e.Key)).Select(e => e.Value);
    public bool AllAccounted => Complete && Responses.Values.All(r => r.Accounted >= r.Expected);
}

public sealed record ParResponse(Guid UnitId, string Callsign, int Accounted, int Expected, IReadOnlyList<string> Missing, DateTimeOffset At);

/// <summary>A firefighter in trouble, as command heard it (Design Document §12: Mayday).</summary>
public sealed class Mayday
{
    public Guid Id { get; init; }
    public Guid UnitId { get; init; }
    public required string Callsign { get; init; }
    public string? Member { get; init; }
    public Guid? IncidentId { get; init; }

    /// <summary>What was heard: who, where, what's wrong (garbled if the call was).</summary>
    public required string Details { get; set; }
    public bool Unclear { get; set; }
    public DateTimeOffset DeclaredAt { get; init; }

    public Guid? RescueUnitId { get; set; }
    public string? RescueCallsign { get; set; }
    public DateTimeOffset? RescueDeployedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Outcome { get; set; }

    public bool Active => ResolvedAt is null;
}
