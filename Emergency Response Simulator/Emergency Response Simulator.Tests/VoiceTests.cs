using System.Collections.Concurrent;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server.Voice;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Systems;
using Emergency_Response_Simulator.Simulation.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 11 M7: voice on the radio, over a real in-process server.</summary>
public class VoiceTests
{
    /// <summary>A recogniser that "hears" what the test says it does, in order.</summary>
    private sealed class ScriptedRecognizer(params string[] heard) : ISpeechRecognizer
    {
        private readonly ConcurrentQueue<string> _heard = new(heard);

        public bool Available => true;

        public List<IReadOnlyList<string>> Vocabularies { get; } = [];

        public Task<string?> TranscribeAsync(float[] samples16k, IReadOnlyList<string> vocabulary, CancellationToken cancellationToken = default)
        {
            Vocabularies.Add(vocabulary);
            return Task.FromResult(_heard.TryDequeue(out var text) ? text : null);
        }
    }

    /// <summary>Everything a client hears on the radio.</summary>
    private sealed class Ears
    {
        public ConcurrentBag<VoiceTalk> Started { get; } = [];
        public ConcurrentBag<VoicePacket> Packets { get; } = [];
        public ConcurrentBag<Guid> Ended { get; } = [];

        public Ears(SimulationConnection client)
        {
            client.VoiceStarted += (_, talk) => Started.Add(talk);
            client.VoiceAudio += (_, packet) => Packets.Add(packet);
            client.VoiceEnded += (_, id) => Ended.Add(id);
        }
    }

    /// <summary>A tone of the given length, as 20 ms frames.</summary>
    private static List<short[]> Tone(TimeSpan length, double hertz = 700) =>
        Frames([.. Enumerable.Range(0, (int)(length.TotalSeconds * VoiceAudio.SampleRate))
            .Select(i => (short)(9000 * Math.Sin(2 * Math.PI * hertz * i / VoiceAudio.SampleRate)))]);

    private static List<short[]> Frames(short[] pcm) =>
        [.. Enumerable.Range(0, pcm.Length / VoiceAudio.FrameSamples).Select(f => pcm[(f * VoiceAudio.FrameSamples)..((f + 1) * VoiceAudio.FrameSamples)])];

