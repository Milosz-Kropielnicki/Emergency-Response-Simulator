using System.Windows;
using System.Windows.Input;
using Emergency_Response_Simulator.ViewModels;
using Mapsui;
using Mapsui.Projections;
using Mapsui.Tiling;
using Microsoft.Extensions.Configuration;

namespace Emergency_Response_Simulator
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel, IConfiguration configuration)
        {
            InitializeComponent();
            DataContext = _viewModel = viewModel;
            MapView.Map = CreateBaseMap(configuration.GetSection("Map"));
        }

        /// <summary>
        /// Placeholder base map: OpenStreetMap tiles centred on the configured area.
        /// Operational layers (units, incidents, zones) are added in Phases 1–3.
        /// </summary>
        private static Map CreateBaseMap(IConfiguration mapSettings)
        {
            var map = new Map();
            // OSM's tile policy requires an identifying user agent.
            map.Layers.Add(OpenStreetMap.CreateTileLayer("EmergencyResponseSimulator/0.1 (training simulator)"));

            var latitude = mapSettings.GetValue("CenterLatitude", 53.344);
            var longitude = mapSettings.GetValue("CenterLongitude", -6.26);
            var zoomLevel = mapSettings.GetValue("ZoomLevel", 14);

            var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
            map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), map.Navigator.Resolutions[zoomLevel]);
            return map;
        }

        private void TopHoverStrip_MouseEnter(object sender, MouseEventArgs e) => _viewModel.IsTopPanelOpen = true;

        private void TopPanel_MouseLeave(object sender, MouseEventArgs e) => _viewModel.IsTopPanelOpen = false;
    }
}
