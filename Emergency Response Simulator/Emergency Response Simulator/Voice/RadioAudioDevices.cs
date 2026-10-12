using System.Collections.Concurrent;
using Emergency_Response_Simulator.Simulation.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Emergency_Response_Simulator.Voice;

/// <summary>
/// The headset (Phase 11 M7), through WASAPI in shared mode: the microphone is turned into 20 ms frames of 48 kHz mono
/// while push-to-talk is held, and what comes in off the radio (several transmissions, or a clip from the review) is
/// mixed to the speakers. Without a microphone or speakers, voice is simply unavailable; nothing else depends on it.
/// </summary>
public sealed class RadioAudioDevices : IDisposable
{
    private static readonly WaveFormat Pcm = new(VoiceAudio.SampleRate, 16, 1);

    private readonly WasapiRecorder? _capture;
    private readonly WasapiPlayer? _output;
    private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(VoiceAudio.SampleRate, 1)) { ReadFully = true };
    private readonly ConcurrentDictionary<Guid, BufferedWaveProvider> _incoming = new();
    private readonly List<short> _pending = [];
    private double _resample;
    private volatile bool _transmitting;

    public RadioAudioDevices()
    {
        try
        {
            _output = new WasapiPlayerBuilder().WithSharedMode().WithLatency(80).WithCategory(AudioStreamCategory.Communications).Build();
            _output.Init(_mixer);
            _output.Play();
        }
        catch (Exception ex)
        {
            Problem = $"No speakers: {ex.Message}";
            _output = null;
        }
        try
        {
            // A headset microphone in communications mode (the system's voice processing), asked for in the codec's format.
            _capture = new WasapiRecorderBuilder().WithSharedMode().WithCommunicationsMode().WithFormat(Pcm).Build();
            _capture.DataAvailable += (buffer, _, _, _) => OnCaptured(buffer);
        }
        catch (Exception ex)
        {
            Problem = $"No microphone: {ex.Message}";
            _capture = null;
        }
    }

    public bool CanTalk => _capture is not null;
    public bool CanListen => _output is not null;

    /// <summary>Why voice isn't fully working, or null.</summary>
    public string? Problem { get; }

    /// <summary>A 20 ms frame from the microphone while transmitting. Raised on the capture thread.</summary>
    public event Action<short[]>? FrameCaptured;

    /// <summary>Starts or stops sending the microphone (push-to-talk). The microphone is open only while this is on.</summary>
    public bool Transmitting
    {
        get => _transmitting;
        set
        {
            if (_transmitting == value) return;
            _transmitting = value;
            try
            {
                if (value) _capture?.StartRecording();
                else _capture?.StopRecording();
            }
            catch (InvalidOperationException)
            {
            }
            lock (_pending) _pending.Clear();
        }
    }

    /// <summary>Microphone audio (whatever the device's format) to 48 kHz mono 16-bit, then to 20 ms frames.</summary>
    private void OnCaptured(ReadOnlySpan<byte> buffer)
    {
        if (!_transmitting || _capture is null) return;
        var format = _capture.WaveFormat;
        var channels = format.Channels;
        var step = (double)format.SampleRate / VoiceAudio.SampleRate;
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = buffer.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var offset = i * bytesPerSample * channels;
            mono[i] = format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
                ? BitConverter.ToSingle(buffer[offset..])
                : BitConverter.ToInt16(buffer[offset..]) / 32768f;
        }

        var ready = new List<short[]>();
        lock (_pending)
        {
            // Linear resampling to 48 kHz (usually already 48 kHz, so this just copies).
            for (; _resample < mono.Length - 1; _resample += step)
            {
                var index = (int)_resample;
                var fraction = (float)(_resample - index);
                var sample = mono[index] * (1 - fraction) + mono[index + 1] * fraction;
                _pending.Add((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
            }
            _resample -= mono.Length - 1;
            while (_pending.Count >= VoiceAudio.FrameSamples)
            {
                ready.Add([.. _pending.GetRange(0, VoiceAudio.FrameSamples)]);
                _pending.RemoveRange(0, VoiceAudio.FrameSamples);
            }
        }
        foreach (var frame in ready) FrameCaptured?.Invoke(frame);
    }

    /// <summary>Plays a frame of a transmission; each transmission has its own stream into the mix.</summary>
    public void Play(Guid transmissionId, short[] frame)
    {
        if (_output is null) return;
        var buffer = _incoming.GetOrAdd(transmissionId, _ =>
        {
            var created = new BufferedWaveProvider(Pcm, TimeSpan.FromSeconds(30)) { DiscardOnBufferOverflow = true, ReadFully = false };
            _mixer.AddMixerInput(created.ToSampleProvider());
            return created;
        });
        var bytes = new byte[frame.Length * 2];
        Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);
        buffer.AddSamples(bytes, 0, bytes.Length);
    }

    /// <summary>A transmission is over; its stream leaves the mix once it has played out.</summary>
    public void Finished(Guid transmissionId) => _incoming.TryRemove(transmissionId, out _);

    /// <summary>A whole recording (the review), played straight through.</summary>
    public void PlayClip(short[] pcm)
    {
        var id = Guid.NewGuid();
        for (var start = 0; start < pcm.Length; start += VoiceAudio.FrameSamples)
            Play(id, pcm[start..Math.Min(pcm.Length, start + VoiceAudio.FrameSamples)]);
        Finished(id);
    }

    public void Dispose()
    {
        Transmitting = false;
        _capture?.Dispose();
        _output?.Stop();
        _output?.Dispose();
    }
}
