using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>How a metric or decision compares with the standard (Design Document §22).</summary>
public enum Rating
{
    /// <summary>Nothing to judge (it didn't come up).</summary>
    NotApplicable,
    Good,
    Fair,
    Poor,
}

/// <summary>An overall judgement for certification.</summary>
public enum Competence
{
    Competent,
    Developing,
    NotYetCompetent,
}

/// <param name="Value">The number behind the metric, for sorting and charts; null when there is nothing to measure.</param>
/// <param name="Display">How it reads: "2 min 15 s", "73 %", "4 of 6".</param>
/// <param name="Standard">What good looks like: "under 2 min".</param>
public sealed record AarMetric(string Category, string Name, double? Value, string Display, string Standard, Rating Rating, string Explanation);

/// <summary>
/// One thing the trainee did, in context (§22: decision timelines): what prompted it, how long it took, what they knew
/// and what was really going on.
/// </summary>
/// <param name="Trigger">What the decision answered, e.g. "Roof warning from Engine 4 at 14:17:42".</param>
/// <param name="ResponseTime">From the trigger to the decision, when there was one.</param>
/// <param name="Known">What the COP showed at the time.</param>
/// <param name="Truth">What was really happening at the time.</param>
public sealed record AarDecision(
    long Sequence,
    DateTimeOffset At,
    string Category,
    string Decision,
    string? Trigger,
    TimeSpan? ResponseTime,
    string Known,
    string Truth,
    Rating Rating,
    string? Note,
    Guid? ActorId = null,
    string? Actor = null);

/// <summary>A message or event in the replayable timeline (decisions, radio, dispatches, what happened).</summary>
/// <param name="ActorId">The participant whose command it was (networked sessions, Phase 11).</param>
/// <param name="RolePlay">Something the instructor said or decided as someone in the world (Phase 11 M4).</param>
/// <param name="AboutId">For an assessment note, the participant it is about (Phase 11 M6), so their timeline shows it.</param>
/// <param name="ClipId">A recorded transmission to play back (Phase 11 M7).</param>
public sealed record AarTimelineEntry(long Sequence, DateTimeOffset At, string Category, string Text, bool IsDecision, bool IsTruth, bool Important,
    Guid? ActorId = null, string? Actor = null, bool RolePlay = false, Guid? AboutId = null, Guid? ClipId = null);

/// <summary>
/// What the instructor did to run the exercise (Phase 11 M4): injects fired, held, released and skipped, role-play,
/// roles taken over and handed back, and bookmarks.
/// </summary>
/// <param name="Kind">e.g. "Inject fired early", "Role-play", "Decision", "Bookmark".</param>
public sealed record AarInstructorAction(DateTimeOffset At, string Kind, string Text, string? By);

/// <summary>
/// One participant in a networked session (Phase 11 M2): when they were there, the roles they held, and how their own
/// decisions rated.
/// </summary>
/// <param name="Present">Time in the session (joined to left, or to the end).</param>
/// <param name="Score">Share of their rated decisions that were good (fair counts half), 0–100; null with none rated.</param>
/// <param name="Focus">What most of their commands were about, e.g. "Dispatch 12, Order 5".</param>
public sealed record AarParticipant(Guid Id, string Name, string Roles, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt, TimeSpan Present,
    int Commands, int Good, int Fair, int Poor, TimeSpan? MeanResponse, int? Score, string Focus,
    int Competent = 0, int Developing = 0, int NotYetCompetent = 0, int? ObserverScore = null, int? Combined = null, AssessmentRating? Level = null);

/// <summary>
/// An observer's (or the instructor's) note on a participant (Phase 11 M6), with the event it was about.
/// </summary>
/// <param name="About">What the event it was tagged to said, e.g. "Engine 4 dispatched to INC-00001".</param>
public sealed record AarNote(DateTimeOffset At, Guid ParticipantId, string Participant, AssessmentRating? Rating, string Note, string? By,
    long? AboutSequence, string? About, Competency? Area = null);

/// <summary>
/// How observers rated one participant in one competency area (Phase 12): the rated notes in that area, averaged on the
/// rubric's scale and put back on it.
/// </summary>
public sealed record AarCompetency(Guid ParticipantId, string Participant, Competency Area, int Ratings, int Score, AssessmentRating Level);

/// <summary>
/// Something that would be hard to defend at an inquiry (Phase 14): an evacuation ordered without the authority to
/// order it, vulnerable people left behind, looting in an unguarded zone, a denial that was untrue…
/// </summary>
/// <param name="Mitigated">Command put it right in time (e.g. consulted the Garda as soon as they rang).</param>
public sealed record AarLiability(DateTimeOffset At, string Kind, string Text, bool Mitigated = false);

