using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Emergency_Response_Simulator.Accessibility;
using Emergency_Response_Simulator.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator
{
    /// <summary>
    /// Usability under pressure (Phase 21): shortcuts and voice commands for every header action, panels popped out to
    /// other monitors and restored with the workspace, display settings applied as they change, and command outcomes
    /// announced to screen readers.
    /// </summary>
    public partial class MainWindow
    {
        private DisplayService _display = null!;
        private IReadOnlyList<HotkeyBinding> _hotkeys = [];
        private readonly VoiceCommandListener _voice = new();
        private string _voiceState = "";
        private readonly Dictionary<WorkspacePanel, Window> _popOuts = [];
        private DisplaySettingsWindow? _settingsWindow;
        private DisplaySettingsViewModel? _settings;
        private Theme _dayTheme = Theme.Dark;
        private string? _restoredFor;

        private void InitialiseAccessibility(IServiceProvider services)
        {
            _display = services.GetRequiredService<DisplayService>();
            _dayTheme = _display.Current.Theme == Theme.Night ? Theme.Dark : _display.Current.Theme;
            _viewModel.ApplyWorkspaceCustomisations(_display.Current.Workspaces);
            OnDisplayChanged();
            _display.Changed += (_, _) => OnDisplayChanged();

            PreviewKeyDown += async (_, e) =>
            {
                if (e.IsRepeat || e.Handled) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (Hotkeys.Match(_hotkeys, key, Keyboard.Modifiers) is not { } action) return;
                e.Handled = true;
                await ExecuteAsync(new VoiceCommand(action));
            };
            // Radio speech is never a command.
            PreviewKeyDown += (_, e) => { if (e.Key == _viewModel.Radio.PttKey) _voice.Paused = true; };
            PreviewKeyUp += (_, e) => { if (e.Key == _viewModel.Radio.PttKey) _voice.Paused = false; };
            _voice.Recognised += (_, heard) => Dispatcher.InvokeAsync(async () =>
            {
                _viewModel.CommandMessage = LocalizationManager.Instance.Format("Voice.Heard", heard.Heard);
                await ExecuteAsync(heard.Command);
            });

            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.CommandMessage)) Announce(CommandMessageText);
                if (e.PropertyName == nameof(MainViewModel.SelectedWorkspace)) RestoreLayout();
            };
            Loaded += (_, _) => RestoreLayout();
            Closed += (_, _) =>
            {
                _voice.Dispose();
                foreach (var window in _popOuts.Values.ToList()) window.Close();
            };
        }

        /// <summary>Re-binds the shortcuts, starts or stops listening, and redraws the map in the new colours.</summary>
        private void OnDisplayChanged()
        {
            var current = _display.Current;
            _hotkeys = Hotkeys.Resolve(current.Hotkeys, _viewModel.Radio.PttKey).Bindings;
            if (current.Theme != Theme.Night) _dayTheme = current.Theme;
            if (current.VoiceCommands && !_voice.Listening)
                _voiceState = _voice.Start() ?? LocalizationManager.Instance["Voice.Listening"];
            else if (!current.VoiceCommands)
            {
                _voice.Stop();
                _voiceState = LocalizationManager.Instance["Voice.Off"];
            }
            // Night operations: the basemap dimmed against the black behind it, so the screen doesn't glare.
            MapView.Opacity = current.Theme == Theme.Night ? NightMapOpacity : 1.0;
            _map.Restyle();
        }

        /// <summary>How bright the map is at night.</summary>
        private const double NightMapOpacity = 0.45;

        private async Task ExecuteAsync(VoiceCommand command)
        {
            if (command.Panel is { } panel)
            {
                if (!_viewModel.ShowPanel(panel) && _popOuts.TryGetValue(panel, out var window)) window.Activate();
                return;
            }
            switch (command.Action)
            {
                case HotkeyAction.TogglePlay: await _viewModel.RunClockAsync(command.On); break;
                case HotkeyAction.Faster: await _viewModel.StepSpeedAsync(+1); break;
                case HotkeyAction.Slower: await _viewModel.StepSpeedAsync(-1); break;
                case HotkeyAction.ToggleLayers: _viewModel.ToggleLeftPanelCommand.Execute(null); break;
                case HotkeyAction.TogglePanels: _viewModel.ToggleRightPanelCommand.Execute(null); break;
                case HotkeyAction.NextPanel: _viewModel.CyclePanel(+1); break;
                case HotkeyAction.PreviousPanel: _viewModel.CyclePanel(-1); break;
                case HotkeyAction.PopOut: PopOut((WorkspacePanel)_viewModel.RightPanelTab); break;
                case HotkeyAction.OpenIap: _viewModel.OpenIapCommand.Execute(null); break;
                case HotkeyAction.OpenForms: _viewModel.OpenFormsCommand.Execute(null); break;
                case HotkeyAction.OpenAar: _viewModel.OpenAarCommand.Execute(null); break;
                case HotkeyAction.OpenStandards: _viewModel.OpenDataExchangeCommand.Execute(null); break;
                case HotkeyAction.OpenSettings: ShowSettings(0); break;
                case HotkeyAction.Shortcuts: ShowSettings(1); break;
                case HotkeyAction.Acknowledge: await _viewModel.AcknowledgeOldestAlertAsync(); break;
                case HotkeyAction.NightMode:
                    var night = command.On ?? _display.Current.Theme != Theme.Night;
                    _display.Apply(_display.Current with { Theme = night ? Theme.Night : _dayTheme });
                    break;
            }
        }

        // ---- Screen readers ----

        /// <summary>Tells a screen reader the element's text changed (it is a live region).</summary>
        private static void Announce(UIElement element)
        {
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        // ---- Settings ----

        private void DisplaySettings_Click(object sender, RoutedEventArgs e) => ShowSettings(0);

        private void ShowSettings(int tab)
        {
            _settings ??= new DisplaySettingsViewModel(_display, _viewModel)
            {
                CaptureLayout = CaptureLayout,
                VoiceState = () => _voiceState,
            };
            _settings.Load();
            _settings.SelectedTab = tab;
            if (_settingsWindow is null)
            {
                _settingsWindow = new DisplaySettingsWindow(_settings) { Owner = this };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            else
            {
                if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                _settingsWindow.Activate();
            }
        }

        // ---- Multi-monitor dashboards ----

        /// <summary>
        /// Moves a right-panel tab's content into a window of its own, which can go to any monitor. The tab keeps a note;
        /// closing the window puts the panel back.
        /// </summary>
        private Window? PopOut(WorkspacePanel panel, PanelPlacement? at = null)
        {
            if (_popOuts.TryGetValue(panel, out var open))
            {
                open.Activate();
                return open;
            }
            if (RightTabs.Items[(int)panel] is not TabItem tab || tab.Content is not UIElement content) return null;
            var name = tab.Header as string ?? panel.ToString();

            tab.Content = new TextBlock
            {
                Text = LocalizationManager.Instance["Name.PopOut"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
                Style = (Style)FindResource("EmptyHint"),
            };
            var host = new Border { Child = content, DataContext = tab.DataContext, Padding = new Thickness(8) };
            var window = new Window
            {
                Title = $"{name} · Emergency Response Simulator",
                Content = host,
                DataContext = DataContext,
                Owner = this,
                ShowInTaskbar = true,
                Width = 480,
                Height = 720,
                Tag = panel,
            };
            window.SetResourceReference(BackgroundProperty, "BackgroundBrush"); // follows the theme
            window.SetResourceReference(ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(window, name);
            if (at is { } place)
            {
                var fitted = Dashboards.Fit(place, Desktop());
                (window.WindowStartupLocation, window.Left, window.Top, window.Width, window.Height) =
                    (WindowStartupLocation.Manual, fitted.Left, fitted.Top, fitted.Width, fitted.Height);
            }
            window.Closed += (_, _) =>
            {
                host.Child = null;
                tab.Content = content;
                _popOuts.Remove(panel);
            };
            _popOuts[panel] = window;
            window.Show();
            if (at is { Maximised: true }) window.WindowState = WindowState.Maximized;
            return window;
        }

        private static DesktopArea Desktop() => new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        private IReadOnlyList<PanelPlacement> CaptureLayout() =>
        [
            .. _popOuts.Select(p =>
            {
                var bounds = p.Value.WindowState == WindowState.Normal
                    ? new Rect(p.Value.Left, p.Value.Top, p.Value.ActualWidth, p.Value.ActualHeight)
                    : p.Value.RestoreBounds;
                return new PanelPlacement(p.Key.ToString(), bounds.Left, bounds.Top, bounds.Width, bounds.Height, p.Value.WindowState == WindowState.Maximized);
            }),
        ];

        /// <summary>Opens the pop-outs saved with the workspace now in use (closing any others), once per workspace choice.</summary>
        private void RestoreLayout()
        {
            if (!IsLoaded || _viewModel.SelectedWorkspace is not { } workspace || _restoredFor == workspace.Name) return;
            _restoredFor = workspace.Name;
            foreach (var window in _popOuts.Values.ToList()) window.Close();
            if (!_display.Current.Dashboards.TryGetValue(workspace.Name, out var layout)) return;
            var visible = _viewModel.VisiblePanels;
            foreach (var place in layout)
                if (Enum.TryParse<WorkspacePanel>(place.Panel, out var panel) && visible.Contains(panel))
                    PopOut(panel, place);
        }
    }
}
