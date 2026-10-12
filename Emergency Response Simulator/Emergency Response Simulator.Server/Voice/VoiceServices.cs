using Emergency_Response_Simulator.Simulation.Remote;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace Emergency_Response_Simulator.Server.Voice;

/// <summary>Bound from the "Voice" configuration section (Phase 11 M7).</summary>
public sealed class VoiceOptions
{
    public const string SectionName = "Voice";

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErsVoice");

    /// <summary>Where recorded transmissions are kept, one folder per session.</summary>
    public string ClipDirectory { get; set; } = Path.Combine(DefaultRoot, "clips");

    /// <summary>How long recordings are kept before they are deleted. Shown to everyone at join (the consent notice).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>The Whisper model (ggml) for transcription. Missing: speech is recorded and relayed but not transcribed.</summary>
    public string WhisperModel { get; set; } = Path.Combine(DefaultRoot, "models", "ggml-base.en.bin");

    /// <summary>A transmission is cut off after this long, as a real radio's time-out timer does.</summary>
    public TimeSpan MaxTransmission { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Speech to text for radio transmissions (Phase 11 M7), so simulated crews hear what players say.</summary>
public interface ISpeechRecognizer
{
    bool Available { get; }

    /// <summary>The text of 16 kHz mono speech, biased towards <paramref name="vocabulary"/> (call signs); null if nothing was heard.</summary>
    Task<string?> TranscribeAsync(float[] samples16k, IReadOnlyList<string> vocabulary, CancellationToken cancellationToken = default);
}

/// <summary>No recogniser configured: transmissions are recorded and relayed between players, but not transcribed.</summary>
public sealed class NoSpeechRecognizer : ISpeechRecognizer
{
    public bool Available => false;

    public Task<string?> TranscribeAsync(float[] samples16k, IReadOnlyList<string> vocabulary, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Whisper (whisper.cpp through Whisper.net), on the CPU, English only. The session's call signs go in the prompt so
/// "Engine 4" comes out as a call sign rather than "engine for". One transcription at a time; the model is loaded once.
/// </summary>
public sealed class WhisperRecognizer(string modelPath, ILogger<WhisperRecognizer> logger) : ISpeechRecognizer, IDisposable
{
    private readonly Lazy<WhisperFactory> _factory = new(() => WhisperFactory.FromPath(modelPath));
    private readonly SemaphoreSlim _one = new(1, 1);

    public bool Available => true;

    public async Task<string?> TranscribeAsync(float[] samples16k, IReadOnlyList<string> vocabulary, CancellationToken cancellationToken = default)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            using var processor = _factory.Value.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt(Simulation.Voice.Transcripts.Prompt(vocabulary))
                .WithNoContext()
                .Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples16k, cancellationToken))
                text.Append(segment.Text);
            var said = text.ToString().Trim();
            return said.Length == 0 || said.StartsWith('[') ? null : said; // "[BLANK_AUDIO]" and the like
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Transcription failed");
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        if (_factory.IsValueCreated) _factory.Value.Dispose();
    }
}

/// <summary>Keeps recorded transmissions (Opus clips) for the review.</summary>
public interface IClipStore
{
    Task SaveAsync(Guid sessionId, Guid clipId, byte[] clip, CancellationToken cancellationToken = default);

    Task<byte[]?> LoadAsync(Guid sessionId, Guid clipId, CancellationToken cancellationToken = default);

    /// <summary>Deletes clips older than the cut-off; returns how many.</summary>
    int DeleteOlderThan(DateTimeOffset cutoff);
}

/// <summary>Clips as files: <c>{ClipDirectory}/{session}/{clip}.erso</c>.</summary>
public sealed class FileClipStore(IOptions<VoiceOptions> options) : IClipStore
{
    private string Root => options.Value.ClipDirectory;

    private string PathOf(Guid sessionId, Guid clipId) => Path.Combine(Root, sessionId.ToString("N"), $"{clipId:N}.erso");

    public async Task SaveAsync(Guid sessionId, Guid clipId, byte[] clip, CancellationToken cancellationToken = default)
    {
        var path = PathOf(sessionId, clipId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, clip, cancellationToken);
    }

    public async Task<byte[]?> LoadAsync(Guid sessionId, Guid clipId, CancellationToken cancellationToken = default)
    {
        var path = PathOf(sessionId, clipId);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public int DeleteOlderThan(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(Root)) return 0;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*.erso", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime) continue;
            File.Delete(file);
            deleted++;
        }
        foreach (var folder in Directory.EnumerateDirectories(Root).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
            Directory.Delete(folder);
        return deleted;
    }
}

/// <summary>Deletes recordings past the retention period, at start-up and then daily.</summary>
public sealed class VoiceRetention(IClipStore clips, IOptions<VoiceOptions> options, ILogger<VoiceRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = clips.DeleteOlderThan(DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays));
                if (deleted > 0) logger.LogInformation("Deleted {Count} voice recording(s) past the {Days}-day retention period", deleted, options.Value.RetentionDays);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Voice retention sweep failed");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}

/// <summary>
/// How voice reaches clients (Phase 11 M7). The router decides who hears what; the transport only carries it, so a
/// dedicated media path (UDP, WebRTC) could replace SignalR without touching the router.
/// </summary>
public interface IVoiceTransport
{
    Task StartedAsync(IReadOnlyCollection<string> connections, VoiceTalk talk);

    Task AudioAsync(IReadOnlyCollection<string> connections, Guid transmissionId, int sequence, byte[] packet);

    Task EndedAsync(IReadOnlyCollection<string> connections, Guid transmissionId);
}

/// <summary>Voice over the simulation hub's SignalR connections.</summary>
public sealed class SignalRVoiceTransport(IHubContext<SimulationHub, ISimulationClient> hub) : IVoiceTransport
{
    public Task StartedAsync(IReadOnlyCollection<string> connections, VoiceTalk talk) =>
        connections.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(connections).VoiceStarted(talk);

    public Task AudioAsync(IReadOnlyCollection<string> connections, Guid transmissionId, int sequence, byte[] packet) =>
        connections.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(connections).VoiceAudio(transmissionId, sequence, packet);

    public Task EndedAsync(IReadOnlyCollection<string> connections, Guid transmissionId) =>
        connections.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(connections).VoiceEnded(transmissionId);
}
