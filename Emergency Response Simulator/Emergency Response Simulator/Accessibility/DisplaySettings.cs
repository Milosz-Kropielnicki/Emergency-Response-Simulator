using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>The colour scheme (Phase 21).</summary>
public enum Theme
{
    /// <summary>The control-room default: dark panels, bright status colours.</summary>
    Dark,

    /// <summary>Dark text on light panels, for bright rooms and projectors.</summary>
    Light,

    /// <summary>
    /// Night operations: red on black at low brightness, so a commander in a darkened command vehicle or out at night keeps
    /// their dark adaptation. Status is told apart by brightness and the words, not hue.
    /// </summary>
    Night,

    /// <summary>White on black with the strongest status colours, for low vision.</summary>
    HighContrast,
}

/// <summary>Colour vision the status and map colours are chosen for (Phase 21).</summary>
public enum ColourVision
{
    Standard,

    /// <summary>Protanopia and deuteranopia (red–green, about 1 man in 12): the Okabe–Ito colours, vermillion against bluish green.</summary>
    RedGreen,

    /// <summary>Tritanopia (blue–yellow): reds, oranges and greens, never blue against green.</summary>
    BlueYellow,
}

/// <summary>The reading font (Phase 21). Each falls back to a font every Windows machine has.</summary>
public enum ReadingFont
{
    Standard,

    /// <summary>OpenDyslexic or Lexend if installed, otherwise Verdana: wide letters, distinct shapes, generous spacing.</summary>
    DyslexiaFriendly,

    /// <summary>Atkinson Hyperlegible if installed, otherwise Verdana: designed for low vision.</summary>
    Hyperlegible,
}

/// <summary>A pop-out panel's place on the desktop (multi-monitor dashboards).</summary>
/// <param name="Panel">The right-panel tab it holds.</param>
public sealed record PanelPlacement(string Panel, double Left, double Top, double Width, double Height, bool Maximised = false);

/// <summary>A role's workspace as this user has customised it (panels shown, the one in front, the board).</summary>
public sealed record WorkspaceCustomisation(IReadOnlyList<string> Panels, string Focus, string Board);

/// <summary>
/// One user's display, input and accessibility settings (Phase 21), kept per machine in
/// %LOCALAPPDATA%\EmergencyResponseSimulator\display.json (or the folder in ERS_SETTINGS). Nothing here reaches the
/// server: it is how this person sees and drives the same picture.
/// </summary>
public sealed record DisplaySettings
{
    public Theme Theme { get; init; } = Theme.Dark;
    public ColourVision Vision { get; init; } = ColourVision.Standard;
    public ReadingFont Font { get; init; } = ReadingFont.Standard;

    /// <summary>Everything drawn larger: 1.0 to 2.0.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>The interface language: "en", "ga" or "pl" (see <see cref="Localization"/>).</summary>
    public string Language { get; init; } = "en";

    /// <summary>Spoken commands ("Simulator, pause"), off unless the user turns them on.</summary>
    public bool VoiceCommands { get; init; }

    /// <summary>Shortcut overrides by action name, e.g. "TogglePlay": "F5". Missing actions keep their default.</summary>
    public IReadOnlyDictionary<string, string> Hotkeys { get; init; } = new Dictionary<string, string>();

    /// <summary>Pop-out panels by workspace name, restored when that workspace is chosen.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PanelPlacement>> Dashboards { get; init; } = new Dictionary<string, IReadOnlyList<PanelPlacement>>();

    /// <summary>Customised workspaces by workspace name.</summary>
    public IReadOnlyDictionary<string, WorkspaceCustomisation> Workspaces { get; init; } = new Dictionary<string, WorkspaceCustomisation>();

    public const double MinScale = 1.0;
    public const double MaxScale = 2.0;

    /// <summary>The settings with anything out of range brought back in (a hand-edited file can hold anything).</summary>
    public DisplaySettings Sanitised() => this with
    {
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, MinScale, MaxScale) : 1.0,
        Language = Localization.Languages.Any(l => l.Code == Language) ? Language : "en",
        Hotkeys = Hotkeys ?? new Dictionary<string, string>(),
        Dashboards = Dashboards ?? new Dictionary<string, IReadOnlyList<PanelPlacement>>(),
        Workspaces = Workspaces ?? new Dictionary<string, WorkspaceCustomisation>(),
    };

    // ---- Storage ----

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder => Environment.GetEnvironmentVariable("ERS_SETTINGS") is { Length: > 0 } folder
        ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmergencyResponseSimulator");

    public static string FilePath => Path.Combine(Folder, "display.json");

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DisplaySettings FromJson(string json) => (JsonSerializer.Deserialize<DisplaySettings>(json, Json) ?? new DisplaySettings()).Sanitised();

    /// <summary>The saved settings, or the defaults when there are none or the file can't be read.</summary>
    public static DisplaySettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : new DisplaySettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DisplaySettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, ToJson());
    }
}
