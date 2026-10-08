using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// Minimal AAR built on the event stream: the full timeline, and the COP rebuilt as of any moment.
/// Metrics, heatmaps and branching comparison come in Phase 9.
/// </summary>
public sealed class AarService(IEventStore store) : IAarService
{
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
}
