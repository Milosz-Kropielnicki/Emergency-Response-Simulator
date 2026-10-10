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
