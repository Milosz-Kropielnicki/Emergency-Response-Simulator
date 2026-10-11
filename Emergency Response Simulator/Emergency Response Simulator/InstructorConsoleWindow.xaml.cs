using System.Windows;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator
{
    /// <summary>
    /// The instructor console (Phase 11 M4) in its own window, for a second screen beside the COP; or, with
    /// <c>--Console</c>, the whole client.
    /// </summary>
    public partial class InstructorConsoleWindow : Window
    {
        public InstructorConsoleWindow(InstructorConsoleViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += async (_, _) => await viewModel.RefreshAsync();
        }
    }
}
