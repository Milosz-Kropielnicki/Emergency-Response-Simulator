using System.Windows;
using Emergency_Response_Simulator.ViewModels;

namespace Emergency_Response_Simulator
{
    /// <summary>Data standards (CAP, EDXL, NIEM, HL7) and the NIMS/ICS check (Phase 20).</summary>
    public partial class DataExchangeWindow : Window
    {
        public DataExchangeWindow(DataExchangeViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += async (_, _) => await viewModel.OpenAsync();
        }
    }
}
