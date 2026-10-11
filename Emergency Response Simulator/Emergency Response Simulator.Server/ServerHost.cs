using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.Simulation;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;

namespace Emergency_Response_Simulator.Server;

/// <summary>Builds the simulation server's web application (Program runs it; tests host it in memory).</summary>
public static class ServerHost
{
    /// <param name="args">Command-line settings; they win over appsettings.json and appsettings.Local.json.</param>
    /// <param name="configure">Extra setup before building, e.g. an in-memory test server.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile(ErsConfiguration.LocalSettingsFileName, optional: true);
        builder.Configuration.AddCommandLine(args);
        builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

        // Decision D4: a networked exercise keeps its record in PostgreSQL. In-memory only when explicitly allowed (dev, tests).
        var connectionString = builder.Configuration.GetConnectionString("Ers");
        var inMemory = string.IsNullOrWhiteSpace(connectionString);
        if (!inMemory)
            builder.Services.AddErsData(connectionString!);
        else if (builder.Configuration.GetValue<bool>($"{ServerOptions.SectionName}:{nameof(ServerOptions.AllowInMemoryStore)}"))
            builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
        else
            throw new InvalidOperationException(
                "The server needs PostgreSQL (ConnectionStrings:Ers). For development or tests, set Server:AllowInMemoryStore=true.");

        builder.Services.AddSimulation(builder.Configuration);
        builder.Services.AddSingleton<IScenario, DemoRosterScenario>();
        builder.Services.AddSingleton<EventFeed>();
        builder.Services.AddSingleton<CommandDispatcher>();
        builder.Services.AddHostedService<Broadcaster>();
        builder.Services.AddSignalR(hub => hub.MaximumReceiveMessageSize = 4 * 1024 * 1024)
            .AddJsonProtocol(json => WireJson.Configure(json.PayloadSerializerOptions));
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapHub<SimulationHub>(SimulationHubProtocol.Path);
        app.MapGet("/", (SimulationSession session, EventFeed feed) =>
            Results.Text($"Emergency Response Simulator server. Session {session.Id}; {feed.Subscribers} client(s) streaming."));
        app.Logger.LogInformation("Simulation server: event store {Store}; clients connect to {Path}",
            inMemory ? "in-memory (development)" : "PostgreSQL", SimulationHubProtocol.Path);
        return app;
    }
}
