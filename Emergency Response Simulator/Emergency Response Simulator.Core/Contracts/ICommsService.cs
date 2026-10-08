using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Communications hub: radio channels, inter-agency chat, incoming calls and field reports
/// (Design Document §13). Implemented in Phase 7, where messages can be delayed, garbled or lost.
/// </summary>
public interface ICommsService
{
    IReadOnlyList<CommsChannel> Channels { get; }

    /// <summary>Raised when a message arrives. May be raised on a background thread.</summary>
    event EventHandler<CommsMessage>? MessageReceived;

    Task SendAsync(CommsMessage message, CancellationToken cancellationToken = default);

    IReadOnlyList<CommsMessage> GetHistory(string channelId, int maxMessages = 100);
}

public enum CommsChannelKind
{
    Radio,
    Chat,
    EmergencyCalls,
    FieldReports,
}

public sealed record CommsChannel(string Id, string Name, CommsChannelKind Kind, AgencyType? Agency);

/// <param name="Degraded">True if the transmission arrived garbled or partial.</param>
public sealed record CommsMessage(
    Guid Id,
    string ChannelId,
    string From,
    string? To,
    string Text,
    DateTimeOffset SimTime,
    bool Degraded = false);
