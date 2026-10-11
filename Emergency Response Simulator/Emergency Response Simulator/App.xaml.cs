using System.Windows;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.Simulation;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Scenarios;
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
                builder.Services.AddSingleton(new DataSourceInfo($"Server {_connection.Options.Server.Authority}" + (session.Truth ? " (instructor)" : "")));
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

            builder.Services.AddSingleton<TimelineViewModel>();
            builder.Services.AddSingleton<InstructorViewModel>();
            builder.Services.AddSingleton<AarViewModel>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();

            var window = _host.Services.GetRequiredService<MainWindow>();
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

        /// <summary>Connects to the simulation server; on failure says why and shuts down.</summary>
        private async Task<bool> ConnectAsync(IConfiguration configuration)
        {
            var address = configuration["Server"];
            if (!Uri.TryCreate(address, UriKind.Absolute, out var server))
            {
                MessageBox.Show("Client mode needs the server's address, e.g. --Server=http://training-pc:5180",
                    "Emergency Response Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return false;
            }

            _connection = new SimulationConnection(new ClientOptions(server, configuration["Role"], configuration["Pin"]));
            try
            {
                await _connection.ConnectAsync();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not join the simulation at {server}:\n\n{ex.Message}",
                    "Emergency Response Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return false;
            }
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
