using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Voice;
using Emergency_Response_Simulator.Voice;
using Microsoft.Extensions.Configuration;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>A channel this player works on, and who is talking on it now.</summary>
public sealed record VoiceChannelRow(string Id, string Name, string OnAir)
{
    public bool Busy => OnAir.Length > 0;

    public override string ToString() => Name;
}

/// <summary>
/// The radio headset (Phase 11 M7), in a networked session: talk on the role's channels with a real push-to-talk key
/// (or the on-screen button), hear the others, and play recordings back in the review.
/// <list type="bullet">
/// <item>Pressing push-to-talk asks the server for the channel; one talker at a time (half-duplex), so a busy channel says
/// who has it. While held, the microphone goes out in 20 ms Opus frames.</item>
/// <item>Letting go ends the transmission; the server records it, transcribes it, and puts it on the air for the
/// simulated crews, and the status shows what it heard.</item>
/// </list>
/// </summary>
public partial class RadioViewModel : ObservableObject, IDisposable
{
    private readonly SimulationConnection? _connection;
    private readonly ICopService _cop;
    private readonly ConcurrentDictionary<Guid, OpusFrameDecoder> _decoders = new();
    private readonly ConcurrentDictionary<Guid, VoiceTalk> _talks = new();
    private RadioAudioDevices? _devices;
    private OpusFrameEncoder? _encoder;
    private Guid? _transmission;
    private int _sequence;
    private UserRole? _role;

    public RadioViewModel(SimulationConnection? connection, ICopService cop, IConfiguration configuration)
    {
        _connection = connection;
        _cop = cop;
        PttKey = Enum.TryParse<Key>(configuration["Voice:PushToTalkKey"], ignoreCase: true, out var key) ? key : Key.Oem3;
        PttLabel = PttKey == Key.Oem3 ? "` (backtick)" : PttKey.ToString();
        if (connection is null) return;

        connection.VoiceStarted += (_, talk) =>
        {
            _talks[talk.TransmissionId] = talk;
            OnUi(Refresh);
        };
        connection.VoiceAudio += (_, packet) =>
            Devices?.Play(packet.TransmissionId, _decoders.GetOrAdd(packet.TransmissionId, _ => new OpusFrameDecoder()).Decode(packet.Packet));
        connection.VoiceEnded += (_, id) =>
        {
            _talks.TryRemove(id, out VoiceTalk? _);
            _decoders.TryRemove(id, out OpusFrameDecoder? _);
            Devices?.Finished(id);
            OnUi(Refresh);
        };
    }

    public bool IsAvailable => _connection is not null;

    /// <summary>The push-to-talk key (<c>Voice:PushToTalkKey</c>; the backtick unless configured).</summary>
    public Key PttKey { get; }

    public string PttLabel { get; }

    public ObservableCollection<VoiceChannelRow> Channels { get; } = [];

    [ObservableProperty] private VoiceChannelRow? _channel;
    [ObservableProperty] private bool _transmitting;
    [ObservableProperty] private string _status = "";

    /// <summary>The headset, opened the first time voice is used, so a client that never talks never touches the devices.</summary>
    private RadioAudioDevices? Devices
    {
        get
        {
            if (_devices is null && _connection is not null)
            {
                _devices = new RadioAudioDevices();
                _devices.FrameCaptured += OnFrame;
                if (_devices.Problem is { } problem) OnUi(() => Status = problem);
            }
            return _devices;
        }
    }

    private void OnFrame(short[] frame)
    {
        if (_transmission is not { } id || _connection is null || _encoder is null) return;
        _ = _connection.SendVoiceFrameAsync(id, _sequence++, _encoder.Encode(frame));
    }

    /// <summary>Push-to-talk pressed: ask for the channel.</summary>
    public async Task PressAsync()
    {
        if (_connection is null || Transmitting || Channel is null) return;
        if (Devices is not { CanTalk: true } devices)
        {
            Status = Devices?.Problem ?? "No microphone.";
            return;
        }
        Transmitting = true;
        try
        {
            var grant = await _connection.VoiceKeyDownAsync(Channel.Id);
            if (!grant.Granted)
            {
                Transmitting = false;
                Status = grant.Reason ?? "Not now.";
                return;
            }
            _encoder = new OpusFrameEncoder();
            _sequence = 0;
            _transmission = grant.TransmissionId;
            devices.Transmitting = true;
            Status = $"TRANSMITTING on {Channel.Name}" + (grant.Reason is { } warning ? $" · {warning}" : "");
        }
        catch (Exception ex)
        {
            Transmitting = false;
            Status = RemoteErrors.Message(ex);
        }
    }

    /// <summary>Push-to-talk released: end the transmission and show what the server heard.</summary>
    public async Task ReleaseAsync()
    {
        if (!Transmitting) return;
        if (_devices is not null) _devices.Transmitting = false;
        Transmitting = false;
        if (_transmission is not { } id || _connection is null) return;
        _transmission = null;
        try
        {
            Status = "Sending…";
            var recorded = await _connection.VoiceKeyUpAsync(id);
            Status = recorded?.Transcript is { } said ? $"Sent: \"{said}\"" : recorded is null ? "Nothing was sent." : "Sent (not transcribed).";
        }
        catch (Exception ex)
        {
            Status = RemoteErrors.Message(ex);
        }
    }

    /// <summary>A recorded transmission, played through the headset (the review).</summary>
    public async Task PlayClipAsync(Guid clipId)
    {
        if (_connection is null) return;
        if (await _connection.GetClipAsync(clipId) is not { } clip)
        {
            Status = "That recording is no longer kept.";
            return;
        }
        Devices?.PlayClip(OpusClip.Decode(clip));
    }

    /// <summary>Follows the role: its channels, and who is on air on each.</summary>
    public void Refresh()
    {
        if (_connection?.Ticket is not { } ticket) return;
        var role = _cop.Participants.FirstOrDefault(p => p.Id == ticket.ParticipantId)?.Role ?? ticket.Role;
        var onAir = _talks.Values.GroupBy(t => t.ChannelId).ToDictionary(g => g.Key, g => g.First().Speaker);
        var rows = VoiceChannels.For(role).Select(id => new VoiceChannelRow(id, RadioPlan.Find(id)?.Name ?? id,
            onAir.TryGetValue(id, out var speaker) ? $"{speaker} talking" : "")).ToList();
        if (!rows.SequenceEqual(Channels))
        {
            var chosen = Channel?.Id;
            Channels.Clear();
            foreach (var row in rows) Channels.Add(row);
            Channel = Channels.FirstOrDefault(c => c.Id == chosen) ?? Channels.FirstOrDefault();
        }
        if (role != _role)
        {
            _role = role;
            if (role == UserRole.Observer) Status = "Observers listen on every channel.";
        }
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public void Dispose() => _devices?.Dispose();
}
