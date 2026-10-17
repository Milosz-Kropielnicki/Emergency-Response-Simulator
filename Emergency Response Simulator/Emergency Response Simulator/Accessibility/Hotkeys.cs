using System.Globalization;
using System.Windows.Input;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>What a shortcut or a voice command does (Phase 21). Each has a name in <see cref="Localization"/> ("Action.…").</summary>
public enum HotkeyAction
{
    TogglePlay,
    Faster,
    Slower,
    ToggleLayers,
    TogglePanels,
    NextPanel,
    PreviousPanel,
    PopOut,
    OpenIap,
    OpenForms,
    OpenAar,
    OpenStandards,
    OpenSettings,
    Acknowledge,
    NightMode,
    Shortcuts,
}

/// <summary>An action and the key that does it.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, KeyGesture Gesture)
{
    public string Display => Hotkeys.Describe(Gesture);
}

/// <summary>
/// Keyboard shortcuts (Phase 21): defaults chosen so none needs the mouse and none collides with typing (letters and digits
/// always need Ctrl or Alt; function keys stand alone), each one overridable in the display settings. A shortcut that
/// clashes with another, can't be parsed, or takes the push-to-talk key is refused and the default kept.
/// </summary>
public static class Hotkeys
{
    public static IReadOnlyDictionary<HotkeyAction, string> Defaults { get; } = new Dictionary<HotkeyAction, string>
    {
        [HotkeyAction.TogglePlay] = "F5",
        [HotkeyAction.Faster] = "Ctrl+OemPlus",
        [HotkeyAction.Slower] = "Ctrl+OemMinus",
        [HotkeyAction.ToggleLayers] = "Ctrl+L",
        [HotkeyAction.TogglePanels] = "Ctrl+R",
        [HotkeyAction.NextPanel] = "Ctrl+PageDown",
        [HotkeyAction.PreviousPanel] = "Ctrl+PageUp",
        [HotkeyAction.PopOut] = "Ctrl+Shift+P",
        [HotkeyAction.OpenIap] = "Ctrl+Shift+I",
        [HotkeyAction.OpenForms] = "Ctrl+Shift+F",
        [HotkeyAction.OpenAar] = "Ctrl+Shift+A",
        [HotkeyAction.OpenStandards] = "Ctrl+Shift+D",
        [HotkeyAction.OpenSettings] = "Ctrl+OemComma",
        [HotkeyAction.Acknowledge] = "Ctrl+Shift+K",
        [HotkeyAction.NightMode] = "Ctrl+Shift+N",
        [HotkeyAction.Shortcuts] = "F1",
    };

    private static readonly KeyGestureConverter Converter = new();

    /// <summary>A gesture from text ("Ctrl+Shift+N", "F5"); null when it isn't one WPF accepts (a bare letter isn't).</summary>
    public static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return Converter.ConvertFromString(null, CultureInfo.InvariantCulture, text.Trim()) as KeyGesture;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public static string Describe(KeyGesture gesture) =>
        (string?)Converter.ConvertTo(null, CultureInfo.InvariantCulture, gesture, typeof(string)) ?? gesture.Key.ToString();

    /// <summary>The bindings in force: the defaults with the user's overrides, and why any override was refused.</summary>
    public static (IReadOnlyList<HotkeyBinding> Bindings, IReadOnlyList<string> Problems) Resolve(IReadOnlyDictionary<string, string> overrides, Key pushToTalk)
    {
        var problems = new List<string>();
        var chosen = Defaults.ToDictionary(d => d.Key, d => Parse(d.Value)!);
        foreach (var (name, text) in overrides)
        {
            if (!Enum.TryParse<HotkeyAction>(name, out var action))
            {
                problems.Add($"No action called {name}.");
                continue;
            }
            if (Parse(text) is not { } gesture)
            {
                problems.Add($"{name}: \"{text}\" isn't a shortcut (letters and digits need Ctrl or Alt).");
                continue;
            }
            if (gesture.Key == pushToTalk)
            {
                problems.Add($"{name}: {text} is the push-to-talk key.");
                continue;
            }
            chosen[action] = gesture;
        }

        // A clash keeps the earlier action's key and gives the later one back its default (or nothing, if that clashes too).
        var bindings = new List<HotkeyBinding>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var gesture = chosen[action];
            if (bindings.FirstOrDefault(b => Same(b.Gesture, gesture)) is { } taken)
            {
                problems.Add($"{action}: {Describe(gesture)} is already {taken.Action}.");
                gesture = Parse(Defaults[action])!;
                if (bindings.Any(b => Same(b.Gesture, gesture))) continue;
            }
            bindings.Add(new HotkeyBinding(action, gesture));
        }
        return (bindings, problems);
    }

    public static bool Same(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.Modifiers == b.Modifiers;

    /// <summary>The action a key press means, if any.</summary>
    public static HotkeyAction? Match(IEnumerable<HotkeyBinding> bindings, Key key, ModifierKeys modifiers) =>
        bindings.FirstOrDefault(b => b.Gesture.Key == key && b.Gesture.Modifiers == modifiers)?.Action;
}
