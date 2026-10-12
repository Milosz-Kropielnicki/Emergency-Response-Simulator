using Concentus;
using Concentus.Enums;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Voice;

/// <summary>
/// Voice audio (Phase 11 M7): 48 kHz mono, in 20 ms Opus frames. Shared by the clients (capture, playback) and the
/// server (routing, radio effects, recording, transcription).
/// </summary>
public static class VoiceAudio
{
    public const int SampleRate = 48_000;
    public const int FrameMilliseconds = 20;
    public const int FrameSamples = SampleRate * FrameMilliseconds / 1000; // 960
    public const int MaxPacket = 1275;

    public static TimeSpan Duration(int frames) => TimeSpan.FromMilliseconds(frames * FrameMilliseconds);

    /// <summary>Whisper's input: 16 kHz mono float, from 48 kHz by averaging each three samples (a crude but adequate low-pass).</summary>
    public static float[] To16k(IReadOnlyList<short> pcm48)
    {
        var output = new float[pcm48.Count / 3];
        for (var i = 0; i < output.Length; i++)
            output[i] = (pcm48[3 * i] + pcm48[3 * i + 1] + pcm48[3 * i + 2]) / (3f * short.MaxValue);
        return output;
    }

    /// <summary>Loudness of a frame (root mean square, 0–1), e.g. to tell speech from silence.</summary>
    public static double Level(ReadOnlySpan<short> frame)
    {
        if (frame.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in frame) sum += (double)sample * sample;
        return Math.Sqrt(sum / frame.Length) / short.MaxValue;
    }
}

/// <summary>Encodes 20 ms frames of 48 kHz mono PCM to Opus (Concentus, voice mode).</summary>
public sealed class OpusFrameEncoder
{
    private readonly IOpusEncoder _encoder = OpusCodecFactory.CreateEncoder(VoiceAudio.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);

    public OpusFrameEncoder(int bitrate = 24_000) => _encoder.Bitrate = bitrate;

    public byte[] Encode(ReadOnlySpan<short> frame)
    {
        if (frame.Length != VoiceAudio.FrameSamples) throw new ArgumentException($"A frame is {VoiceAudio.FrameSamples} samples.", nameof(frame));
        Span<byte> packet = stackalloc byte[VoiceAudio.MaxPacket];
        var length = _encoder.Encode(frame, VoiceAudio.FrameSamples, packet, packet.Length);
        return packet[..length].ToArray();
    }
}

/// <summary>Decodes Opus packets back to 20 ms frames of 48 kHz mono PCM; a lost packet is concealed.</summary>
public sealed class OpusFrameDecoder
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceAudio.SampleRate, 1);

    public short[] Decode(byte[]? packet)
    {
        var frame = new short[VoiceAudio.FrameSamples];
        var samples = _decoder.Decode(packet ?? ReadOnlySpan<byte>.Empty, frame, VoiceAudio.FrameSamples, false);
        return samples == frame.Length ? frame : frame[..Math.Max(0, samples)];
    }
}

/// <summary>
/// A recorded transmission on disk (Phase 11 M7): "ERSO", the sample rate and frame length, then each Opus packet with
/// its length. Plain framing rather than an Ogg container: clips are only ever read back by this program.
/// </summary>
public static class OpusClip
{
    private static readonly byte[] Magic = "ERSO"u8.ToArray();

    public static byte[] Write(IReadOnlyList<byte[]> packets)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(VoiceAudio.SampleRate);
        writer.Write((ushort)VoiceAudio.FrameMilliseconds);
        writer.Write(packets.Count);
        foreach (var packet in packets)
        {
            writer.Write((ushort)packet.Length);
            writer.Write(packet);
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static IReadOnlyList<byte[]> Read(byte[] clip)
    {
        using var reader = new BinaryReader(new MemoryStream(clip));
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a voice clip.");
        reader.ReadInt32();
        reader.ReadUInt16();
        var count = reader.ReadInt32();
        var packets = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
            packets.Add(reader.ReadBytes(reader.ReadUInt16()));
        return packets;
    }

    /// <summary>The whole clip as 48 kHz mono PCM.</summary>
    public static short[] Decode(byte[] clip)
    {
        var decoder = new OpusFrameDecoder();
        return [.. Read(clip).SelectMany(decoder.Decode)];
    }
}

/// <summary>
/// What the radio does to a voice (Phase 11 M7, the Phase 7 model applied to audio): every transmission is band-limited
/// to a handset's 300–3,000 Hz with a little hiss; a broken one loses some 20 ms frames to crackle; a garbled one (two
/// people keyed at once) loses most of them and is clipped hard. One instance per transmission keeps the filters' state.
/// </summary>
public sealed class RadioEffect(CommsQuality quality, int seed)
{
    private readonly Random _random = new(seed);
    private double _highPrevIn, _highPrevOut, _low;

    public CommsQuality Quality { get; } = quality;

    public short[] Apply(ReadOnlySpan<short> frame)
    {
        var output = new short[frame.Length];
        var lost = Quality switch
        {
            CommsQuality.Broken => _random.NextDouble() < 0.25,
            CommsQuality.Garbled => _random.NextDouble() < 0.6,
            _ => false,
        };
        const double highAlpha = 0.962; // ~300 Hz high-pass at 48 kHz
        const double lowAlpha = 0.33;   // ~3 kHz low-pass at 48 kHz
        for (var i = 0; i < frame.Length; i++)
        {
            double x = lost ? (_random.NextDouble() - 0.5) * 3000 : frame[i];
            var high = highAlpha * (_highPrevOut + x - _highPrevIn);
            _highPrevIn = x;
            _highPrevOut = high;
            _low += lowAlpha * (high - _low);
            var y = _low * (Quality == CommsQuality.Garbled ? 3 : 1.4) + (_random.NextDouble() - 0.5) * 400;
            output[i] = (short)Math.Clamp(y, short.MinValue, short.MaxValue);
        }
        return output;
    }
}
