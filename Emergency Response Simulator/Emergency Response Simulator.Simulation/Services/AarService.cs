using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Aar;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// The after-action review on the event stream (Design Document §22): the full timeline, the COP rebuilt as of any
/// moment, the analysed report (<see cref="AarAnalyzer"/>) and, for a scripted scenario, branch re-runs under other
/// command policies (<see cref="BranchRunner"/>).
/// </summary>
/// <param name="routing">Names places (the nearest road) in hotspots and delays.</param>
/// <param name="branches">Re-runs the scenario; null when the session's scenario can't be re-run.</param>
public sealed class AarService(IEventStore store, IRoutingService? routing = null, BranchRunner? branches = null) : IAarService
{
    /// <summary>Branch re-runs cover the session's length, within these bounds.</summary>
    public static readonly TimeSpan MinimumBranch = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan MaximumBranch = TimeSpan.FromMinutes(60);

    public async Task<IReadOnlyList<SimEvent>> GetTimelineAsync(
        Guid sessionId, bool includeTruth, CancellationToken cancellationToken = default)
    {
        var timeline = new List<SimEvent>();
        await foreach (var simEvent in store.ReadAsync(sessionId, cancellationToken: cancellationToken))
        {
            if (includeTruth || simEvent.Visibility == EventVisibility.Perceived)
                timeline.Add(simEvent);
        }
        return timeline;
    }

    public async Task<ICopService> ReplayToAsync(
        Guid sessionId, DateTimeOffset simTime, CancellationToken cancellationToken = default)
    {
        var picture = new PerceivedState();
        await foreach (var simEvent in store.ReadAsync(sessionId, cancellationToken: cancellationToken))
        {
            if (simEvent.SimTime > simTime)
                break;
            picture.Apply(simEvent);
        }
        return picture;
    }

    public async Task<AarReport> BuildReportAsync(Guid sessionId, string? title = null, CancellationToken cancellationToken = default)
    {
        var events = await GetTimelineAsync(sessionId, includeTruth: true, cancellationToken);
        // The analysis is CPU work over the whole stream: keep it off the caller's (UI) thread.
        return await Task.Run(() => AarAnalyzer.Analyze(sessionId, events, title, PlaceName), cancellationToken);
    }

    public bool CanCompareBranches => branches is not null;

    public async Task<IReadOnlyList<AarOutcome>> CompareBranchesAsync(
        Guid sessionId, int runs = 2, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (branches is null) return [];
        var events = await GetTimelineAsync(sessionId, includeTruth: false, cancellationToken);
        if (events.Count == 0) return [];

        var start = events[0].SimTime;
        var length = events[^1].SimTime - start;
        var duration = length < MinimumBranch ? MinimumBranch : length > MaximumBranch ? MaximumBranch : length;
        return await Task.Run(async () => (IReadOnlyList<AarOutcome>)
        [
            await branches.RunAsync(BranchPolicy.Recommended, start, duration, runs, progress, cancellationToken),
            await branches.RunAsync(BranchPolicy.NoResponse, start, duration, runs, progress, cancellationToken),
        ], cancellationToken);
    }

    private string? PlaceName(Core.Geo.GeoPoint point) =>
        routing?.NearestRoad(point) is { DistanceMeters: < 80, Name: { Length: > 0 } name } ? name : null;
}
