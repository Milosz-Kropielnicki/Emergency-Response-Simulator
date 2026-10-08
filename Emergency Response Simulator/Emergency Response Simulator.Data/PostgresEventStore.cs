using System.Runtime.CompilerServices;
using Emergency_Response_Simulator.Core.Events;
using Microsoft.EntityFrameworkCore;

namespace Emergency_Response_Simulator.Data;

/// <summary>Durable event stream in the <c>events</c> table.</summary>
public sealed class PostgresEventStore(IDbContextFactory<ErsDbContext> contextFactory) : IEventStore
{
    // Keeps Appended notifications in the same order as the sequence numbers the database hands out.
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    public event EventHandler<SimEvent>? Appended;

    public async Task<SimEvent> AppendAsync(SimEventDraft draft, CancellationToken cancellationToken = default)
    {
        await _appendGate.WaitAsync(cancellationToken);
        try
        {
            var record = new EventRecord
            {
                SessionId = draft.SessionId,
                // timestamptz columns only accept UTC offsets.
                SimTime = draft.SimTime.ToUniversalTime(),
                WallTime = DateTimeOffset.UtcNow,
                Visibility = draft.Visibility,
                Source = draft.Source,
                Type = draft.Payload.GetType().Name,
                Payload = EventJson.Serialize(draft.Payload),
            };

            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                db.Events.Add(record);
                await db.SaveChangesAsync(cancellationToken);
            }

            var appended = new SimEvent
            {
                Sequence = record.Sequence,
                SessionId = record.SessionId,
                SimTime = record.SimTime,
                WallTime = record.WallTime,
                Visibility = record.Visibility,
                Source = record.Source,
                Payload = draft.Payload,
            };
            Appended?.Invoke(this, appended);
            return appended;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async IAsyncEnumerable<SimEvent> ReadAsync(
        Guid sessionId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var records = db.Events.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken);

        await foreach (var record in records)
            yield return record.ToSimEvent();
    }
}
