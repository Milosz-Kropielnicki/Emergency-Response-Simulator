using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using Emergency_Response_Simulator.Accessibility;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 21: UI/UX and accessibility. Every theme and colour-vision palette meets WCAG AA; every string has every language;
/// shortcuts refuse clashes, bare letters and the push-to-talk key; voice commands need the wake word; pop-out panels
/// always come back on screen; role workspaces never show a player more than their role allows.
/// </summary>
public class AccessibilityTests
{
    [Fact]
    public void Every_theme_and_colour_vision_keeps_text_and_status_colours_at_WCAG_AA_on_every_surface()
    {
        foreach (var theme in Enum.GetValues<Theme>())
            foreach (var vision in Enum.GetValues<ColourVision>())
            {
                var palette = Palettes.Ui(theme, vision);
                foreach (var (name, colour) in palette.Foregrounds)
                    foreach (var (surface, background) in palette.Surfaces)
                        Assert.True(Rgb.Contrast(colour, background) >= 4.5,
                            $"{theme}/{vision}: {name} {colour} on {surface} {background} is {Rgb.Contrast(colour, background):0.00}:1");
            }
        Assert.True(Rgb.Contrast(Palettes.Ui(Theme.HighContrast, ColourVision.Standard).Text, Palettes.Ui(Theme.HighContrast, ColourVision.Standard).Background) >= 7);
    }

    [Fact]
    public void Night_operations_is_red_only_and_dim()
    {
        var night = Palettes.Ui(Theme.Night, ColourVision.Standard);
        foreach (var (name, colour) in night.Foregrounds.Concat(night.Surfaces))
            Assert.True(colour.R >= colour.G && colour.R >= colour.B, $"{name} {colour} isn't red-dominant");
        Assert.True(night.Text.Luminance < Palettes.Ui(Theme.Dark, ColourVision.Standard).Text.Luminance / 2, "night text should be far dimmer than day");
        Assert.Equal(night, Palettes.Ui(Theme.Night, ColourVision.RedGreen)); // status by brightness and words, not hue
    }

    /// <summary>How a protanope or deuteranope sees a colour (Viénot, Brettel and Mollon 1999, in linear RGB).</summary>
    private static (double R, double G, double B) Simulate(Rgb colour, bool protan)
    {
        static double Linear(byte c) => c / 255.0 <= 0.04045 ? c / 255.0 / 12.92 : Math.Pow((c / 255.0 + 0.055) / 1.055, 2.4);
        var (r, g, b) = (Linear(colour.R), Linear(colour.G), Linear(colour.B));
        return protan
            ? (0.11238 * r + 0.88762 * g, 0.11238 * r + 0.88762 * g, 0.00401 * r - 0.00401 * g + b)
            : (0.29031 * r + 0.70969 * g, 0.29031 * r + 0.70969 * g, -0.02197 * r + 0.02197 * g + b);
    }

    private static double SeenApart(Rgb a, Rgb b, bool protan)
    {
        var (x, y) = (Simulate(a, protan), Simulate(b, protan));
        return Math.Sqrt(Math.Pow(x.R - y.R, 2) + Math.Pow(x.G - y.G, 2) + Math.Pow(x.B - y.B, 2));
    }

