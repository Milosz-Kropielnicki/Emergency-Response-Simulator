using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Voice;

namespace Emergency_Response_Simulator.Server.Voice;

/// <summary>
/// One session's radio voice (Phase 11 M7).
/// <list type="bullet">
/// <item><b>Membership</b>: a participant talks and listens on their role's channels (<see cref="VoiceChannels"/>),
/// the ones their workspace's comms hub shows; observers listen on all and never talk.</item>
/// <item><b>Half-duplex</b>: one talker per channel. Keying up on a channel someone is talking on is refused.</item>
/// <item><b>Patches</b> (Phase 12): channels patched together are one talk group: one talker across them, and
/// everyone on any of them hears it.</item>
/// <item><b>Emergency traffic</b> (Phase 12): while it is declared on a channel, only those working the emergency may
/// key up (<see cref="VoiceChannels.MayTalkUnderEmergencyTraffic"/>).</item>
/// <item><b>Phase 7 on the audio</b>: every transmission sounds like a radio; keying up over simulated traffic doubles
/// with it and comes out garbled, for the players and in the transcript the crews hear.</item>
/// <item><b>Recording</b>: each transmission is kept as an Opus clip, as it was heard, and recorded
/// (<see cref="RadioAudioRecorded"/>) for playback in the review.</item>
/// <item><b>Simulated parties</b>: the clean speech is transcribed and the transcript enters the radio traffic as the
/// speaker's <see cref="IC2Service.TransmitAsync"/>, so a crew that hears its call sign answers. The transcript is
/// the event; the audio is attached.</item>
/// </list>
/// </summary>
public sealed class VoiceRouter(SessionRuntime session, IVoiceTransport transport, IClipStore clips, ISpeechRecognizer recognizer, VoiceOptions options,
    ILogger logger)
{
    private sealed class Talk
    {
        public required Guid Id { get; init; }
        public required ParticipantRecord Speaker { get; init; }
        public required string Channel { get; init; }
        public required RadioEffect Effect { get; init; }
        public required List<string> Listeners { get; init; }
        public OpusFrameDecoder Decoder { get; } = new();
        public OpusFrameEncoder Encoder { get; } = new();
        public List<short> Clean { get; } = [];
        public List<byte[]> Heard { get; } = [];
        public int LastSequence { get; set; } = -1;
        public bool Ended { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Talk> _floor = [];

    /// <summary>Who is on air on each channel now.</summary>
    public IReadOnlyDictionary<string, string> OnAir
    {
        get { lock (_lock) return _floor.ToDictionary(f => f.Key, f => f.Value.Speaker.Name); }
    }

    public async Task<VoiceGrant> KeyDownAsync(ParticipantRecord speaker, string channel)
    {
        if (session.State != SessionState.Running) return new(false, Reason: "The exercise isn't running.");
        if (!speaker.VoiceConsent) return new(false, Reason: "You haven't agreed to voice recording, so you can listen but not talk.");
        if (!VoiceChannels.CanTalk(speaker.Role, channel))
            return new(false, Reason: speaker.Role == UserRole.Observer ? "Observers listen; they don't talk." : $"{Roles.Name(speaker.Role)} isn't on {channel}.");

        // Channels patched together (Phase 7) are one talk group (Phase 12): one talker across them, heard on all of them.
        var cop = session.Services.GetRequiredService<ICopService>();
        var linked = cop.Linked(channel);
        var states = cop.Channels.Where(c => linked.Contains(c.Info.Id)).ToList();

        // Emergency traffic (Phase 12): only those working the emergency stay on the air.
        if (states.FirstOrDefault(s => s.EmergencyTraffic) is { } emergency && !VoiceChannels.MayTalkUnderEmergencyTraffic(speaker.Role))
            return new(false, Reason: $"Emergency traffic on {emergency.Info.Name}: keep off the air unless you are working the emergency.");

        // Phase 7: keying up over simulated traffic doubles with it.
        var simulated = states.FirstOrDefault(s => s.IsBusy(session.Engine.SimTime))?.Talker;
        Talk talk;
        lock (_lock)
        {
            if (_floor.Values.FirstOrDefault(t => linked.Contains(t.Channel)) is { } busy)
                return new(false, Reason: busy.Speaker == speaker ? "You are already talking."
                    : busy.Channel == channel ? $"{channel} is busy: {busy.Speaker.Name} is talking."
                    : $"{channel} is patched to {busy.Channel}, where {busy.Speaker.Name} is talking.");
            if (_floor.Values.Any(t => t.Speaker == speaker)) return new(false, Reason: "You are already talking on another channel.");
            talk = new Talk
            {
                Id = Guid.NewGuid(), Speaker = speaker, Channel = channel,
                Effect = new RadioEffect(simulated is null ? CommsQuality.Clear : CommsQuality.Garbled, Random.Shared.Next()),
                Listeners = Listeners(linked, speaker),
            };
            _floor[channel] = talk;
        }
        await transport.StartedAsync(talk.Listeners, new VoiceTalk(talk.Id, channel, speaker.Id, speaker.Name));
        return new(true, talk.Id, simulated is null ? null : $"Doubled with {simulated}: you'll come out garbled.");
    }

    /// <summary>
    /// The connections of everyone present who listens on the channel or one patched to it, except the speaker (half-duplex).
    /// </summary>
    private List<string> Listeners(IReadOnlyCollection<string> channels, ParticipantRecord speaker) =>
        [.. session.Participants.Where(p => p.Present && p != speaker && channels.Any(c => VoiceChannels.Hears(p.Role, c))).SelectMany(p => p.Connections)];

    public async Task FrameAsync(ParticipantRecord speaker, Guid transmissionId, int sequence, byte[] packet)
    {
        Talk? talk;
        byte[] heard;
        bool timedOut;
        lock (_lock)
        {
            talk = _floor.Values.FirstOrDefault(t => t.Id == transmissionId && t.Speaker == speaker);
            if (talk is null || talk.Ended || sequence <= talk.LastSequence) return;
            // A frame lost on the way is concealed, so the recording keeps its timing.
            for (var missing = talk.LastSequence + 1; missing < sequence && talk.LastSequence >= 0; missing++)
                Add(talk, talk.Decoder.Decode(null));
            talk.LastSequence = sequence;
            heard = Add(talk, talk.Decoder.Decode(packet));
            timedOut = VoiceAudio.Duration(talk.Heard.Count) >= options.MaxTransmission;
        }
        await transport.AudioAsync(talk.Listeners, transmissionId, sequence, heard);
        if (timedOut) await KeyUpAsync(speaker, transmissionId);
    }

    private static byte[] Add(Talk talk, short[] clean)
    {
        if (clean.Length != VoiceAudio.FrameSamples) Array.Resize(ref clean, VoiceAudio.FrameSamples);
        talk.Clean.AddRange(clean);
        var heard = talk.Encoder.Encode(talk.Effect.Apply(clean));
        talk.Heard.Add(heard);
        return heard;
    }

    /// <summary>
    /// The end of a transmission: the channel is free again; the clip is kept and recorded; and what was said goes out
    /// on the radio to the simulated crews, as the speaker's transmission. Returns the recording event's sequence.
    /// </summary>
    public async Task<RadioAudioRecorded?> KeyUpAsync(ParticipantRecord speaker, Guid transmissionId)
    {
        Talk? talk;
        lock (_lock)
        {
            talk = _floor.Values.FirstOrDefault(t => t.Id == transmissionId && t.Speaker == speaker);
            if (talk is null || talk.Ended) return null;
            talk.Ended = true;
            _floor.Remove(talk.Channel);
        }
        await transport.EndedAsync(talk.Listeners, talk.Id);
        if (talk.Heard.Count == 0) return null;

        try
        {
            var clipId = Guid.NewGuid();
            await clips.SaveAsync(session.Id, clipId, OpusClip.Write(talk.Heard));
            var duration = VoiceAudio.Duration(talk.Heard.Count);

            string? transcript = null;
            if (recognizer.Available && VoiceAudio.Level(talk.Clean.ToArray()) > 0.003)
            {
                var callsigns = session.Services.GetRequiredService<ICopService>().Units.Select(u => u.Callsign).ToList();
                if (await recognizer.TranscribeAsync(VoiceAudio.To16k(talk.Clean), callsigns) is { Length: > 0 } heard && !string.IsNullOrWhiteSpace(heard))
                    transcript = Transcripts.Normalise(heard, callsigns);
            }

            var c2 = session.Services.GetRequiredService<IC2Service>();
            return await session.Gate.RunAsync(async () =>
            {
                using (CommandContext.As(speaker.Actor))
                {
                    Guid? messageId = null;
                    if (transcript is not null)
                    {
                        // No hold time: the audio is what was said, so nothing was cut off that the transcript would still hold.
                        var sent = await c2.TransmitAsync(talk.Channel, null, transcript);
                        if (sent.Succeeded) messageId = sent.EntityId;
                    }
                    var recorded = new RadioAudioRecorded(clipId, talk.Channel, speaker.Id, speaker.Name, duration.TotalSeconds, talk.Effect.Quality,
                        transcript, messageId);
                    await session.Engine.PublishAsync(recorded, EventVisibility.Perceived, EventSources.Comms);
                    return recorded;
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording transmission {Transmission} failed", transmissionId);
            return null;
        }
    }

    /// <summary>A participant's link dropped: whatever they were saying ends there.</summary>
    public async Task DroppedAsync(ParticipantRecord speaker)
    {
        List<Guid> theirs;
        lock (_lock) theirs = [.. _floor.Values.Where(t => t.Speaker == speaker).Select(t => t.Id)];
        foreach (var id in theirs) await KeyUpAsync(speaker, id);
    }

    public Task<byte[]?> ClipAsync(Guid clipId) => clips.LoadAsync(session.Id, clipId);
}
