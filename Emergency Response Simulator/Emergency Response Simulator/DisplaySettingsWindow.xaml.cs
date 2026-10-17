using System.Windows;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator
{
    /// <summary>Display and accessibility settings, the shortcuts and the workspace (Phase 21).</summary>
    public partial class DisplaySettingsWindow : Window
    {
        public DisplaySettingsWindow(DisplaySettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
