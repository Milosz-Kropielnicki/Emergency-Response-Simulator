using System.Windows;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator
{
    /// <summary>The scenario editor and live training controls (Phase 19), for the instructor.</summary>
    public partial class ScenarioEditorWindow : Window
    {
        public ScenarioEditorWindow(ScenarioEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += async (_, _) => await viewModel.OpenAsync();
        }
    }
}
