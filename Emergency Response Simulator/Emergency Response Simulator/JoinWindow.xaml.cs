using System.Windows;
using System.Windows.Controls;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;

namespace Emergency_Response_Simulator
{
    /// <summary>
    /// Getting into a networked session (Phase 11 M2): join one with the instructor's code, a name and a role, or open one
    /// as the instructor on a scenario (with the server's PIN). Stays open with the server's reason if it says no.
    /// </summary>
    public partial class JoinWindow : Window
    {
        public JoinWindow(string? server, string? name, string? code, UserRole? role, string? error)
        {
            InitializeComponent();
            ServerBox.Text = server ?? "http://localhost:5180";
            NameBox.Text = name ?? Environment.UserName;
            CodeBox.Text = code ?? "";
            RoleBox.ItemsSource = Enum.GetValues<UserRole>().Select(r => new RoleChoice(r)).ToList();
            RoleBox.SelectedItem = ((IEnumerable<RoleChoice>)RoleBox.ItemsSource).First(r => r.Role == (role ?? UserRole.IncidentCommander));
            ScenarioBox.ItemsSource = ScenarioCatalog.All;
            ScenarioBox.SelectedIndex = 0;
            ErrorText.Text = error ?? "";
            Mode.SelectionChanged += (_, _) => GoButton.Content = Mode.SelectedIndex == 0 ? "Join" : "Open session";
        }

        /// <summary>The connection, joined to its session, once the dialog closes with OK.</summary>
        public SimulationConnection? Connection { get; private set; }

        private sealed record RoleChoice(UserRole Role)
        {
            public override string ToString() => Roles.Name(Role);
        }

        private void OnRoleChanged(object sender, SelectionChangedEventArgs e) =>
            JoinPinPanel.Visibility = RoleBox.SelectedItem is RoleChoice { Role: UserRole.Instructor } ? Visibility.Visible : Visibility.Collapsed;

        private void OnScenarioChanged(object sender, SelectionChangedEventArgs e) =>
            ScenarioText.Text = (ScenarioBox.SelectedItem as ScenarioInfo)?.Description ?? "";

        private async void OnGo(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var server))
            {
                ErrorText.Text = "Give the server's address, e.g. http://training-pc:5180";
                return;
            }

            GoButton.IsEnabled = false;
            var connection = new SimulationConnection(new ClientOptions(server));
            try
            {
                await connection.ConnectAsync();
                if (Mode.SelectedIndex == 0)
                {
                    var role = ((RoleChoice)RoleBox.SelectedItem).Role;
                    await connection.JoinSessionAsync(new JoinSessionRequest(CodeBox.Text.Trim(), NameBox.Text.Trim(), role,
                        role == UserRole.Instructor ? JoinPinBox.Password : null));
                }
                else
                {
                    var scenario = (ScenarioInfo)ScenarioBox.SelectedItem;
                    await connection.CreateSessionAsync(new CreateSessionRequest(scenario.Key, TitleBox.Text.Trim(), NameBox.Text.Trim(), CreatePinBox.Password));
                }
                Connection = connection;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync();
                ErrorText.Text = RemoteErrors.Message(ex);
            }
            finally
            {
                GoButton.IsEnabled = true;
            }
        }
    }
}