/// <summary>The decision log against the decisions that needed one (Phase 14).</summary>
/// <param name="Undocumented">The significant decisions with no log entry, described.</param>
public sealed record AarDocumentation(int Significant, int Documented, IReadOnlyList<string> Undocumented)
{
    public int Percent => Significant == 0 ? 100 : (int)Math.Round(100.0 * Documented / Significant);
}

/// <summary>A supply that ran out at an incident (Phase 15): how long crews went without, and whether the warning was acted on.</summary>
/// <param name="To">When the next delivery arrived; null if it never did.</param>
/// <param name="WarnedAt">When the supply unit first said it was running low.</param>
/// <param name="OrderedAt">The first order for it after the warning (or the outage).</param>
public sealed record AarSupplyOutage(string Incident, SupplyKind Kind, DateTimeOffset From, DateTimeOffset? To, DateTimeOffset? WarnedAt, DateTimeOffset? OrderedAt);

/// <summary>A breakdown (Phase 15), whether the crew had warned of it, and how long the unit was off the road.</summary>
public sealed record AarBreakdown(string Unit, DateTimeOffset At, string Fault, DateTimeOffset? WarnedAt, DateTimeOffset? RepairRequestedAt, DateTimeOffset? BackAt);

/// <summary>Logistics and finance over the session (Phase 15).</summary>
/// <param name="Costs">The finance section's last figures, by category.</param>
/// <param name="Claimed">Reimbursement claimed from central government, and approved.</param>
public sealed record AarLogistics(
    IReadOnlyList<CostLine> Costs,
    decimal Total,
    decimal Claimed,
    decimal Approved,
    IReadOnlyList<AarSupplyOutage> Outages,
    IReadOnlyList<AarBreakdown> Breakdowns,
    int Orders,
    int RanDry,
    int Serviced)
{
    public static readonly AarLogistics None = new([], 0, 0, 0, [], [], 0, 0, 0);
    public bool Any => Total > 0 || Orders > 0 || Outages.Count > 0 || Breakdowns.Count > 0 || RanDry > 0;
}

/// <summary>Medical surge, the dead, public health and behavioural health over the session (Phase 16).</summary>
/// <param name="RightFirstTime">Patients whose first hospital could give them the care they needed.</param>
/// <param name="DiedAwaitingTransport">Tagged patients who died before they left the scene.</param>
/// <param name="ProtectedInTime">Exposed people given the countermeasure within its window, of <paramref name="Exposed"/>.</param>
public sealed record AarHealth(
    int Tagged,
    int Arrived,
    int RightFirstTime,
    int Transfers,
    int DiedAwaitingTransport,
    int DiedInHospital,
    int Fatalities,
    TimeSpan? CoronerAfter,
    TimeSpan? LongestAtScene,
    int Identified,
    int Exposed,
    int ProtectedInTime,
    int Ill,
    int Distressed,
    int SeenByCounsellors,
    int WorriedWellPeak)
{
    public static readonly AarHealth None = new(0, 0, 0, 0, 0, 0, 0, null, null, 0, 0, 0, 0, 0, 0, 0);
    public bool Any => Tagged > 0 || Fatalities > 0 || Exposed > 0 || Distressed > 0;
    public int RightFirstTimePercent => Arrived == 0 ? 100 : (int)Math.Round(100.0 * RightFirstTime / Arrived);
}

