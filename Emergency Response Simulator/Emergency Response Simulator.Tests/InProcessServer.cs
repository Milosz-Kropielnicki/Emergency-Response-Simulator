using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// A real simulation server in this process (in memory, over the SignalR test transport) with one session opened by an
/// instructor. Unless asked not to, the instructor starts the exercise and pauses the clock, so tests step the engine
/// themselves and get the same world every time.
/// </summary>
internal sealed class InProcessServer : IAsyncDisposable
{
    public const string Pin = "4321";

    private readonly WebApplication _app;
    private readonly List<SimulationConnection> _clients = [];

    private InProcessServer(WebApplication app) => _app = app;

    public SimulationConnection Instructor { get; private set; } = null!;
    public SessionRuntime Session { get; private set; } = null!;
    public SessionManager Sessions => _app.Services.GetRequiredService<SessionManager>();
    public CommandDispatcher Dispatcher => _app.Services.GetRequiredService<CommandDispatcher>();
    public SimulationEngine Engine => Session.Engine;
    public ICopService Cop => Session.Services.GetRequiredService<ICopService>();
    public StoreView Store => new(_app.Services.GetRequiredService<IEventStore>(), Session.Id);

    public static async Task<InProcessServer> StartAsync(string scenario = BarrowStreetScenario.Key, bool start = true, params string[] settings)
    {
        // Command-line settings win over appsettings.Local.json, so the developer's database is never used.
        var app = ServerHost.Build(
        [
            "--ConnectionStrings:Ers=", "--Server:AllowInMemoryStore=true", $"--Server:InstructorPin={Pin}",
            "--Server:SnapshotInterval=00:00:00.200", "--Server:ClockInterval=00:00:00.100", .. settings,
        ], builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        var server = new InProcessServer(app);

        server.Instructor = server.NewConnection();
        await server.Instructor.ConnectAsync();
        var ticket = await server.Instructor.CreateSessionAsync(new CreateSessionRequest(scenario, "Test exercise", "Ines (instructor)", Pin));
        server.Session = server.Sessions.Find(ticket.SessionId)!;
        await server.Instructor.StartFeedAsync();

        // The roster and scenario are seeded by the session's hosted service: wait for them.
        await Until(() => server.Store.Count() > 50, TimeSpan.FromSeconds(20));
        if (start)
        {
            await server.Instructor.StartSessionAsync();
            await server.Engine.PauseAsync();
        }
        return server;
    }

    /// <summary>A client connection to this server, not yet connected or joined.</summary>
    public SimulationConnection NewConnection()
    {
        var test = _app.GetTestServer();
        var connection = new SimulationConnection(new ClientOptions(test.BaseAddress), http =>
        {
            http.HttpMessageHandlerFactory = _ => test.CreateHandler();
            http.WebSocketFactory = async (context, token) => await test.CreateWebSocketClient().ConnectAsync(context.Uri, token);
            http.Transports = HttpTransportType.WebSockets;
        });
        _clients.Add(connection);
        return connection;
    }

    /// <summary>Someone joins the session with its code.</summary>
    public async Task<SimulationConnection> JoinAsync(string name, UserRole role, bool feed = true, string? pin = null)
    {
        var connection = NewConnection();
        await connection.ConnectAsync();
        await connection.JoinSessionAsync(new JoinSessionRequest(Session.Code, name, role, pin));
        if (feed) await connection.StartFeedAsync();
        return connection;
    }

    public async Task StepAsync(TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(5))
            await Engine.StepAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Waits until a client has every event it may see that the server has stored.</summary>
    public Task CaughtUpAsync(SimulationConnection client)
    {
        var truth = client.Session!.Truth;
        return Until(() => client.Mirror.LastSequence >= Store.All().Where(e => truth || e.Visibility == EventVisibility.Perceived)
            .Select(e => e.Sequence).DefaultIfEmpty(0).Max(), TimeSpan.FromSeconds(20));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ---- Helpers for assertions ----

    /// <summary>Synchronous reads of the server's store.</summary>
    internal sealed class StoreView(IEventStore store, Guid session)
    {
        public List<SimEvent> All() => store.ReadAsync(session).ToBlockingEnumerable().ToList();
        public int Count() => All().Count;
    }

    public static async Task Until(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    /// <summary>A client's COP, rebuilt from its mirror.</summary>
    public static PerceivedState CopOf(SimulationConnection client)
    {
        var cop = new PerceivedState();
        foreach (var simEvent in client.Mirror.ReadAsync(client.Session!.SessionId).ToBlockingEnumerable())
            cop.Apply(simEvent);
        return cop;
    }

    /// <summary>A fingerprint of a COP: what every view would show.</summary>
    public static string Fingerprint(ICopService cop) => JsonSerializer.Serialize(new
    {
        cop.LastSequence,
        Incidents = cop.Incidents.OrderBy(i => i.Id).Select(i => $"{i.Id}|{i.Number}|{i.Status}|{i.Priority}|{i.AssignedUnits.Count}"),
        Units = cop.Units.OrderBy(u => u.Id).Select(u => $"{u.Id}|{u.Status}|{u.AssignedIncidentId}|{u.Location}"),
        Reports = cop.Reports.Count,
        Alerts = cop.Alerts.Count,
        Comms = cop.CommsLog.Count,
        Zones = cop.Zones.Count,
        Orders = cop.Orders.Count,
        Participants = cop.Participants.Select(p => $"{p.Id}|{p.Role}|{p.Present}"),
    });
}
