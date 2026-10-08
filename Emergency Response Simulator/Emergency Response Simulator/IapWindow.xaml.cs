using System.Windows;
using System.Windows.Input;
using Emergency_Response_Simulator.ViewModels.Iap;

namespace Emergency_Response_Simulator
{
    /// <summary>The IAP Builder, in its own window so it can sit beside the COP (or on a second screen).</summary>
    public partial class IapWindow : Window
    {
        private readonly IapBuilderViewModel _viewModel;

        public IapWindow(IapBuilderViewModel viewModel)
        {
            InitializeComponent();
            DataContext = _viewModel = viewModel;
            Loaded += (_, _) => viewModel.Opened();
            Closed += (_, _) => viewModel.Closed();
        }

        private void IssueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox { SelectedItem: IssueRow issue })
                _viewModel.ShowIssueCommand.Execute(issue);
        }
    }
}
