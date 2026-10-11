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
    string? Note);

/// <summary>A message or event in the replayable timeline (decisions, radio, dispatches, what happened).</summary>
public sealed record AarTimelineEntry(long Sequence, DateTimeOffset At, string Category, string Text, bool IsDecision, bool IsTruth, bool Important);

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
