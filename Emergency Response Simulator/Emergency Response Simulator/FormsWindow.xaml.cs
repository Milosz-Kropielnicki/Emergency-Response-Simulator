using System.Windows;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator
{
    /// <summary>ICS forms, the IAP package, situation reports and the decision log (Phase 18), in their own window.</summary>
    public partial class FormsWindow : Window
    {
        public FormsWindow(FormsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += async (_, _) => await viewModel.OpenAsync();
        }
    }
}
