using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// The communications hub (Design Document §13) over the COP's comms log: the channels command knows of, what has
/// been heard on them, and push-to-talk through C2. Only what was actually heard is here; lost transmissions are
/// ground truth.
/// </summary>
public sealed class CommsService : ICommsService
{
    private readonly ICopService _cop;
    private readonly IC2Service _c2;

    public CommsService(ICopService cop, IC2Service c2, IEventStore store, Guid sessionId)
    {
        _cop = cop;
        _c2 = c2;
        store.Appended += (_, simEvent) =>
        {
            if (simEvent.SessionId == sessionId && simEvent.Payload is CommsLogged logged)
                MessageReceived?.Invoke(this, new CommsMessage(logged.MessageId, logged.ChannelId, logged.From, logged.To, logged.Text,
                    simEvent.SimTime, logged.Quality != CommsQuality.Clear));
        };
    }

    public event EventHandler<CommsMessage>? MessageReceived;

    public IReadOnlyList<CommsChannel> Channels =>
        _cop.Channels.Select(c => new CommsChannel(c.Info.Id, c.Info.Name, c.Info.Kind switch
        {
            ChannelKind.EmergencyCalls => CommsChannelKind.EmergencyCalls,
            ChannelKind.Chat => CommsChannelKind.Chat,
            _ => CommsChannelKind.Radio,
        }, c.Info.Agency)).ToList();

    public async Task SendAsync(CommsMessage message, CancellationToken cancellationToken = default)
    {
        var result = await _c2.TransmitAsync(message.ChannelId, message.To, message.Text, cancellationToken: cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error);
    }

    public IReadOnlyList<CommsMessage> GetHistory(string channelId, int maxMessages = 100) =>
        _cop.CommsLog.Where(m => m.ChannelId == channelId).TakeLast(maxMessages)
            .Select(m => new CommsMessage(m.Id, m.ChannelId, m.From, m.To, m.Text, m.At, m.Quality != CommsQuality.Clear))
            .ToList();
}
