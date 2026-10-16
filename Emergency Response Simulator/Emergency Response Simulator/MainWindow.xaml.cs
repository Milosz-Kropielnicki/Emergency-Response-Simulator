using System.Windows;
using System.Windows.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Mapping;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly MapController _map;
        private IapWindow? _iapWindow;
        private AarWindow? _aarWindow;

        public MainWindow(MainViewModel viewModel, CopView cop, IServiceProvider services, IConfiguration configuration)
        {
            InitializeComponent();
            DataContext = _viewModel = viewModel;

            // The GIS service only exists when a database is configured.
            _map = new MapController(MapView, viewModel, cop, services.GetService<IGisService>(),
                services.GetRequiredService<AvlService>(), configuration.GetSection("Map"),
                services.GetRequiredService<RoutingService>(), services.GetRequiredService<TrafficFeed>(),
                services.GetRequiredService<LiveTraffic>());
            viewModel.IapRequested += (_, _) => ShowIapBuilder();
            viewModel.AarRequested += (_, _) => ShowAar(services);
            viewModel.FormsRequested += (_, _) => ShowForms(services);
            viewModel.ConsoleRequested += (_, _) => ShowConsole(services);
            // Push-to-talk (Phase 11 M7): held down, the radio transmits; anywhere in the window, even while typing.
            PreviewKeyDown += async (_, e) =>
            {
                if (e.Key != viewModel.Radio.PttKey || !viewModel.Radio.IsAvailable) return;
                e.Handled = true;
                if (!e.IsRepeat) await viewModel.Radio.PressAsync();
            };
            PreviewKeyUp += async (_, e) =>
            {
                if (e.Key != viewModel.Radio.PttKey || !viewModel.Radio.IsAvailable) return;
                e.Handled = true;
                await viewModel.Radio.ReleaseAsync();
            };
            Deactivated += async (_, _) => await viewModel.Radio.ReleaseAsync(); // no stuck transmission when focus leaves
            Closed += (_, _) => viewModel.Radio.Dispose();
            Loaded += async (_, _) =>
            {
                await viewModel.Timeline.LoadAsync();
                await _map.LoadStaticLayersAsync();
            };
        }

        /// <summary>One builder window; asking again brings it to the front.</summary>
        private void ShowIapBuilder()
        {
            if (_iapWindow is null)
            {
                _iapWindow = new IapWindow(_viewModel.Iap) { Owner = this };
                _iapWindow.Closed += (_, _) => _iapWindow = null;
                _iapWindow.Show();
            }
            else
            {
                if (_iapWindow.WindowState == WindowState.Minimized)
                    _iapWindow.WindowState = WindowState.Normal;
                _iapWindow.Activate();
            }
        }

        /// <summary>One review window; asking again brings it to the front (it keeps its report until refreshed).</summary>
        private void ShowAar(IServiceProvider services)
        {
            if (_aarWindow is null)
            {
                _aarWindow = new AarWindow(services.GetRequiredService<AarViewModel>()) { Owner = this };
                _aarWindow.Closed += (_, _) => _aarWindow = null;
                _aarWindow.Show();
            }
            else
            {
                if (_aarWindow.WindowState == WindowState.Minimized)
                    _aarWindow.WindowState = WindowState.Normal;
                _aarWindow.Activate();
            }
        }

        private FormsWindow? _formsWindow;

        /// <summary>One forms window; asking again brings it to the front and regenerates (Phase 18).</summary>
        private void ShowForms(IServiceProvider services)
        {
            if (_formsWindow is null)
            {
                _formsWindow = new FormsWindow(services.GetRequiredService<FormsViewModel>()) { Owner = this };
                _formsWindow.Closed += (_, _) => _formsWindow = null;
                _formsWindow.Show();
            }
            else
            {
                if (_formsWindow.WindowState == WindowState.Minimized)
                    _formsWindow.WindowState = WindowState.Normal;
                _formsWindow.Activate();
                _ = services.GetRequiredService<FormsViewModel>().OpenAsync();
            }
        }

        private InstructorConsoleWindow? _consoleWindow;

        /// <summary>The instructor console (Phase 11 M4), not owned by this window so it can sit on another screen.</summary>
        private void ShowConsole(IServiceProvider services)
        {
            if (_consoleWindow is null)
            {
                _consoleWindow = new InstructorConsoleWindow(services.GetRequiredService<InstructorConsoleViewModel>());
                _consoleWindow.Closed += (_, _) => _consoleWindow = null;
                Closed += (_, _) => _consoleWindow?.Close();
                _consoleWindow.Show();
            }
            else
            {
                if (_consoleWindow.WindowState == WindowState.Minimized)
                    _consoleWindow.WindowState = WindowState.Normal;
                _consoleWindow.Activate();
            }
        }

        // The on-screen push-to-talk button (Phase 11 M7): held, the radio transmits.
        private async void PttButton_Down(object sender, MouseButtonEventArgs e)
        {
            ((UIElement)sender).CaptureMouse();
            await _viewModel.Radio.PressAsync();
        }

        private async void PttButton_Up(object sender, MouseEventArgs e)
        {
            ((UIElement)sender).ReleaseMouseCapture();
            await _viewModel.Radio.ReleaseAsync();
        }

        private void TopHoverStrip_MouseEnter(object sender, MouseEventArgs e) => _viewModel.IsTopPanelOpen = true;

        private void TopPanel_MouseLeave(object sender, MouseEventArgs e) => _viewModel.IsTopPanelOpen = false;

        private void TimelineList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox { SelectedItem: TimelineRow row })
                _viewModel.Timeline.ReplayToEntryCommand.Execute(row);
        }

        // Push-to-talk: transmitting lasts as long as the button is held.
        private void PttButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel.CommsHub.PressTalk();
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private async void PttButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
            await _viewModel.CommsHub.ReleaseTalkAsync();
        }

        private async void PttButton_LostMouseCapture(object sender, MouseEventArgs e) =>
            await _viewModel.CommsHub.ReleaseTalkAsync();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _viewModel.ZoneDrawing.IsActive)
            {
                _viewModel.ZoneDrawing.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
