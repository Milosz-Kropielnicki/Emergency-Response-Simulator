namespace Emergency_Response_Simulator.Core.Events;

/// <summary>
/// Append-only, time-stamped event stream. Events can be added and read back in order;
/// they are never changed or removed.
/// </summary>
public interface IEventStore
{
    /// <summary>Raised after an event has been durably appended, in sequence order.</summary>
    event EventHandler<SimEvent>? Appended;

    Task<SimEvent> AppendAsync(SimEventDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Reads a session's events in sequence order, starting after <paramref name="afterSequence"/>.</summary>
    IAsyncEnumerable<SimEvent> ReadAsync(
        Guid sessionId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);
}
