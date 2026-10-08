using System.Windows;
using System.Windows.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Mapping;
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

        public MainWindow(MainViewModel viewModel, CopView cop, IServiceProvider services, IConfiguration configuration)
        {
            InitializeComponent();
            DataContext = _viewModel = viewModel;

            // The GIS service only exists when a database is configured.
            _map = new MapController(MapView, viewModel, cop, services.GetService<IGisService>(), configuration.GetSection("Map"));
            Loaded += async (_, _) =>
            {
                await viewModel.Timeline.LoadAsync();
                await _map.LoadStaticLayersAsync();
            };
        }

        private void TopHoverStrip_MouseEnter(object sender, MouseEventArgs e) => _viewModel.IsTopPanelOpen = true;

        private void TopPanel_MouseLeave(object sender, MouseEventArgs e) => _viewModel.IsTopPanelOpen = false;

        private void TimelineList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox { SelectedItem: TimelineRow row })
                _viewModel.Timeline.ReplayToEntryCommand.Execute(row);
        }

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
