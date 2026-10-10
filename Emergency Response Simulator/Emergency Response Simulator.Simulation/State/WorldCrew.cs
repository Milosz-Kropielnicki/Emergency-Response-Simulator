using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>Where a responder really is.</summary>
public enum ResponderState
{
    OnDuty,
    Trapped,

    /// <summary>Separated from their crew, e.g. lost in smoke.</summary>
    Lost,
    Injured,
    StoodDown,
}

/// <summary>One responder as they really are (Design Document §12): how tired and how stressed, and where.</summary>
public sealed class WorldResponder
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public CrewRole Role { get; init; }
    public IReadOnlyList<string> Qualifications { get; init; } = [];
    public IReadOnlyList<string> Lapsed { get; init; } = [];

    /// <summary>0 fresh to 1 spent.</summary>
    public double Fatigue { get; set; }

    /// <summary>Acute stress now, 0–1.</summary>
    public double Stress { get; set; }

    /// <summary>Accumulated exposure to traumatic events; only peer support brings it down.</summary>
    public double TraumaLoad { get; set; }

    public ResponderState State { get; set; } = ResponderState.OnDuty;

    /// <summary>When an acute stress reaction will take them off the crew, unless the stress eases first.</summary>
    public DateTimeOffset? StandDownAt { get; set; }

    public bool Available => State == ResponderState.OnDuty;

    /// <summary>"Firefighter Byrne", "Paramedic Walsh": how they are named on the radio.</summary>
    public string Title => $"{RoleTitle(Role)} {Name.Split(' ')[^1]}";

    public static string RoleTitle(CrewRole role) => role switch
    {
        CrewRole.Officer => "Sub-Officer",
        CrewRole.Driver or CrewRole.Firefighter => "Firefighter",
        CrewRole.Paramedic => "Paramedic",
        CrewRole.Emt => "EMT",
        CrewRole.Garda => "Garda",
        _ => "Technician",
    };
}

/// <summary>The crew riding a unit, as it really is (advanced by <c>CrewSystem</c>).</summary>
public sealed class WorldCrew
{
    public List<WorldResponder> Members { get; } = [];
    public bool Rostered => Members.Count > 0;

    public DateTimeOffset ShiftStart { get; set; }
    public DateTimeOffset ShiftEnd { get; set; }

    /// <summary>When the current spell of work at a scene began (reset by rehab).</summary>
    public DateTimeOffset? WorkingSince { get; set; }

    public DateTimeOffset? RehabSince { get; set; }

    /// <summary>A relief crew is taking over: the unit works at half pace until this time.</summary>
    public DateTimeOffset? HandoverUntil { get; set; }

    /// <summary>Withdrew from the building on an evacuation signal: working from outside (defensive) since.</summary>
    public bool Withdrawn { get; set; }

    /// <summary>A relief crew that was never told the building had been evacuated: it may go back inside.</summary>
    public bool UnawareOfEvacuation { get; set; }

    /// <summary>The Mayday this crew is working as the rescue team, if any: it does nothing else meanwhile.</summary>
    public Guid? Rescuing { get; set; }

    public DateTimeOffset? PeerSupportArrangedAt { get; set; }

    /// <summary>Orders the crew has acknowledged: what it believes it is meant to be doing.</summary>
    public List<string> Tasks { get; } = [];

    /// <summary>Orders a relief crew was never told about at a rushed handover.</summary>
    public List<string> ForgottenTasks { get; } = [];

    /// <summary>How many times this unit has changed crews (to roster distinct people).</summary>
    public int Reliefs { get; set; }

    public IEnumerable<WorldResponder> Available => Members.Where(m => m.Available);
    public int OnDuty => Members.Count(m => m.Available);

    /// <summary>The crew's average fatigue, or 0 with nobody rostered.</summary>
    public double Fatigue => Members.Any(m => m.Available) ? Members.Where(m => m.Available).Average(m => m.Fatigue) : 0;

    public double Stress => Members.Any(m => m.Available) ? Members.Where(m => m.Available).Average(m => m.Stress) : 0;

    public int Holding(string qualification) => Members.Count(m => m.Available && m.Qualifications.Contains(qualification));

