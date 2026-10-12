using System.Collections.Concurrent;
using System.Threading.Channels;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Remote;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// Streams the session's events to each connection, in order and with no gaps (Phase 11 M1):
/// <list type="number">
/// <item>On subscribe the connection starts queueing live events at once, so nothing appended from now on is missed.</item>
/// <item>The history after the client's last applied sequence is read from the store and sent.</item>
/// <item>The queue is then drained, skipping anything the history already covered.</item>
/// </list>
/// Visibility is decided here, on the server: a connection without the truth role never has a truth event queued,
/// read or sent. One pump per connection keeps its stream in sequence order.
/// </summary>
public sealed class EventFeed : IDisposable
{
    public const int BatchSize = 500;

    private readonly IEventStore _store;
    private readonly Guid _sessionId;
    private readonly IHubContext<SimulationHub, ISimulationClient> _hub;
    private readonly ILogger<EventFeed> _logger;
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();

    public EventFeed(IEventStore store, Guid sessionId, IHubContext<SimulationHub, ISimulationClient> hub, ILogger<EventFeed> logger)
    {
        _store = store;
        _sessionId = sessionId;
        _hub = hub;
        _logger = logger;
        _store.Appended += OnAppended;
    }

    public int Subscribers => _subscribers.Count;

    /// <summary>
    /// What a connection may see: perceived events; ground truth with the truth role; and assessment notes for assessors
    /// (observers and the instructor, Phase 11 M6), with or without truth. Trainees never get a note.
    /// </summary>
    public static bool Visible(SimEvent simEvent, bool truth, bool assessor = false) =>
        truth || simEvent.Visibility == EventVisibility.Perceived || (assessor && simEvent.Payload is AssessmentNoteRecorded);

    public void Subscribe(string connectionId, long afterSequence, bool truth, bool assessor = false) =>
        Start(connectionId, new Subscriber(truth, assessor) { Last = afterSequence }, backfill: false);

    /// <summary>
    /// The connection's view changed (Phase 11 M6: an observer granted or refused truth, or a new role): carry on from
    /// where it was with the new view. Newly granted truth first backfills the truth events it missed before now.
    /// </summary>
    public void Retune(string connectionId, bool truth, bool assessor)
    {
        if (!_subscribers.TryGetValue(connectionId, out var old) || (old.Truth == truth && old.Assessor == assessor)) return;
        Start(connectionId, new Subscriber(truth, assessor) { Last = old.Last }, backfill: truth && !old.Truth);
    }

    private void Start(string connectionId, Subscriber subscriber, bool backfill)
    {
        if (_subscribers.TryRemove(connectionId, out var previous))
            previous.Stop();
        _subscribers[connectionId] = subscriber; // live events queue from here on
        subscriber.Pump = Task.Run(() => PumpAsync(connectionId, subscriber, backfill));
    }

    public void Unsubscribe(string connectionId)
    {
        if (_subscribers.TryRemove(connectionId, out var subscriber))
            subscriber.Stop();
    }

    private void OnAppended(object? sender, SimEvent simEvent)
    {
        if (simEvent.SessionId != _sessionId) return;
        foreach (var subscriber in _subscribers.Values)
            if (Visible(simEvent, subscriber.Truth, subscriber.Assessor))
                subscriber.Queue.Writer.TryWrite(simEvent);
    }

    private async Task PumpAsync(string connectionId, Subscriber subscriber, bool backfill)
    {
        var client = _hub.Clients.Client(connectionId);
        var token = subscriber.Stopping.Token;
        var afterSequence = subscriber.Last;
        var last = afterSequence;
        var batch = new List<SimEvent>(BatchSize);
        try
        {
            // Truth just granted: the truth events before now, which the client's stream skipped (Phase 11 M6).
            if (backfill)
            {
                await foreach (var simEvent in _store.ReadAsync(_sessionId, 0, token))
                {
                    if (simEvent.Sequence > afterSequence) break;
                    if (simEvent.Visibility != EventVisibility.Truth) continue;
                    batch.Add(simEvent);
                    if (batch.Count >= BatchSize) await Backfill();
                }
                await Backfill();
            }

            // History the client hasn't applied yet.
            await foreach (var simEvent in _store.ReadAsync(_sessionId, afterSequence, token))
            {
                last = simEvent.Sequence;
                if (!Visible(simEvent, subscriber.Truth, subscriber.Assessor)) continue;
                batch.Add(simEvent);
                if (batch.Count >= BatchSize) await Flush();
            }
            await Flush();

            // Then live, skipping what the history already sent.
            while (await subscriber.Queue.Reader.WaitToReadAsync(token))
            {
                while (batch.Count < BatchSize && subscriber.Queue.Reader.TryRead(out var simEvent))
                {
                    if (simEvent.Sequence <= last) continue;
                    last = simEvent.Sequence;
                    batch.Add(simEvent);
                }
                await Flush();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event feed to {Connection} stopped", connectionId);
        }

        async Task Flush()
        {
            if (batch.Count > 0)
            {
                await client.Events(batch.ToList());
                batch.Clear();
            }
            subscriber.Last = last; // only once sent: a retune carries on from here, with no gap
        }

        async Task Backfill()
        {
            if (batch.Count == 0) return;
            await client.Backfill(batch.ToList());
            batch.Clear();
        }
    }

    public void Dispose()
    {
        _store.Appended -= OnAppended;
        foreach (var subscriber in _subscribers.Values)
            subscriber.Stop();
    }

    private sealed class Subscriber(bool truth, bool assessor)
    {
        public bool Truth { get; } = truth;
        public bool Assessor { get; } = assessor;

        /// <summary>The last sequence sent (or, before anything is, the one the client said it had).</summary>
        public long Last { get; set; }

        public Channel<SimEvent> Queue { get; } = Channel.CreateUnbounded<SimEvent>(new UnboundedChannelOptions { SingleReader = true });
        public CancellationTokenSource Stopping { get; } = new();
        public Task? Pump { get; set; }

        public void Stop()
        {
            Queue.Writer.TryComplete();
            Stopping.Cancel();
        }
    }
}
