using System.Windows;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator
{
    /// <summary>The after-action review, in its own window so the COP can replay beside it.</summary>
    public partial class AarWindow : Window
    {
        public AarWindow(AarViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            // Build straight away the first time; afterwards the trainee refreshes when they want.
            Loaded += async (_, _) =>
            {
                if (!viewModel.HasReport) await viewModel.BuildCommand.ExecuteAsync(null);
            };
        }
    }
}
