using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Accessibility;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>An action and the shortcut the user wants for it.</summary>
public sealed partial class HotkeyRow(HotkeyAction action, string label, string gesture) : ObservableObject
{
    public HotkeyAction Action { get; } = action;
    public string Label { get; } = label;
    public string Default => Hotkeys.Defaults[Action];
    [ObservableProperty] private string _gesture = gesture;
}

/// <summary>A panel the workspace may show.</summary>
public sealed partial class PanelRow(WorkspacePanel panel, bool shown, bool fixedOn) : ObservableObject
{
    public WorkspacePanel Panel { get; } = panel;
    public string Name => Panel.ToString();

    /// <summary>The desk and the session tab are always there.</summary>
    public bool CanChange => !fixedOn;
    [ObservableProperty] private bool _shown = shown || fixedOn;
}

/// <summary>
/// Display and accessibility (Phase 21): theme, colour vision, reading font, size and language; voice commands; the
/// shortcuts; the current workspace's panels; and the pop-out layout. Applied at once and saved per machine.
/// </summary>
public sealed partial class DisplaySettingsViewModel : ObservableObject
{
    private readonly DisplayService _display;
    private readonly MainViewModel _main;

    public DisplaySettingsViewModel(DisplayService display, MainViewModel main)
    {
        _display = display;
        _main = main;
        Load();
    }

    /// <summary>Set by the main window: its pop-out panels as they are placed now.</summary>
    public Func<IReadOnlyList<PanelPlacement>>? CaptureLayout { get; set; }

    /// <summary>Set by the main window: the voice listener's state.</summary>
    public Func<string>? VoiceState { get; set; }

    public ObservableCollection<Choice<Theme>> Themes { get; } = [];
    public ObservableCollection<Choice<ColourVision>> Visions { get; } = [];
    public ObservableCollection<Choice<ReadingFont>> Fonts { get; } = [];
    public IReadOnlyList<Language> Languages => Localization.Languages;
    public ObservableCollection<HotkeyRow> Shortcuts { get; } = [];
    public ObservableCollection<PanelRow> Panels { get; } = [];
    public IReadOnlyList<WorkspacePanel> FocusChoices { get; } = Enum.GetValues<WorkspacePanel>();

    [ObservableProperty] private Choice<Theme>? _theme;
    [ObservableProperty] private Choice<ColourVision>? _vision;
    [ObservableProperty] private Choice<ReadingFont>? _font;
    [ObservableProperty] private Language? _language;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private bool _voiceCommands;
    [ObservableProperty] private string _voiceStatus = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _shortcutProblems = "";
    [ObservableProperty] private string _workspaceName = "";
    [ObservableProperty] private WorkspacePanel _focus;
    [ObservableProperty] private string _layoutSummary = "";

    /// <summary>0 display, 1 shortcuts, 2 workspace.</summary>
    [ObservableProperty] private int _selectedTab;

    private static string L(string key) => LocalizationManager.Instance[key];

    /// <summary>Fills the window from the settings in force and the workspace in use.</summary>
    public void Load()
    {
        var current = _display.Current;
        Replace(Themes, Enum.GetValues<Theme>().Select(t => new Choice<Theme>(t, L($"Theme.{t}"))));
        Replace(Visions, Enum.GetValues<ColourVision>().Select(v => new Choice<ColourVision>(v, L($"Vision.{v}"))));
        Replace(Fonts, Enum.GetValues<ReadingFont>().Select(f => new Choice<ReadingFont>(f, L($"Font.{f}"))));
        Theme = Themes.First(t => t.Value == current.Theme);
        Vision = Visions.First(v => v.Value == current.Vision);
        Font = Fonts.First(f => f.Value == current.Font);
        Language = Languages.First(l => l.Code == current.Language);
        Scale = current.Scale;
        VoiceCommands = current.VoiceCommands;
        VoiceStatus = VoiceState?.Invoke() ?? "";

        var (bindings, problems) = Hotkeys.Resolve(current.Hotkeys, _main.Radio.PttKey);
        Replace(Shortcuts, bindings.Select(b => new HotkeyRow(b.Action, L($"Action.{b.Action}"), b.Display)));
        ShortcutProblems = string.Join(Environment.NewLine, problems);

        var workspace = _main.SelectedWorkspace ?? RoleWorkspaces.Full;
        WorkspaceName = workspace.Name;
        Replace(Panels, Enum.GetValues<WorkspacePanel>().Select(p => new PanelRow(p, workspace.Panels.Contains(p),
            p is WorkspacePanel.Desk or WorkspacePanel.Session)));
        Focus = workspace.Focus;
        var saved = current.Dashboards.GetValueOrDefault(workspace.Name);
        LayoutSummary = saved is { Count: > 0 } ? string.Join(", ", saved.Select(p => p.Panel)) : "—";
    }

