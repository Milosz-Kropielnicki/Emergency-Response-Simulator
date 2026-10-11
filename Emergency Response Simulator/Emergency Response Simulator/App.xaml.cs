using System.Windows;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.Simulation;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Emergency_Response_Simulator
{
    /// <summary>
    /// Builds the host (configuration, DI, logging) and shows the dashboard. Two ways to run (Phase 11, decision D2):
    /// <list type="bullet">
    /// <item><c>--Mode=Standalone</c> (the default): the engine, event store and COP run in this process, as before.</item>
    /// <item><c>--Mode=Client --Server=http://host:5180 [--Role=Instructor --Pin=…]</c>: the engine runs on the
    /// simulation server; this process mirrors its event stream, projects the COP locally and sends commands to it.</item>
    /// </list>
    /// The view models see the same contracts either way.
    /// </summary>
    public partial class App : Application
    {
        private IHost? _host;
        private SimulationConnection? _connection;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Configuration.SetBasePath(AppContext.BaseDirectory);
            builder.Configuration.AddJsonFile("appsettings.json", optional: false);
            builder.Configuration.AddJsonFile(ErsConfiguration.LocalSettingsFileName, optional: true);
            // Command-line overrides (e.g. --Map:InitialView=terrain) win over the files above.
            builder.Configuration.AddCommandLine(e.Args);

            var connectionString = builder.Configuration.GetConnectionString("Ers");
            var usingDatabase = !string.IsNullOrWhiteSpace(connectionString);
            var client = string.Equals(builder.Configuration["Mode"], "Client", StringComparison.OrdinalIgnoreCase);

            if (client)
            {
                if (!await ConnectAsync(builder.Configuration)) return;
                builder.Services.AddSimulationClient(_connection!);
                // The map's static layers and the road network still come from a GIS database, if this machine has one.
                if (usingDatabase) builder.Services.AddErsGis(connectionString!);
                var session = _connection!.Session!;
                var ticket = _connection.Ticket!;
                builder.Services.AddSingleton(new DataSourceInfo($"Server {_connection.Options.Server.Authority} · code {session.Code} · {ticket.DisplayName}, {Roles.Name(ticket.Role)}"));
            }
            else
            {
                // PostgreSQL when configured; otherwise run entirely in memory so the shell always starts.
                if (usingDatabase)
                    builder.Services.AddErsData(connectionString!);
                else
                    builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();

                builder.Services.AddSimulation(builder.Configuration);
                builder.Services.AddSingleton<IScenario, DemoRosterScenario>();
                builder.Services.AddSingleton(new DataSourceInfo(usingDatabase ? "PostgreSQL" : "In-memory"));
            }

            builder.Services.AddSingleton(sp => new Permissions(sp.GetRequiredService<CopView>(), sp.GetService<SimulationConnection>()));
            builder.Services.AddSingleton(sp => new SessionViewModel(sp.GetRequiredService<CopView>(), sp.GetService<SimulationConnection>(),
                sp.GetRequiredService<Permissions>()));
            builder.Services.AddSingleton<TimelineViewModel>();
            builder.Services.AddSingleton<InstructorViewModel>();
            builder.Services.AddSingleton<AarViewModel>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton(sp => new InstructorConsoleViewModel(sp.GetRequiredService<IInstructorService>(), sp.GetRequiredService<CopView>(),
                sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<SimulationSession>(), sp.GetRequiredService<ISimulationControl>(),
                sp.GetService<SimulationConnection>()));
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();

            // --Console: a separate instructor client (Phase 11 M4), the console on its own (the COP stays on another machine or screen).
            Window window = builder.Configuration.GetValue<bool>("Console") && _host.Services.GetRequiredService<InstructorConsoleViewModel>().IsAvailable
                ? new InstructorConsoleWindow(_host.Services.GetRequiredService<InstructorConsoleViewModel>())
                : _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            try
            {
                await _host.StartAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, $"The simulation failed to start:\n\n{ex.Message}",
                    "Emergency Response Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Gets into a session on the simulation server (Phase 11 M2). With everything on the command line it joins
        /// (<c>--Code --Name --Role</c>, and <c>--Pin</c> for an instructor) or opens one (<c>--Create=scenario --Name --Pin
        /// [--Title]</c>) straight away; otherwise, or if the server says no, the join dialog asks. Cancelling quits.
        /// </summary>
        private async Task<bool> ConnectAsync(IConfiguration configuration)
        {
            var address = configuration["Server"];
            var name = configuration["Name"];
            var code = configuration["Code"];
            var create = configuration["Create"];
            UserRole? role = Enum.TryParse<UserRole>(configuration["Role"], ignoreCase: true, out var parsed) ? parsed : null;
            string? error = null;

            if (Uri.TryCreate(address, UriKind.Absolute, out var server) && !string.IsNullOrWhiteSpace(name)
                && (!string.IsNullOrWhiteSpace(code) && role is not null || !string.IsNullOrWhiteSpace(create)))
            {
                var connection = new SimulationConnection(new ClientOptions(server));
                try
                {
                    await connection.ConnectAsync();
                    if (!string.IsNullOrWhiteSpace(create))
                        await connection.CreateSessionAsync(new CreateSessionRequest(create, configuration["Title"] ?? "", name, configuration["Pin"] ?? ""));
                    else
                        await connection.JoinSessionAsync(new JoinSessionRequest(code!, name, role!.Value, configuration["Pin"]));
                    _connection = connection;
                    return true;
                }
                catch (Exception ex)
                {
                    await connection.DisposeAsync();
                    error = RemoteErrors.Message(ex);
                }
            }

            // The dialog is the only window for now: don't let closing it end the app before the main window opens.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dialog = new JoinWindow(address, name, code, role, error);
            var joined = dialog.ShowDialog() == true;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (!joined || dialog.Connection is null)
            {
                Shutdown(0);
                return false;
            }
            _connection = dialog.Connection;
            return true;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
            if (_connection is not null)
                await _connection.DisposeAsync();
            base.OnExit(e);
        }
    }
}
