using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// After-Action Review: "How did we get here?" (Design Document §22).
/// Everything is derived by replaying the event stream.
/// </summary>
public interface IAarService
{
    /// <summary>
    /// The session's events in order. With <paramref name="includeTruth"/> the reviewer can compare
    /// what the trainee knew against what was actually happening.
    /// </summary>
    Task<IReadOnlyList<SimEvent>> GetTimelineAsync(
        Guid sessionId, bool includeTruth, CancellationToken cancellationToken = default);

    /// <summary>The COP exactly as the trainee saw it at a given simulation time.</summary>
    Task<ICopService> ReplayToAsync(
        Guid sessionId, DateTimeOffset simTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// The full review: decision timeline, performance metrics, commitment and delay analytics, branch points
    /// comparing key decisions with the recommended response, outcome and findings.
    /// </summary>
    Task<AarReport> BuildReportAsync(Guid sessionId, string? title = null, CancellationToken cancellationToken = default);

    /// <summary>Whether the session's scenario can be re-run under other command policies.</summary>
    bool CanCompareBranches { get; }

    /// <summary>
    /// Re-runs the scenario headless under the recommended response and with nobody in command, for as long as the
    /// session ran, and returns their (averaged) outcomes to set beside the trainee's.
    /// </summary>
    Task<IReadOnlyList<AarOutcome>> CompareBranchesAsync(
        Guid sessionId, int runs = 2, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