/// <summary>Evacuation, shelter, infrastructure, cyber and GPS, and the environment over the session (Phase 17).</summary>
/// <param name="Population">Everyone really in the evacuation zones when they were ordered; <paramref name="Out"/> of them got out.</param>
/// <param name="LongestClearanceMinutes">The slowest zone to get 90% of those able to leave out; null when none got there.</param>
/// <param name="ShelterArrivals">Evacuees in rest centres at the most (each centre at its fullest); <paramref name="Registered"/> of them were registered.</param>
/// <param name="ShelterPeak">The fullest rest centre at its fullest, as a percentage of its capacity.</param>
/// <param name="InfrastructureFailures">Assets their operators reported failed or leaking.</param>
/// <param name="ToWaterLitres">Contaminated run-off that reached a river or canal; <paramref name="ContainedLitres"/> was held back.</param>
public sealed record AarProtection(
    int Zones,
    int Population,
    int Out,
    int StillInside,
    int HurtInZones,
    double? LongestClearanceMinutes,
    int UnsafeReEntries,
    int ShelterArrivals,
    int Registered,
    int ShelterPeak,
    int ShelterIncidents,
    int UnshelteredPeak,
    int InfrastructureFailures,
    int GasIgnitions,
    int CyberAttacks,
    int GnssInterference,
    double RunoffLitres,
    double ToWaterLitres,
    double ContainedLitres,
    int Wildlife)
{
    public static readonly AarProtection None = new(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool Any => Zones > 0 || ShelterArrivals > 0 || UnshelteredPeak > 0 || InfrastructureFailures > 0 || CyberAttacks > 0 || GnssInterference > 0
                       || RunoffLitres > 0 || Wildlife > 0;

    public int OutPercent => Population == 0 ? 100 : (int)Math.Round(100.0 * Out / Population);
}

/// <summary>How the session was run as training (Phase 19): the scenario, difficulty, random events and the incidents within the incident.</summary>
/// <param name="Adjustments">"14:20 raised to 4 (score 86)", in order.</param>
/// <param name="Complications">"Secondary device made safe 12 min after it was placed", one per incident within the incident.</param>
public sealed record AarTraining(
    string? Scenario,
    DifficultyMode Mode,
    int StartLevel,
    int EndLevel,
    IReadOnlyList<string> Adjustments,
    IReadOnlyList<int> Scores,
    IReadOnlyList<string> RandomEvents,
    IReadOnlyList<string> Complications,
    int RespondersHurt)
{
    public static readonly AarTraining None = new(null, DifficultyMode.Fixed, 3, 3, [], [], [], [], 0);

    public bool Any => Scenario is not null || Adjustments.Count > 0 || RandomEvents.Count > 0 || Complications.Count > 0 || Scores.Count > 0;
}

/// <summary>A unit's status over the session, for the commitment chart.</summary>
public sealed record AarStatusSpan(DateTimeOffset From, DateTimeOffset To, UnitStatus Status);

/// <param name="Committed">Share of the session the unit was committed (dispatched to back in service).</param>
public sealed record AarUnitUsage(Guid UnitId, string Callsign, string Type, IReadOnlyList<AarStatusSpan> Spans, double Committed, TimeSpan? DelayedEnRoute);

/// <summary>
/// A grid of values over the incident area (§22: heatmaps): where resources were committed (unit-minutes) or where
/// they were held up (minutes stopped or crawling while en route).
/// </summary>
public sealed class AarHeatmap
{
    public required string Title { get; init; }
    public required string Unit { get; init; }
    public GeoPoint SouthWest { get; init; }
    public double CellMeters { get; init; }
    public int Columns { get; init; }
    public int Rows { get; init; }

    /// <summary>Row-major from the south-west corner: index = row * Columns + column.</summary>
    public required double[] Values { get; init; }

    /// <summary>The incident, for orientation.</summary>
    public GeoPoint? Focus { get; init; }

    /// <summary>The hottest cells, described: "Pearse Street: 12 unit-min".</summary>
    public IReadOnlyList<string> Hotspots { get; init; } = [];

    public double Max => Values.Length == 0 ? 0 : Values.Max();

    public GeoPoint CellCentre(int column, int row) =>
        GeoMath.Destination(GeoMath.Destination(SouthWest, 0, (row + 0.5) * CellMeters), 90, (column + 0.5) * CellMeters);
}

/// <summary>A hold-up while en route: where, how long, why (as far as the record shows).</summary>
public sealed record AarDelay(string Callsign, DateTimeOffset At, TimeSpan Lost, string Where, string Cause);

/// <summary>
/// A key decision point (§22: branching AAR): what the trainee did, what the recommended response would have been, and
/// the likely difference, estimated from what really happened in the session.
/// </summary>
/// <param name="Yours">"Evacuation signal 3 min 18 s after the first roof warning."</param>
/// <param name="Recommended">"Within 1 min of the first warning."</param>
/// <param name="Alternative">"No evacuation signal."</param>
/// <param name="Effect">"Two firefighters trapped; with the recommended timing all crews would have been out before the collapse."</param>
public sealed record AarBranchPoint(string Topic, DateTimeOffset? At, string Yours, string Recommended, string Alternative, string Effect, Rating Rating);

/// <summary>The outcome of one run of the scenario: the session itself, or a re-simulation with a different command policy.</summary>
/// <param name="FirefightersInDistress">Trapped, lost or out of air.</param>
/// <param name="FirstOnSceneMinutes">From the emergency starting to the first unit on scene.</param>
/// <param name="KeyActions">What command did, in order, e.g. "+3:15 Incident opened" (for comparing with the recommended response).</param>
public sealed record AarOutcome(
    string Label,
    int Runs,
    double Injured,
    double Deaths,
    double FirefightersInDistress,
    double PeakFireSquareMeters,
    double? FirstOnSceneMinutes,
    double? FirstDispatchMinutes,
    string Notes,
    IReadOnlyList<string>? KeyActions = null);

/// <summary>A strength or a point to work on, for the debrief.</summary>
public sealed record AarFinding(bool Strength, string Text);

/// <summary>Everything the after-action review knows about a session (§22).</summary>
public sealed class AarReport
{
    public Guid SessionId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public TimeSpan Duration => To - From;

    public IReadOnlyList<AarMetric> Metrics { get; init; } = [];
    public IReadOnlyList<AarDecision> Decisions { get; init; } = [];
    public IReadOnlyList<AarTimelineEntry> Timeline { get; init; } = [];
    public IReadOnlyList<AarUnitUsage> Units { get; init; } = [];
    public IReadOnlyList<AarHeatmap> Heatmaps { get; init; } = [];
    public IReadOnlyList<AarDelay> Delays { get; init; } = [];
    public IReadOnlyList<AarBranchPoint> Branches { get; init; } = [];

    /// <summary>How the session went, and (once compared) how other command policies did in the same scenario.</summary>
    public List<AarOutcome> Outcomes { get; init; } = [];

    public IReadOnlyList<AarFinding> Findings { get; init; } = [];

    /// <summary>Who took part (networked sessions); empty for a standalone session.</summary>
    public IReadOnlyList<AarParticipant> Participants { get; init; } = [];

    /// <summary>How the instructor ran the exercise (Phase 11 M4), in time order.</summary>
    public IReadOnlyList<AarInstructorAction> Instructor { get; init; } = [];

    /// <summary>Observers' notes and ratings (Phase 11 M6), in time order.</summary>
    public IReadOnlyList<AarNote> Notes { get; init; } = [];

    /// <summary>Observers' ratings by participant and competency area (Phase 12).</summary>
    public IReadOnlyList<AarCompetency> Competencies { get; init; } = [];

    /// <summary>What would be hard to defend afterwards (Phase 14: legal and liability), in time order.</summary>
    public IReadOnlyList<AarLiability> Liabilities { get; init; } = [];

    /// <summary>How well the significant decisions were documented in the decision log (Phase 14).</summary>
    public AarDocumentation Documentation { get; init; } = new(0, 0, []);

    /// <summary>Supplies, the fleet, costs and cost recovery (Phase 15).</summary>
    public AarLogistics Logistics { get; init; } = AarLogistics.None;

    /// <summary>Patients, the dead, public health and behavioural health (Phase 16).</summary>
    public AarHealth Health { get; init; } = AarHealth.None;

    /// <summary>Evacuation, rest centres, infrastructure, cyber and GPS, and the environment (Phase 17).</summary>
    public AarProtection Protection { get; init; } = AarProtection.None;

    /// <summary>The scenario, difficulty, random events and incidents within the incident (Phase 19).</summary>
    public AarTraining Training { get; init; } = AarTraining.None;

    /// <summary>The decisions one participant made (everyone's with null).</summary>
    public IEnumerable<AarDecision> DecisionsOf(Guid? participantId) =>
        participantId is { } id ? Decisions.Where(d => d.ActorId == id) : Decisions;

    /// <summary>What one participant's commands produced, in order (the whole timeline with null).</summary>
    public IEnumerable<AarTimelineEntry> TimelineOf(Guid? participantId) =>
        participantId is { } id ? Timeline.Where(t => t.ActorId == id || t.AboutId == id) : Timeline;

    public Competence Overall
    {
        get
        {
            var rated = Metrics.Concat(Decisions.Select(d => new AarMetric("", "", null, "", "", d.Rating, "")))
                .Where(m => m.Rating != Rating.NotApplicable).ToList();
            if (rated.Count == 0) return Competence.Developing;
            var score = rated.Average(m => m.Rating switch { Rating.Good => 1.0, Rating.Fair => 0.5, _ => 0.0 });
            var serious = Branches.Count(b => b.Rating == Rating.Poor);
            return score >= 0.75 && serious == 0 ? Competence.Competent : score >= 0.5 && serious <= 1 ? Competence.Developing : Competence.NotYetCompetent;
        }
    }

    /// <summary>Share of rated items that were good (fair counts half), 0–100.</summary>
    public int Score
    {
        get
        {
            var rated = Metrics.Select(m => m.Rating).Concat(Decisions.Select(d => d.Rating)).Where(r => r != Rating.NotApplicable).ToList();
            return rated.Count == 0 ? 0 : (int)Math.Round(100 * rated.Average(r => r switch { Rating.Good => 1.0, Rating.Fair => 0.5, _ => 0.0 }));
        }
    }
}
