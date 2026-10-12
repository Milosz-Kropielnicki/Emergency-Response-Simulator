using System.Runtime.CompilerServices;
using Emergency_Response_Simulator.Core.Events;

namespace Emergency_Response_Simulator.Client;

/// <summary>
/// The client's copy of the event stream (Phase 11 M1): every event the server has sent this connection, with the
/// server's own sequence numbers, in order. Everything that reads a store in a standalone session (the COP, AVL trails,
/// the timeline, replay) reads this instead, unchanged. Clients never write events: they send commands.
/// </summary>
public sealed class MirrorEventStore : IEventStore
{
    private readonly object _lock = new();
    private readonly List<SimEvent> _events = [];
    private readonly List<(long Sequence, TaskCompletionSource Done)> _waiters = [];

    public event EventHandler<SimEvent>? Appended;

    /// <summary>The last sequence applied: where the stream resumes after a reconnect.</summary>
    public long LastSequence { get; private set; }

    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    /// <summary>Takes a batch from the server. Anything already held (a reconnect overlap) is skipped.</summary>
    public void Accept(IEnumerable<SimEvent> batch)
    {
        lock (_lock)
        {
            foreach (var simEvent in batch)
            {
                if (simEvent.Sequence <= LastSequence) continue;
                _events.Add(simEvent);
                LastSequence = simEvent.Sequence;
                // Raised inside the lock, like the in-memory store, so subscribers see events in sequence order.
                Appended?.Invoke(this, simEvent);
            }

            foreach (var waiter in _waiters.Where(w => w.Sequence <= LastSequence).ToList())
            {
                _waiters.Remove(waiter);
                waiter.Done.TrySetResult();
            }
        }
    }

    /// <summary>Raised after earlier events were merged in (<see cref="Backfill"/>), so views reading history can reload.</summary>
    public event EventHandler? Backfilled;

    /// <summary>
    /// Merges in earlier events this client may now see (Phase 11 M6: an observer granted ground truth), in sequence
    /// order. They don't replay through <see cref="Appended"/>: the live COP never takes truth, and history readers reload.
    /// </summary>
    public void Backfill(IEnumerable<SimEvent> batch)
    {
        lock (_lock)
        {
            var held = _events.Select(e => e.Sequence).ToHashSet();
            var added = batch.Where(e => e.Sequence <= LastSequence && held.Add(e.Sequence)).ToList();
            if (added.Count == 0) return;
            _events.AddRange(added);
            _events.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        }
        Backfilled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Completes once the event with this sequence (or a later one) has been applied, or after the timeout, so a
    /// command's caller sees its own result on the COP when the call returns.
    /// </summary>
    public async Task WaitForAsync(long sequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource done;
        lock (_lock)
        {
            if (LastSequence >= sequence) return;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((sequence, done));
        }
        await Task.WhenAny(done.Task, Task.Delay(timeout, cancellationToken));
    }

    public Task<SimEvent> AppendAsync(SimEventDraft draft, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A client doesn't write events; it sends commands to the server.");

    public async IAsyncEnumerable<SimEvent> ReadAsync(Guid sessionId, long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<SimEvent> snapshot;
        lock (_lock)
            snapshot = _events.Where(e => e.SessionId == sessionId && e.Sequence > afterSequence).ToList();
        foreach (var simEvent in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return simEvent;
        }
        await Task.CompletedTask;
    }
}
