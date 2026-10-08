using System.Runtime.CompilerServices;
using Emergency_Response_Simulator.Core.Events;

namespace Emergency_Response_Simulator.Simulation.EventStore;

/// <summary>
/// Event store kept in process memory. Used when no database is configured and in tests.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly object _lock = new();
    private readonly List<SimEvent> _events = [];
    private long _nextSequence = 1;

    public event EventHandler<SimEvent>? Appended;

    public Task<SimEvent> AppendAsync(SimEventDraft draft, CancellationToken cancellationToken = default)
    {
        SimEvent appended;
        lock (_lock)
        {
            appended = new SimEvent
            {
                Sequence = _nextSequence++,
                SessionId = draft.SessionId,
                SimTime = draft.SimTime,
                WallTime = DateTimeOffset.UtcNow,
                Visibility = draft.Visibility,
                Source = draft.Source,
                Payload = draft.Payload,
            };
            _events.Add(appended);

            // Raised inside the lock so subscribers always observe events in sequence order.
            Appended?.Invoke(this, appended);
        }

        return Task.FromResult(appended);
    }

    public async IAsyncEnumerable<SimEvent> ReadAsync(
        Guid sessionId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<SimEvent> snapshot;
        lock (_lock)
        {
            snapshot = _events.Where(e => e.SessionId == sessionId && e.Sequence > afterSequence).ToList();
        }

        foreach (var simEvent in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return simEvent;
        }

        await Task.CompletedTask;
    }
}
