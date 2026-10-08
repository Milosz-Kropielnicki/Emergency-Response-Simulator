using System.Windows;
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
    /// Builds the host (configuration, DI, logging, the simulation engine) and shows the dashboard.
    /// </summary>
    public partial class App : Application
    {
        private IHost? _host;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Configuration.SetBasePath(AppContext.BaseDirectory);
            builder.Configuration.AddJsonFile("appsettings.json", optional: false);
            builder.Configuration.AddJsonFile(ErsConfiguration.LocalSettingsFileName, optional: true);
            // Command-line overrides (e.g. --Map:InitialView=terrain) win over the files above.
            builder.Configuration.AddCommandLine(e.Args);

            // PostgreSQL when configured; otherwise run entirely in memory so the shell always starts.
            var connectionString = builder.Configuration.GetConnectionString("Ers");
            var usingDatabase = !string.IsNullOrWhiteSpace(connectionString);
            if (usingDatabase)
                builder.Services.AddErsData(connectionString!);
            else
                builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();

            builder.Services.AddSimulation(builder.Configuration);
            builder.Services.AddSingleton<IScenario, DemoRosterScenario>();

            builder.Services.AddSingleton(new DataSourceInfo(usingDatabase ? "PostgreSQL" : "In-memory"));
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

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
            base.OnExit(e);
        }
    }
}