    private DisplaySettings Gather() => _display.Current with
    {
        Theme = Theme?.Value ?? Accessibility.Theme.Dark,
        Vision = Vision?.Value ?? ColourVision.Standard,
        Font = Font?.Value ?? ReadingFont.Standard,
        Language = Language?.Code ?? "en",
        Scale = Scale,
        VoiceCommands = VoiceCommands,
    };

    [RelayCommand]
    private void Apply()
    {
        Status = _display.Apply(Gather()) ?? L("Settings.Saved");
        Load();
    }

    /// <summary>Only the shortcuts that differ from the defaults are kept; refused ones are reported and the default stays.</summary>
    [RelayCommand]
    private void ApplyShortcuts()
    {
        var overrides = Shortcuts.Where(s => !string.IsNullOrWhiteSpace(s.Gesture) && s.Gesture.Trim() != s.Default)
            .ToDictionary(s => s.Action.ToString(), s => s.Gesture.Trim());
        var (_, problems) = Hotkeys.Resolve(overrides, _main.Radio.PttKey);
        Status = _display.Apply(Gather() with { Hotkeys = overrides }) ?? L("Settings.Saved");
        Load();
        ShortcutProblems = string.Join(Environment.NewLine, problems);
        SelectedTab = 1;
    }

    [RelayCommand]
    private void ResetShortcuts()
    {
        Status = _display.Apply(Gather() with { Hotkeys = new Dictionary<string, string>() }) ?? L("Settings.Saved");
        Load();
        SelectedTab = 1;
    }

    [RelayCommand]
    private void SaveWorkspace()
    {
        var custom = new WorkspaceCustomisation([.. Panels.Where(p => p.Shown).Select(p => p.Name)], Focus.ToString(),
            (_main.SelectedWorkspace ?? RoleWorkspaces.Full).Board.ToString());
        var workspaces = new Dictionary<string, WorkspaceCustomisation>(_display.Current.Workspaces) { [WorkspaceName] = custom };
        Status = _display.Apply(Gather() with { Workspaces = workspaces }) ?? L("Settings.Saved");
        _main.ApplyWorkspaceCustomisations(workspaces);
        Load();
        SelectedTab = 2;
    }

    [RelayCommand]
    private void ResetWorkspace()
    {
        var workspaces = new Dictionary<string, WorkspaceCustomisation>(_display.Current.Workspaces);
        workspaces.Remove(WorkspaceName);
        Status = _display.Apply(Gather() with { Workspaces = workspaces }) ?? L("Settings.Saved");
        _main.ApplyWorkspaceCustomisations(workspaces);
        Load();
        SelectedTab = 2;
    }

    [RelayCommand]
    private void SaveLayout()
    {
        var layout = CaptureLayout?.Invoke() ?? [];
        var dashboards = new Dictionary<string, IReadOnlyList<PanelPlacement>>(_display.Current.Dashboards) { [WorkspaceName] = layout };
        Status = _display.Apply(Gather() with { Dashboards = dashboards }) ?? L("Settings.Saved");
        Load();
        SelectedTab = 2;
    }

    [RelayCommand]
    private void ClearLayout()
    {
        var dashboards = new Dictionary<string, IReadOnlyList<PanelPlacement>>(_display.Current.Dashboards);
        dashboards.Remove(WorkspaceName);
        Status = _display.Apply(Gather() with { Dashboards = dashboards }) ?? L("Settings.Saved");
        Load();
        SelectedTab = 2;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