    /// <summary>Speech from Windows' synthesiser, 48 kHz mono, as a player would say it into a headset.</summary>
    private static short[] Speak(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        using var stream = new MemoryStream();
        synthesizer.SetOutputToAudioStream(stream, new SpeechAudioFormatInfo(VoiceAudio.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.Rate = -1;
        synthesizer.Speak(text);
        var bytes = stream.ToArray();
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
        return pcm;
    }

    /// <summary>Keys up, sends the audio frame by frame, lets go; returns the recording.</summary>
    private static async Task<RadioAudioRecorded?> TalkAsync(SimulationConnection client, string channel, IReadOnlyList<short[]> frames)
    {
        var grant = await client.VoiceKeyDownAsync(channel);
        Assert.True(grant.Granted, grant.Reason);
        var encoder = new OpusFrameEncoder();
        for (var i = 0; i < frames.Count; i++)
            await client.SendVoiceFrameAsync(grant.TransmissionId!.Value, i, encoder.Encode(frames[i]));
        return await client.VoiceKeyUpAsync(grant.TransmissionId!.Value);
    }

    /// <summary>Waits, as a good operator does, until nobody is talking on the channel (the simulated crews included).</summary>
    private static async Task ClearAsync(InProcessServer server, string channel)
    {
        for (var i = 0; i < 40 && server.Cop.Channels.Single(c => c.Info.Id == channel).IsBusy(server.Engine.SimTime); i++)
            await server.Engine.StepAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>Everything on the radio and what was lost, for a failure message.</summary>
    private static string RadioLog(InProcessServer server) => string.Join(Environment.NewLine, server.Store.All().Select(e => e.Payload switch
    {
        CommsLogged c => $"{e.SimTime:HH:mm:ss} {c.ChannelId} {c.From}→{c.To}: {c.Text} [{c.Quality}]",
        TransmissionLost l => $"{e.SimTime:HH:mm:ss} LOST {l.ChannelId} {l.From}: {l.Text} ({l.Reason})",
        RadioCallMade r => $"{e.SimTime:HH:mm:ss} CALL {r.ChannelId}: {r.Text}",
        _ => null,
    }).OfType<string>());

    /// <summary>
    /// The crew said <paramref name="expected"/>: heard clearly, or broken up on the way back (the Phase 7 radio still
    /// applies to their reply), in which case every word that got through is one of theirs.
    /// </summary>
    private static void Answered(InProcessServer server, string from, Func<string, bool> clear, string? expected)
    {
        var reply = server.Store.All().Select(e => e.Payload).OfType<CommsLogged>().LastOrDefault(c => c.From == from);
        Assert.True(reply is not null, $"{from} never answered.{Environment.NewLine}{RadioLog(server)}");
        if (reply.Quality == CommsQuality.Clear)
            Assert.True(clear(reply.Text), $"{from} said \"{reply.Text}\".{Environment.NewLine}{RadioLog(server)}");
        else if (expected is not null)
            Assert.All(reply.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w != "…"), word => Assert.Contains(word.Trim('—'), expected));
    }

    private static string? Said(InProcessServer server, string from) =>
        server.Store.All().Select(e => e.Payload).OfType<CommsLogged>().LastOrDefault(c => c.From == from)?.Text;

    // ---- The exit criteria ----

    [Fact]
    public async Task Two_players_talk_on_a_tactical_channel_one_at_a_time()
    {
        await using var server = await StartAsync();
        var bea = await server.JoinAsync("Bea", UserRole.Operations);
        var sam = await server.JoinAsync("Sam", UserRole.Safety);
        var alex = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var olly = await server.JoinAsync("Olly", UserRole.Observer);
        var (beaEars, samEars, alexEars, ollyEars) = (new Ears(bea), new Ears(sam), new Ears(alex), new Ears(olly));

        // Bea keys up on FIRE TAC 2: the channel is hers until she lets go.
        var grant = await bea.VoiceKeyDownAsync(RadioPlan.FireTac2);
        Assert.True(grant.Granted, grant.Reason);
        var busy = await sam.VoiceKeyDownAsync(RadioPlan.FireTac2);
        Assert.False(busy.Granted);
        Assert.Equal("FIRE TAC 2 is busy: Bea is talking.", busy.Reason);

        var encoder = new OpusFrameEncoder();
        var tone = Tone(TimeSpan.FromSeconds(1));
        for (var i = 0; i < tone.Count; i++)
            await bea.SendVoiceFrameAsync(grant.TransmissionId!.Value, i, encoder.Encode(tone[i]));
        var recorded = await bea.VoiceKeyUpAsync(grant.TransmissionId!.Value);
        Assert.NotNull(recorded);
        Assert.Equal((RadioPlan.FireTac2, "Bea", 1.0), (recorded.ChannelId, recorded.Speaker, recorded.DurationSeconds));

        // Sam (and the observer) heard all of it, through a radio; Bea doesn't hear herself; the IC isn't on TAC 2.
        await Until(() => samEars.Packets.Count == tone.Count && samEars.Ended.Contains(grant.TransmissionId.Value), TimeSpan.FromSeconds(10));
        Assert.Equal("Bea", Assert.Single(samEars.Started).Speaker);
        var decoder = new OpusFrameDecoder();
        var heard = samEars.Packets.OrderBy(p => p.Sequence).SelectMany(p => decoder.Decode(p.Packet)).ToArray();
        Assert.True(VoiceAudio.Level(heard) > 0.02, "Sam heard the transmission");
        await Until(() => ollyEars.Packets.Count == tone.Count, TimeSpan.FromSeconds(10));
        Assert.Empty(beaEars.Packets);
        Assert.Empty(alexEars.Started);

        // Half-duplex: now Sam has the channel, and Bea hears him.
        Assert.NotNull(await TalkAsync(sam, RadioPlan.FireTac2, Tone(TimeSpan.FromSeconds(0.4), 500)));
        await Until(() => beaEars.Packets.Count == 20, TimeSpan.FromSeconds(10));
        Assert.Equal("Sam", Assert.Single(beaEars.Started).Speaker);
    }

    [Fact]
    public async Task A_spoken_order_to_a_simulated_crew_is_read_back()
    {
        var recognizer = new ScriptedRecognizer("engine four, control, proceed to Barrow Street and set up for an interior attack, over");
        await using var server = await StartWithAsync(services => services.AddSingleton<ISpeechRecognizer>(recognizer));
        var bea = await server.JoinAsync("Bea", UserRole.Operations);

        await ClearAsync(server, RadioPlan.FireCommand);
        var recorded = await TalkAsync(bea, RadioPlan.FireCommand, Tone(TimeSpan.FromSeconds(2)));
        Assert.Equal("Engine 4, control, proceed to Barrow Street and set up for an interior attack, over", recorded!.Transcript);
        Assert.Contains("Engine 4", recognizer.Vocabularies.Single()); // the session's call signs biased the recogniser

        // The transcript entered the radio traffic as Bea's transmission ...
        var call = server.Store.All().Single(e => e.Payload is RadioCallMade);
        Assert.Equal(((RadioCallMade)call.Payload).MessageId, recorded.MessageId);
        Assert.Equal(bea.Ticket!.ParticipantId, call.ActorId);
        Assert.Null(((RadioCallMade)call.Payload).HeldSeconds); // the audio is what was said: nothing to cut off

        // ... and Engine 4 read it back.
        await server.StepAsync(TimeSpan.FromSeconds(40));
        const string readBack = "Control, Engine 4, received: proceed to Barrow Street and set up for an interior attack, over.";
        Answered(server, "Engine 4", said => said == readBack, readBack);
    }

    [Fact]
    public async Task Every_transmission_plays_back_in_the_review()
    {
        var recognizer = new ScriptedRecognizer("Engine 12, Control, what's your status", "", "Sam, Bea, the roof is going, pull your crews back");
        await using var server = await StartWithAsync(services => services.AddSingleton<ISpeechRecognizer>(recognizer));
        var bea = await server.JoinAsync("Bea", UserRole.Operations);
        var sam = await server.JoinAsync("Sam", UserRole.Safety);

        var lengths = new[] { 1.2, 0.6, 1.6 };
        await TalkAsync(bea, RadioPlan.FireCommand, Tone(TimeSpan.FromSeconds(lengths[0])));
        await TalkAsync(sam, RadioPlan.FireTac2, Tone(TimeSpan.FromSeconds(lengths[1]), 400));
        await TalkAsync(bea, RadioPlan.FireTac2, Tone(TimeSpan.FromSeconds(lengths[2]), 900));

        var recordings = server.Store.All().Select(e => e.Payload).OfType<RadioAudioRecorded>().ToList();
        Assert.Equal(3, recordings.Count);
        Assert.Null(recordings[1].Transcript); // heard nothing: recorded all the same
        Assert.Null(recordings[1].MessageId);

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        var played = report.Timeline.Where(t => t.ClipId is not null).ToList();
        Assert.Equal(recordings.Select(r => r.ClipId), played.Select(t => t.ClipId!.Value));
        for (var i = 0; i < played.Count; i++)
        {
            // Through a client, as the review window fetches it: the whole transmission, as it was heard.
            var clip = await sam.GetClipAsync(played[i].ClipId!.Value);
            Assert.NotNull(clip);
            var pcm = OpusClip.Decode(clip);
            Assert.Equal((int)Math.Round(lengths[i] * VoiceAudio.SampleRate), pcm.Length);
            Assert.True(VoiceAudio.Level(pcm) > 0.02);
        }
        Assert.Contains(played, t => t.Text.Contains("Voice: Bea on FIRE CMD 1") && t.Text.Contains("Engine 12, Control, what's your status"));
    }

    // ---- Real speech recognition ----

    [Fact]
    public async Task Real_speech_is_transcribed_with_the_call_signs_and_answered()
    {
        var model = new VoiceOptions().WhisperModel;
        if (!File.Exists(model)) return; // no Whisper model on this machine: nothing to test against
        await using var server = await StartWithAsync(null, settings: [$"--Voice:WhisperModel={model}"]);
        var bea = await server.JoinAsync("Bea", UserRole.Operations);

        await ClearAsync(server, RadioPlan.FireCommand);
        var recorded = await TalkAsync(bea, RadioPlan.FireCommand, Frames(Speak("Engine four, this is Control. What is your status? Over.")));
        Assert.NotNull(recorded!.Transcript);
        Assert.Contains("Engine 4", recorded.Transcript);
        Assert.Contains("status", recorded.Transcript, StringComparison.OrdinalIgnoreCase);
        await server.StepAsync(TimeSpan.FromSeconds(40));
        Answered(server, "Engine 4", said => said.StartsWith("Control, Engine 4:"), null); // broken up on the way back: any street it named
    }

    // ---- Who may talk ----

    [Fact]
    public async Task Talking_needs_consent_a_running_exercise_and_the_role_s_channel()
    {
        await using var server = await StartAsync(start: false);
        var bea = await server.JoinAsync("Bea", UserRole.Operations);
        var quiet = await server.JoinAsync("Quinn", UserRole.Planning, voiceConsent: false);
        var olly = await server.JoinAsync("Olly", UserRole.Observer);
        Assert.Equal("The exercise isn't running.", (await bea.VoiceKeyDownAsync(RadioPlan.FireTac2)).Reason);

        await server.Instructor.StartSessionAsync();
        Assert.Contains("haven't agreed", (await quiet.VoiceKeyDownAsync(RadioPlan.FireCommand)).Reason);
        Assert.Equal("Observers listen; they don't talk.", (await olly.VoiceKeyDownAsync(RadioPlan.FireCommand)).Reason);
        Assert.Equal("Operations Section Chief isn't on GARDA OPS.", (await bea.VoiceKeyDownAsync(RadioPlan.Garda)).Reason);
        Assert.True((await server.Instructor.VoiceKeyDownAsync(RadioPlan.Garda)).Granted); // the instructor works every channel

        var consents = server.Store.All().Select(e => e.Payload).OfType<VoiceConsentRecorded>().ToList();
        Assert.Contains(consents, c => c.ParticipantId == quiet.Ticket!.ParticipantId && !c.Consented);
        Assert.All(consents, c => Assert.Equal(30, c.RetentionDays));
        var policy = await bea.VoicePolicyAsync();
        Assert.Equal(30, policy.RetentionDays);
        Assert.Contains("kept for 30 day(s)", policy.Notice);
    }

    [Fact]
    public async Task A_dropped_link_ends_the_transmission_and_frees_the_channel()
    {
        await using var server = await StartAsync();
        var bea = await server.JoinAsync("Bea", UserRole.Operations);
        var sam = await server.JoinAsync("Sam", UserRole.Safety);
        var grant = await bea.VoiceKeyDownAsync(RadioPlan.FireTac2);
        var encoder = new OpusFrameEncoder();
        foreach (var (frame, i) in Tone(TimeSpan.FromSeconds(0.2)).Select((f, i) => (f, i)))
            await bea.SendVoiceFrameAsync(grant.TransmissionId!.Value, i, encoder.Encode(frame));
        await bea.DropAsync();
        await Until(() => server.Session.Voice.OnAir.Count == 0, TimeSpan.FromSeconds(10));
        Assert.True((await sam.VoiceKeyDownAsync(RadioPlan.FireTac2)).Granted);
    }

    // ---- The parts ----

    [Fact]
    public async Task Recordings_past_the_retention_period_are_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "ers-voice-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileClipStore(Options.Create(new VoiceOptions { ClipDirectory = root }));
            var session = Guid.NewGuid();
            var (old, recent) = (Guid.NewGuid(), Guid.NewGuid());
            await store.SaveAsync(session, old, OpusClip.Write([new OpusFrameEncoder().Encode(new short[VoiceAudio.FrameSamples])]));
            await store.SaveAsync(session, recent, OpusClip.Write([]));
            File.SetLastWriteTimeUtc(Path.Combine(root, session.ToString("N"), $"{old:N}.erso"), DateTime.UtcNow.AddDays(-40));

            Assert.Equal(1, store.DeleteOlderThan(DateTimeOffset.UtcNow.AddDays(-30)));
            Assert.Null(await store.LoadAsync(session, old));
            Assert.NotNull(await store.LoadAsync(session, recent));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Call_signs_said_as_words_become_call_signs()
    {
        string[] callsigns = ["Engine 4", "Engine 12", "Ambulance 14", "Ladder 2"];
        Assert.Equal("Engine 4, Control, no one is inside", Transcripts.Normalise("engine four, Control,  no one is inside", callsigns));
        Assert.Equal("Engine 12 and Ambulance 14", Transcripts.Normalise("Engine twelve and ambulance-fourteen", callsigns));
        Assert.Equal("Ladder 2 to the rear", Transcripts.Normalise("ladder2 to the rear", callsigns));
        Assert.Contains("Ambulance 14", Transcripts.Prompt(callsigns));
    }

    [Fact]
    public void Crews_read_back_instructions_but_not_bare_calls()
    {
        Assert.Equal("proceed to Barrow Street", CommsSystem.Instruction("Engine 4, Control, proceed to Barrow Street, over.", "Engine 4"));
        Assert.Null(CommsSystem.Instruction("Engine 4, this is Control, over", "Engine 4"));
        Assert.Null(CommsSystem.Instruction("Control to Engine 4, radio check", "Engine 4"));
    }

    [Fact]
    public void Audio_survives_opus_and_sounds_like_a_radio()
    {
        var tone = Tone(TimeSpan.FromSeconds(0.5));
        var (encoder, decoder) = (new OpusFrameEncoder(), new OpusFrameDecoder());
        var roundTrip = tone.SelectMany(f => decoder.Decode(encoder.Encode(f))).ToArray();
        Assert.Equal(tone.Count * VoiceAudio.FrameSamples, roundTrip.Length);
        Assert.InRange(VoiceAudio.Level(roundTrip), 0.1, 0.3);

        // Garbled (doubled) audio loses most of its frames to noise; clear audio keeps the voice.
        double Similarity(CommsQuality quality)
        {
            var effect = new RadioEffect(quality, 7);
            var out1 = tone.SelectMany(f => effect.Apply(f)).ToArray();
            var input = tone.SelectMany(f => f).ToArray();
            double dot = 0, a = 0, b = 0;
            for (var i = 0; i < input.Length; i++) { dot += (double)input[i] * out1[i]; a += (double)input[i] * input[i]; b += (double)out1[i] * out1[i]; }
            return dot / Math.Sqrt(a * b);
        }
        Assert.True(Similarity(CommsQuality.Clear) > 0.6);
        Assert.True(Similarity(CommsQuality.Garbled) < Similarity(CommsQuality.Clear) - 0.2);
    }
}
