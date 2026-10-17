using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Server.Api;
using Emergency_Response_Simulator.Server.Field;
using Emergency_Response_Simulator.Server.Voice;
using Microsoft.Extensions.Options;

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
        // Every session shares the store (events are keyed by session) and the GIS database.
        var connectionString = builder.Configuration.GetConnectionString("Ers");
        var inMemory = string.IsNullOrWhiteSpace(connectionString);
        if (!inMemory)
            builder.Services.AddErsData(connectionString!);
        else if (builder.Configuration.GetValue<bool>($"{ServerOptions.SectionName}:{nameof(ServerOptions.AllowInMemoryStore)}"))
            builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
        else
            throw new InvalidOperationException(
                "The server needs PostgreSQL (ConnectionStrings:Ers). For development or tests, set Server:AllowInMemoryStore=true.");

        // Each session builds its own engine and services when an instructor opens it (SessionRuntime).
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<CommandDispatcher>();
        builder.Services.AddHostedService<Broadcaster>();
        builder.Services.AddSingleton<ApiKeys>(); // the REST API's callers (Phase 20)
        // Enums by name in request bodies, as everywhere else on the wire (the field companion's statuses, Phase 21).
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // Voice (Phase 11 M7): recordings on disk with a retention period; Whisper transcription when its model is there.
        builder.Services.Configure<VoiceOptions>(builder.Configuration.GetSection(VoiceOptions.SectionName));
        builder.Services.AddSingleton<IClipStore, FileClipStore>();
        builder.Services.AddSingleton<IVoiceTransport, SignalRVoiceTransport>();
        builder.Services.AddSingleton<ISpeechRecognizer>(sp => File.Exists(sp.GetRequiredService<IOptions<VoiceOptions>>().Value.WhisperModel)
            ? new WhisperRecognizer(sp.GetRequiredService<IOptions<VoiceOptions>>().Value.WhisperModel, sp.GetRequiredService<ILogger<WhisperRecognizer>>())
            : new NoSpeechRecognizer());
        builder.Services.AddHostedService<VoiceRetention>();
        builder.Services.AddSignalR(hub => hub.MaximumReceiveMessageSize = 4 * 1024 * 1024)
            .AddJsonProtocol(json => WireJson.Configure(json.PayloadSerializerOptions));
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapHub<SimulationHub>(SimulationHubProtocol.Path);
        app.MapErsApi();
        app.MapFieldApi();
        app.MapGet("/", (SessionManager sessions) => Results.Text(
            "Emergency Response Simulator server.\n" + string.Join('\n', sessions.All.Select(s =>
                $"{s.Title} [{s.State}] code {s.Code}: {s.Participants.Count(p => p.Present)} participant(s)"))));
        app.Logger.LogInformation("Simulation server: event store {Store}; clients connect to {Path}; REST API at {Api} ({Keys} key(s))",
            inMemory ? "in-memory (development)" : "PostgreSQL", SimulationHubProtocol.Path, ApiEndpoints.Base,
            app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.ApiKeys.Count);
        return app;
    }
}
