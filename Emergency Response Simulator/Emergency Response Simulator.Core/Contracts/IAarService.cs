using Emergency_Response_Simulator.Core.Events;

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
}
