using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>A recognised command: an action, or a panel to bring to the front; for the clock and night mode, which way.</summary>
public sealed record VoiceCommand(HotkeyAction? Action, WorkspacePanel? Panel = null, bool? On = null);

/// <summary>
/// Spoken commands (Phase 21), for hands busy with a radio or a map: "Simulator, pause", "Simulator, show health",
/// "Simulator, night mode". The wake word keeps radio traffic and conversation from being taken as commands. The phrases
/// are English, the language of the exercise, whatever the interface language. Recognition is Windows' own offline
/// recogniser (<see cref="VoiceCommandListener"/>); this class is the grammar and what each phrase means.
/// </summary>
public static class VoiceCommands
{
    public const string WakeWord = "simulator";

    private static readonly (string Phrase, VoiceCommand Command)[] Commands =
    [
        ("start", new(HotkeyAction.TogglePlay, On: true)),
        ("resume", new(HotkeyAction.TogglePlay, On: true)),
        ("start the clock", new(HotkeyAction.TogglePlay, On: true)),
        ("pause", new(HotkeyAction.TogglePlay, On: false)),
        ("stop the clock", new(HotkeyAction.TogglePlay, On: false)),
        ("faster", new(HotkeyAction.Faster)),
        ("speed up", new(HotkeyAction.Faster)),
        ("slower", new(HotkeyAction.Slower)),
        ("slow down", new(HotkeyAction.Slower)),
        ("layers", new(HotkeyAction.ToggleLayers)),
        ("side panel", new(HotkeyAction.TogglePanels)),
        ("next panel", new(HotkeyAction.NextPanel)),
        ("previous panel", new(HotkeyAction.PreviousPanel)),
        ("pop out", new(HotkeyAction.PopOut)),
        ("action plan", new(HotkeyAction.OpenIap)),
        ("open the plan", new(HotkeyAction.OpenIap)),
        ("forms", new(HotkeyAction.OpenForms)),
        ("review", new(HotkeyAction.OpenAar)),
        ("standards", new(HotkeyAction.OpenStandards)),
        ("display settings", new(HotkeyAction.OpenSettings)),
        ("acknowledge", new(HotkeyAction.Acknowledge)),
        ("acknowledge alert", new(HotkeyAction.Acknowledge)),
        ("night mode", new(HotkeyAction.NightMode, On: true)),
        ("day mode", new(HotkeyAction.NightMode, On: false)),
        ("shortcuts", new(HotkeyAction.Shortcuts)),
        ("help", new(HotkeyAction.Shortcuts)),
    ];

    /// <summary>Spoken names of the panels, for "show …".</summary>
    private static readonly (string Name, WorkspacePanel Panel)[] PanelNames =
    [
        ("comms", WorkspacePanel.Comms), ("radio", WorkspacePanel.Comms), ("incident", WorkspacePanel.Incident), ("unit", WorkspacePanel.Unit),
        ("command", WorkspacePanel.Ics), ("crews", WorkspacePanel.Crews), ("agencies", WorkspacePanel.Agencies), ("session", WorkspacePanel.Session),
        ("assessment", WorkspacePanel.Assess), ("desk", WorkspacePanel.Desk), ("public", WorkspacePanel.Public), ("logistics", WorkspacePanel.Logistics),
        ("health", WorkspacePanel.Health), ("evacuation", WorkspacePanel.Protection),
    ];

    /// <summary>Every phrase after the wake word, for the recogniser's grammar.</summary>
    public static IReadOnlyList<string> Phrases { get; } =
        [.. Commands.Select(c => c.Phrase), .. PanelNames.Select(p => $"show {p.Name}")];

    /// <summary>What was said means, or null: it must start with the wake word ("Simulator, show health").</summary>
    public static VoiceCommand? Match(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard)) return null;
        var words = new string(heard.ToLowerInvariant().Select(c => char.IsLetter(c) || c == ' ' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] != WakeWord) return null;
        var phrase = string.Join(' ', words.Skip(1));
        if (phrase.StartsWith("show ", StringComparison.Ordinal))
        {
            var name = phrase[5..];
            if (PanelNames.FirstOrDefault(p => p.Name == name) is { Name: not null } panel) return new VoiceCommand(null, panel.Panel);
        }
        return Commands.FirstOrDefault(c => c.Phrase == phrase).Command;
    }
}