    /// <summary>Replaces the crew with the people on the roster, as tired as their time on shift makes them.</summary>
    public void Roster(IReadOnlyList<CrewMemberInfo> members, DateTimeOffset shiftStart, TimeSpan shiftLength, DateTimeOffset now)
    {
        Members.Clear();
        ShiftStart = shiftStart;
        ShiftEnd = shiftStart + shiftLength;
        HandoverUntil = null;
        PeerSupportArrangedAt = null;
        var hours = Math.Max(0, (now - shiftStart).TotalHours);
        foreach (var member in members)
        {
            var noise = Hazards.SimRandom.For(member.MemberId).NextDouble();
            Members.Add(new WorldResponder
            {
                Id = member.MemberId, Name = member.Name, Role = member.Role, Qualifications = member.Qualifications,
                Lapsed = member.Lapsed ?? [], Fatigue = Math.Clamp(hours * FatiguePerHourOnShift + noise * 0.08, 0, 0.6),
                Stress = noise * 0.1,
            });
        }
    }

    /// <summary>Fatigue a long shift brings by itself (being awake, travelling, routine calls).</summary>
    public const double FatiguePerHourOnShift = 0.035;
}

/// <summary>A firefighter really in trouble, and how the rescue is going.</summary>
public sealed class WorldDistress
{
    public Guid Id { get; init; }
    public Guid UnitId { get; init; }
    public Guid MemberId { get; init; }
    public required string Cause { get; init; }
    public GeoPoint Location { get; init; }

    /// <summary>Trapped (needs rescuing) rather than lost (may find their own way out).</summary>
    public bool Trapped { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset AirRunsOutAt { get; init; }

    /// <summary>Command has heard (or declared) a Mayday for it.</summary>
    public bool Heard { get; set; }
    public HashSet<Guid> MaydayIds { get; } = [];
    public DateTimeOffset? LastCallAt { get; set; }
    public int Calls { get; set; }

    public Guid? RescueUnitId { get; set; }

    /// <summary>0–1 of the way through the rescue.</summary>
    public double RescueProgress { get; set; }

    public bool Ended { get; set; }
    public string? Outcome { get; set; }
}

/// <summary>A PAR (or evacuation signal) as it really plays out: who heard it, who has answered.</summary>
public sealed class WorldPar
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>An evacuation signal: crews withdraw before they answer.</summary>
    public bool Evacuation { get; init; }

    /// <summary>Crews at the scene when it was called, who should answer.</summary>
    public HashSet<Guid> Involved { get; } = [];
    public Dictionary<Guid, DateTimeOffset> HeardBy { get; } = [];
    public HashSet<Guid> Answered { get; } = [];
}

/// <summary>A relief crew on its way to take over a unit.</summary>
public sealed class ReliefState
{
    public Guid Id { get; init; }
    public Guid UnitId { get; init; }
    public bool Briefing { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? ArriveAt { get; set; }
}

/// <summary>
/// How a crew's condition changes what it does (Design Document §12: "fatigue affects decision speed, radio discipline,
/// error rates"). A unit with nobody rostered (no crew model running) behaves as it always did.
/// </summary>
public static class CrewFactors
{
    /// <summary>How much longer everything takes: turning out, sizing up, reading back. 1 for a fresh crew, up to about 2.</summary>
    public static double SlowFactor(WorldUnit unit) =>
        unit.Crew.Rostered ? 1 + 0.8 * unit.Crew.Fatigue + 0.4 * Math.Max(0, unit.Crew.Stress - 0.3) : 1;

    /// <summary>Extra chance of a mistake: a garbled transmission, a wrong read-back. Nothing until a crew is tired.</summary>
    public static double ErrorRate(WorldUnit unit) =>
        unit.Crew.Rostered ? Math.Max(0, unit.Crew.Fatigue - 0.5) * 0.4 + Math.Max(0, unit.Crew.Stress - 0.5) * 0.3 : 0;

    /// <summary>
    /// How much work the crew gets done compared with a full, fresh crew working inside: fewer people, tiredness,
    /// working from outside after an evacuation, a handover in progress, a rescue.
    /// </summary>
    public static double Effectiveness(WorldUnit unit, DateTimeOffset now)
    {
        var crew = unit.Crew;
        if (crew.Rescuing is not null) return 0;
        var factor = crew.Withdrawn ? 0.6 : 1;
        if (crew.HandoverUntil is { } until && now < until) factor *= 0.5;
        if (!crew.Rostered) return factor;
        var headcount = (double)crew.OnDuty / crew.Members.Count;
        return factor * headcount * (1 - 0.5 * crew.Fatigue);
    }

    /// <summary>Fire crews go inside burning buildings; everyone else works outside.</summary>
    public static bool IsFireCrew(UnitType type) => ResourceGroups.AgencyFor(type) == AgencyType.Fire;

    /// <summary>"fresh", "tired" or "exhausted" for a fatigue level.</summary>
    public static string Band(double fatigue) => fatigue >= 0.8 ? "exhausted" : fatigue >= 0.5 ? "tired" : "fresh";
}
