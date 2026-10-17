using System.Globalization;
using System.Speech.Recognition;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>
/// Listens for <see cref="VoiceCommands"/> with Windows' offline speech recogniser (Phase 21). Only the command grammar is
/// loaded, so nothing else said is transcribed, and nothing leaves the machine. Paused while the user talks on the radio.
/// </summary>
public sealed class VoiceCommandListener : IDisposable
{
    /// <summary>Below this the recogniser's guess is ignored rather than acted on.</summary>
    public const float MinimumConfidence = 0.6f;

    private SpeechRecognitionEngine? _engine;

    /// <summary>Raised on the recogniser's thread with what was heard and what it means.</summary>
    public event EventHandler<(string Heard, VoiceCommand Command)>? Recognised;

    public bool Listening => _engine is not null;

    /// <summary>True while push-to-talk is held: radio speech is never a command.</summary>
    public bool Paused { get; set; }

    /// <summary>Starts listening; returns why it can't, or null.</summary>
    public string? Start()
    {
        if (_engine is not null) return null;
        try
        {
            var recogniser = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
            if (recogniser is null) return Localization.Text("Voice.Unavailable", LocalizationManager.Instance.Language);

            var engine = new SpeechRecognitionEngine(recogniser);
            var phrases = new Choices([.. VoiceCommands.Phrases.Select(p => $"{VoiceCommands.WakeWord} {p}")]);
            engine.LoadGrammar(new Grammar(new GrammarBuilder(phrases) { Culture = recogniser.Culture }) { Name = "commands" });
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += (_, e) =>
            {
                if (Paused || e.Result.Confidence < MinimumConfidence) return;
                if (VoiceCommands.Match(e.Result.Text) is { } command) Recognised?.Invoke(this, (e.Result.Text, command));
            };
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _engine = engine;
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", Localization.Text("Voice.Unavailable", LocalizationManager.Instance.Language), ex.Message);
        }
    }

    public void Stop()
    {
        if (_engine is null) return;
        try
        {
            _engine.RecognizeAsyncCancel();
        }
        catch (InvalidOperationException)
        {
        }
        _engine.Dispose();
        _engine = null;
    }

    public void Dispose() => Stop();
}