    [Fact]
    public void The_red_green_palettes_keep_apart_what_protanopes_and_deuteranopes_would_confuse()
    {
        // The problem: the standard critical red and OK green look almost the same to a deuteranope.
        var standard = Palettes.Ui(Theme.Dark, ColourVision.Standard);
        Assert.True(SeenApart(standard.Critical, standard.Ok, protan: false) < 0.05);

        foreach (var protan in new[] { true, false })
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.HighContrast })
            {
                var ui = Palettes.Ui(theme, ColourVision.RedGreen);
                Assert.True(SeenApart(ui.Critical, ui.Ok, protan) >= 0.15, $"{theme}: critical and OK too close for {(protan ? "protan" : "deutan")}");
                Assert.True(SeenApart(ui.Critical, ui.Warning, protan) >= 0.05, $"{theme}: critical and warning too close");
            }
            var map = Palettes.Map(ColourVision.RedGreen);
            Assert.True(SeenApart(map.Fire, map.Ems, protan) >= 0.15);
            Assert.True(SeenApart(map.Hot, map.Cold, protan) >= 0.15);
            Assert.True(SeenApart(map.Immediate, map.Delayed, protan) >= 0.15);
        }

        // Blue–yellow: police isn't blue against an EMS green.
        var tritan = Palettes.Map(ColourVision.BlueYellow);
        Assert.True(tritan.Police.R > tritan.Police.G, "police should be purple, not blue, for tritanopes");
    }

    [Fact]
    public void Every_string_has_every_language_and_unknown_keys_fall_back()
    {
        foreach (var key in Localization.Keys)
            foreach (var language in Localization.Languages)
                Assert.False(string.IsNullOrWhiteSpace(Localization.Text(key, language.Code)), $"{key} has no {language.Code}");
        Assert.Equal("☰ Sraitheanna", Localization.Text("Header.Layers", "ga"));
        Assert.Equal("☰ Warstwy", Localization.Text("Header.Layers", "pl"));
        Assert.Equal("☰ Layers", Localization.Text("Header.Layers", "fr"));
        Assert.Equal("No.Such.Key", Localization.Text("No.Such.Key", "en"));
        foreach (var action in Enum.GetValues<HotkeyAction>())
            Assert.NotEqual($"Action.{action}", Localization.Text($"Action.{action}", "en"));
        foreach (var theme in Enum.GetValues<Theme>()) Assert.NotEqual($"Theme.{theme}", Localization.Text($"Theme.{theme}", "pl"));
    }

    [Fact]
    public void Every_Loc_key_the_windows_use_exists()
    {
        foreach (var file in new[] { "MainWindow.xaml", "DisplaySettingsWindow.xaml" })
            foreach (Match match in Regex.Matches(File.ReadAllText(Xaml(file)), @"\{acc:Loc ([A-Za-z.]+)\}"))
                Assert.Contains(match.Groups[1].Value, Localization.Keys);
    }

    [Fact]
    public void Shortcuts_resolve_defaults_and_refuse_clashes_bare_letters_and_the_push_to_talk_key()
    {
        var (defaults, none) = Hotkeys.Resolve(new Dictionary<string, string>(), Key.Oem3);
        Assert.Empty(none);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, defaults.Count);
        Assert.Equal(defaults.Count, defaults.Select(b => (b.Gesture.Key, b.Gesture.Modifiers)).Distinct().Count());
        Assert.Equal(HotkeyAction.TogglePlay, Hotkeys.Match(defaults, Key.F5, ModifierKeys.None));
        Assert.Equal(HotkeyAction.NightMode, Hotkeys.Match(defaults, Key.N, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Null(Hotkeys.Match(defaults, Key.N, ModifierKeys.None)); // typing an N is typing

        var (bindings, problems) = Hotkeys.Resolve(new Dictionary<string, string>
        {
            ["TogglePlay"] = "F9",          // fine
            ["Faster"] = "F9",              // clashes with TogglePlay: keeps its default
            ["Slower"] = "P",               // a bare letter: refused
            ["ToggleLayers"] = "Oem3",      // the push-to-talk key: refused
            ["Teleport"] = "F10",           // no such action
        }, Key.Oem3);
        Assert.Equal(HotkeyAction.TogglePlay, Hotkeys.Match(bindings, Key.F9, ModifierKeys.None));
        Assert.Equal(HotkeyAction.Faster, Hotkeys.Match(bindings, Key.OemPlus, ModifierKeys.Control));
        Assert.Equal(HotkeyAction.Slower, Hotkeys.Match(bindings, Key.OemMinus, ModifierKeys.Control));
        Assert.Equal(HotkeyAction.ToggleLayers, Hotkeys.Match(bindings, Key.L, ModifierKeys.Control));
        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void Voice_commands_need_the_wake_word_and_map_phrases_to_actions_and_panels()
    {
        Assert.Equal(new VoiceCommand(HotkeyAction.TogglePlay, On: false), VoiceCommands.Match("Simulator, pause."));
        Assert.Equal(new VoiceCommand(HotkeyAction.TogglePlay, On: true), VoiceCommands.Match("simulator start the clock"));
        Assert.Equal(new VoiceCommand(null, WorkspacePanel.Health), VoiceCommands.Match("Simulator, show health"));
        Assert.Equal(new VoiceCommand(HotkeyAction.NightMode, On: true), VoiceCommands.Match("SIMULATOR NIGHT MODE"));
        Assert.Null(VoiceCommands.Match("pause"));                    // no wake word: radio talk, not a command
        Assert.Null(VoiceCommands.Match("Simulator, launch missiles"));
        Assert.Null(VoiceCommands.Match("Simulator, show nothing"));
        Assert.All(VoiceCommands.Phrases, p => Assert.NotNull(VoiceCommands.Match($"{VoiceCommands.WakeWord} {p}")));
    }

    [Fact]
    public void A_pop_out_saved_on_a_missing_monitor_comes_back_onto_the_desktop()
    {
        var oneScreen = new DesktopArea(0, 0, 1920, 1080);
        var gone = Dashboards.Fit(new PanelPlacement("Comms", 3000, 200, 600, 800), oneScreen); // the second monitor is unplugged
        Assert.True(gone.Left <= oneScreen.Right - Dashboards.Reachable);
        var tiny = Dashboards.Fit(new PanelPlacement("Comms", double.NaN, -500, 10, 10), oneScreen);
        Assert.Equal(0, tiny.Left);
        Assert.Equal(0, tiny.Top);
        Assert.Equal(Dashboards.MinWidth, tiny.Width);
        var twoScreens = new DesktopArea(-1920, 0, 3840, 1080);
        Assert.Equal(-1800, Dashboards.Fit(new PanelPlacement("Health", -1800, 100, 600, 800), twoScreens).Left); // kept where it was
    }

    [Fact]
    public void Display_settings_round_trip_and_bring_a_hand_edited_file_back_into_range()
    {
        var settings = new DisplaySettings
        {
            Theme = Theme.Night, Vision = ColourVision.BlueYellow, Font = ReadingFont.DyslexiaFriendly, Scale = 1.5, Language = "ga", VoiceCommands = true,
            Hotkeys = new Dictionary<string, string> { ["TogglePlay"] = "F9" },
            Dashboards = new Dictionary<string, IReadOnlyList<PanelPlacement>> { ["Operations Section Chief"] = [new("Health", 1920, 0, 600, 1000, true)] },
            Workspaces = new Dictionary<string, WorkspaceCustomisation> { ["Safety Officer"] = new(["Crews", "Comms"], "Comms", "Status") },
        };
        var back = DisplaySettings.FromJson(settings.ToJson());
        Assert.Equal(settings.Theme, back.Theme);
        Assert.Equal(1.5, back.Scale);
        Assert.Equal("F9", back.Hotkeys["TogglePlay"]);
        Assert.True(back.Dashboards["Operations Section Chief"][0].Maximised);
        Assert.Equal("Comms", back.Workspaces["Safety Officer"].Focus);

        var wild = DisplaySettings.FromJson("""{"Scale": 9, "Language": "xx", "Theme": "Light"}""");
        Assert.Equal(DisplaySettings.MaxScale, wild.Scale);
        Assert.Equal("en", wild.Language);
        Assert.Equal(Theme.Light, wild.Theme);
    }

    [Fact]
    public void The_built_in_workspaces_are_consistent()
    {
        foreach (var workspace in RoleWorkspaces.Choices())
            Assert.Empty(RoleWorkspaces.Problems(workspace));
        Assert.Equal(Enum.GetValues<UserRole>().Length, RoleWorkspaces.All.Count); // one per role
    }

    [Fact]
    public void A_trainee_choosing_the_full_or_instructor_layout_gets_no_instructor_board_and_never_a_hidden_tab()
    {
        var instructor = RoleWorkspaces.For(UserRole.Instructor);
        var trainee = RoleWorkspaces.Resolve(instructor, assessor: false, instructorTools: false, networked: true);
        Assert.DoesNotContain(WorkspaceBoard.Instructor, trainee.Boards);
        Assert.DoesNotContain(WorkspacePanel.Assess, trainee.Panels);
        Assert.Contains(trainee.Board, trainee.Boards);
        Assert.Contains(trainee.Focus, trainee.Panels);
        Assert.DoesNotContain(WorkspaceBoard.Instructor, RoleWorkspaces.Resolve(RoleWorkspaces.Full, false, false, true).Boards);

        var standalone = RoleWorkspaces.Resolve(RoleWorkspaces.Full, assessor: false, instructorTools: true, networked: false);
        Assert.Contains(WorkspaceBoard.Instructor, standalone.Boards);
        Assert.DoesNotContain(WorkspacePanel.Session, standalone.Panels); // no session to show
        Assert.Equal(WorkspaceBoard.Instructor, RoleWorkspaces.Resolve(instructor, true, true, true).Board);
    }

    [Fact]
    public void A_customised_workspace_never_widens_what_a_role_sees_and_keeps_the_desk_and_session()
    {
        var safety = RoleWorkspaces.For(UserRole.Safety);
        var custom = RoleWorkspaces.Customise(safety, new WorkspaceCustomisation(["Comms", "Assess", "Bogus"], "Assess", "Instructor"));
        Assert.Equal([WorkspacePanel.Comms, WorkspacePanel.Session, WorkspacePanel.Desk], custom.Panels); // in tab order
        Assert.DoesNotContain(WorkspacePanel.Assess, custom.Panels);
        Assert.Contains(custom.Focus, custom.Panels);
        Assert.Equal(safety.Board, custom.Board); // the instructor's board isn't the safety officer's to add
        Assert.Empty(RoleWorkspaces.Problems(custom));
        Assert.Same(safety, RoleWorkspaces.Customise(safety, null));

        var observer = RoleWorkspaces.Customise(RoleWorkspaces.For(UserRole.Observer), new WorkspaceCustomisation(["Assess"], "Assess", "History"));
        Assert.Equal(WorkspacePanel.Assess, observer.Focus);
    }

    [Fact]
    public void Buttons_without_words_have_names_for_screen_readers_and_the_windows_use_theme_colours()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (var file in new[] { "MainWindow.xaml", "AarWindow.xaml", "ScenarioEditorWindow.xaml", "DisplaySettingsWindow.xaml", "DataExchangeWindow.xaml" })
            foreach (var button in XDocument.Load(Xaml(file)).Descendants().Where(e => e.Name == presentation + "Button" || e.Name == presentation + "ToggleButton"))
            {
                var content = (string?)button.Attribute("Content") ?? "";
                // A button led by a symbol or emoji is read out by its Unicode name first (bar chart, then AAR), so it needs a name.
                var text = content.StartsWith("{acc:Loc ", StringComparison.Ordinal) ? Localization.Text(content[9..^1], "en") : content;
                var needsName = text.Length > 0 && !text.StartsWith('{') && !char.IsLetterOrDigit(text[0]);
                if (needsName) Assert.True(button.Attribute("AutomationProperties.Name") is not null, $"{file}: \"{content}\" has no AutomationProperties.Name");
            }
        foreach (var file in new[] { "MainWindow.xaml", "AarWindow.xaml" })
            Assert.DoesNotMatch(new Regex("\"#[0-9A-Fa-f]{6,8}\""), File.ReadAllText(Xaml(file)));
    }

    private static string Xaml(string name)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Emergency Response Simulator", name))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder.FullName, "Emergency Response Simulator", name);
    }
}
