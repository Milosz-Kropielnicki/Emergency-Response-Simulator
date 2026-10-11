using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 11 M1: the server/client split. A real server (in memory, Barrow Street with the world models) and real
/// SignalR clients over the test transport.
/// </summary>
public class ServerClientTests
{
    private const string Pin = "4321";

    /// <summary>The server in this process, with its own engine stepped by the test (the clock stays paused).</summary>
    private sealed class InProcessServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<SimulationConnection> _clients = [];

        private InProcessServer(WebApplication app) => _app = app;

        public static async Task<InProcessServer> StartAsync()
        {
            // Command-line settings win over appsettings.Local.json, so the developer's database is never used.
            var app = ServerHost.Build(
            [
                "--ConnectionStrings:Ers=", "--Server:AllowInMemoryStore=true", $"--Server:InstructorPin={Pin}",
                "--Server:SnapshotInterval=00:00:00.200", "--Server:ClockInterval=00:00:00.100",
                $"--Simulation:Scenario={BarrowStreetScenario.Key}", "--Simulation:AutoStart=false",
            ], builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            var server = new InProcessServer(app);
            // The scenario and roster are seeded by the hosted service: wait for them.
            await Until(() => server.Store.Count() > 50, TimeSpan.FromSeconds(20));
            return server;
        }

        public SimulationEngine Engine => _app.Services.GetRequiredService<SimulationEngine>();
        public ICopService Cop => _app.Services.GetRequiredService<ICopService>();
        public InMemoryStoreView Store => new(_app.Services.GetRequiredService<IEventStore>(), Engine.SessionId);

        public async Task<SimulationConnection> ConnectAsync(string? role = null, string? pin = null, bool feed = true)
        {
            var test = _app.GetTestServer();
            var connection = new SimulationConnection(new ClientOptions(test.BaseAddress, role, pin), http =>
            {
                http.HttpMessageHandlerFactory = _ => test.CreateHandler();
                http.WebSocketFactory = async (context, token) => await test.CreateWebSocketClient().ConnectAsync(context.Uri, token);
                http.Transports = HttpTransportType.WebSockets;
            });
            _clients.Add(connection);
            await connection.ConnectAsync();
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
    }

    /// <summary>Synchronous reads of the server's store for assertions.</summary>
    private sealed class InMemoryStoreView(IEventStore store, Guid session)
    {
        public List<SimEvent> All() => store.ReadAsync(session).ToBlockingEnumerable().ToList();
        public int Count() => All().Count;
    }

    private static async Task Until(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    private static PerceivedState CopOf(SimulationConnection client)
    {
        var cop = new PerceivedState();
        foreach (var simEvent in client.Mirror.ReadAsync(client.Session!.SessionId).ToBlockingEnumerable())
            cop.Apply(simEvent);
        return cop;
    }

    /// <summary>A fingerprint of a COP: what every view would show.</summary>
    private static string Fingerprint(ICopService cop) => JsonSerializer.Serialize(new
    {
        cop.LastSequence,
        Incidents = cop.Incidents.OrderBy(i => i.Id).Select(i => $"{i.Id}|{i.Number}|{i.Status}|{i.Priority}|{i.AssignedUnits.Count}"),
        Units = cop.Units.OrderBy(u => u.Id).Select(u => $"{u.Id}|{u.Status}|{u.AssignedIncidentId}|{u.Location}"),
        Reports = cop.Reports.Count,
        Alerts = cop.Alerts.Count,
        Comms = cop.CommsLog.Count,
        Zones = cop.Zones.Count,
        Orders = cop.Orders.Count,
    });

    [Fact]
    public async Task Two_clients_see_the_same_COP_as_the_server()
    {
        await using var server = await InProcessServer.StartAsync();
        var a = await server.ConnectAsync();
        var b = await server.ConnectAsync();
        var c2 = RemoteProxy.Create<IC2Service>(a, SimulationHubProtocol.Services.C2);

        // A trainee on client A opens an incident and dispatches; the scenario plays on for three minutes.
        var created = await c2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation, "Barrow Street");
        Assert.True(created.Succeeded, created.Error);
        var engine = CopOf(a).Units.First(u => u.Type == UnitType.Engine && u.Agency?.AiControlled != true);
        Assert.True((await c2.DispatchAsync(engine.Id, created.EntityId!.Value)).Succeeded);
        await server.StepAsync(TimeSpan.FromMinutes(3));
        await server.CaughtUpAsync(a);
        await server.CaughtUpAsync(b);

        var expected = Fingerprint(server.Cop);
        Assert.Equal(expected, Fingerprint(CopOf(a)));
        Assert.Equal(expected, Fingerprint(CopOf(b)));
        Assert.Contains(CopOf(b).Incidents, i => i.Id == created.EntityId);
    }

    [Fact]
    public async Task A_trainee_connection_never_receives_truth_and_the_instructor_receives_everything()
    {
        await using var server = await InProcessServer.StartAsync();
        var trainee = await server.ConnectAsync();
        var instructor = await server.ConnectAsync(SimulationHubProtocol.InstructorRole, Pin);
        Assert.False(trainee.Session!.Truth);
        Assert.True(instructor.Session!.Truth);

        await server.StepAsync(TimeSpan.FromMinutes(6)); // the fire starts, spreads, the wind shifts: plenty of truth
        await server.CaughtUpAsync(trainee);
        await server.CaughtUpAsync(instructor);

        var stored = server.Store.All();
        Assert.True(stored.Count(e => e.Visibility == EventVisibility.Truth) > 10);
        var heard = trainee.Mirror.ReadAsync(trainee.Session.SessionId).ToBlockingEnumerable().ToList();
        Assert.DoesNotContain(heard, e => e.Visibility == EventVisibility.Truth);
        Assert.Equal(stored.Where(e => e.Visibility == EventVisibility.Perceived).Select(e => e.Sequence), heard.Select(e => e.Sequence));
        Assert.Equal(stored.Select(e => e.Sequence), instructor.Mirror.ReadAsync(instructor.Session.SessionId).ToBlockingEnumerable().Select(e => e.Sequence));

        // Ground truth pushes go to the instructor only.
        await Until(() => instructor.Snapshot.At == server.Engine.Snapshot.At, TimeSpan.FromSeconds(10)); // the latest push, not an earlier one
        Assert.Contains(instructor.Snapshot.Hazards, h => h.Footprint is not null); // geometries survive the wire
        await Task.Delay(500);
        Assert.Same(WorldSnapshot.Empty, trainee.Snapshot);

        // Asking for it doesn't help: the timeline comes back without truth, and the review is the instructor's.
        var args = new[] { JsonSerializer.SerializeToElement(trainee.Session.SessionId, WireJson.Options), JsonSerializer.SerializeToElement(true, WireJson.Options) };
        var timeline = (await trainee.CallAsync(SimulationHubProtocol.Services.Aar, nameof(IAarService.GetTimelineAsync), args))!.Value
            .Deserialize<List<SimEvent>>(WireJson.Options)!;
        Assert.NotEmpty(timeline);
        Assert.All(timeline, e => Assert.Equal(EventVisibility.Perceived, e.Visibility));
        var refused = await Assert.ThrowsAsync<HubException>(() =>
            RemoteProxy.Create<IAarService>(trainee, SimulationHubProtocol.Services.Aar).BuildReportAsync(trainee.Session.SessionId));
        Assert.Contains("instructor", refused.Message);
        var report = await new ClientAarService(instructor).BuildReportAsync(instructor.Session.SessionId);
        Assert.NotEmpty(report.Metrics);
    }

    [Fact]
    public async Task A_wrong_instructor_PIN_is_refused()
    {
        await using var server = await InProcessServer.StartAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => server.ConnectAsync(SimulationHubProtocol.InstructorRole, "0000"));
    }

    [Fact]
    public async Task A_dropped_client_reconnects_and_resumes_with_no_gaps_or_duplicates()
    {
        await using var server = await InProcessServer.StartAsync();
        var client = await server.ConnectAsync();
        await server.StepAsync(TimeSpan.FromMinutes(1));
        await server.CaughtUpAsync(client);
        var before = client.Mirror.LastSequence;

        // The link drops; the world carries on without it.
        await client.DropAsync();
        await server.StepAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(before, client.Mirror.LastSequence);

        await Until(() => client.Connected && client.Reconnects > 0, TimeSpan.FromSeconds(15));
        await server.CaughtUpAsync(client);
        var perceived = server.Store.All().Where(e => e.Visibility == EventVisibility.Perceived).Select(e => e.Sequence).ToList();
        var mirrored = client.Mirror.ReadAsync(client.Session!.SessionId).ToBlockingEnumerable().Select(e => e.Sequence).ToList();
        Assert.Equal(perceived, mirrored);

        // And it matches a client that joined fresh (a restarted app).
        var fresh = await server.ConnectAsync();
        await server.CaughtUpAsync(fresh);
        Assert.Equal(Fingerprint(CopOf(fresh)), Fingerprint(CopOf(client)));
        Assert.Equal(Fingerprint(server.Cop), Fingerprint(CopOf(client)));
    }

    [Fact]
    public async Task A_command_returns_once_the_clients_own_COP_shows_its_result()
    {
        await using var server = await InProcessServer.StartAsync();
        var client = await server.ConnectAsync();
        var cop = new PerceivedState();
        cop.Follow(client.Mirror, client.Session!.SessionId);
        await server.CaughtUpAsync(client);
        foreach (var simEvent in client.Mirror.ReadAsync(client.Session.SessionId).ToBlockingEnumerable()) cop.Apply(simEvent);

        var c2 = RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);
        var result = await c2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.Medium, BarrowStreetScenario.ChemicalStore);
        Assert.NotNull(cop.FindIncident(result.EntityId!.Value)); // no wait needed by the caller

        var refused = await c2.DispatchAsync(Guid.NewGuid(), result.EntityId.Value);
        Assert.False(refused.Succeeded); // validated on the server against its COP, as standalone
        Assert.Equal("Unknown unit.", refused.Error);
    }

    [Fact]
    public async Task Every_contract_method_is_reachable_remotely_one_for_one()
    {
        // Every asynchronous method of the contracts is in the dispatch table, with no two sharing a name and arity.
        foreach (var (service, contract) in CommandDispatcher.Contracts)
        {
            var remotable = contract.GetMethods().Where(CommandDispatcher.IsRemotable).ToList();
            var callable = CommandDispatcher.Callable.Where(c => c.Service == service).Select(c => c.Method).ToList();
            Assert.Equal(remotable.Count, callable.Count);
            Assert.All(remotable, m => Assert.Contains(m, callable));
        }
        Assert.Equal(44, typeof(IC2Service).GetMethods().Length);
        Assert.Contains(CommandDispatcher.Callable, c => c.Method.Name == nameof(IIapService.PullFromCopAsync));
        Assert.DoesNotContain(CommandDispatcher.Callable, c => c.Method.Name == nameof(IAarService.ReplayToAsync)); // runs on the client

        await using var server = await InProcessServer.StartAsync();
        var client = await server.ConnectAsync();
        var c2 = RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);
        var iap = RemoteProxy.Create<IIapService>(client, SimulationHubProtocol.Services.Iap);
        var incident = (await c2.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        DateTimeOffset clockNow() => server.Engine.SimTime;

        // A method returning a document, one with a complex argument, and the clock.
        var period = await iap.StartOperationalPeriodAsync(incident, clockNow(), clockNow().AddHours(2), "Contain the fire");
        Assert.True(period.Succeeded, period.Error);
        var draft = await iap.CreateDraftAsync(period.EntityId!.Value, "Planning");
        Assert.True(draft.Succeeded, draft.Error);
        var content = await iap.PullFromCopAsync(incident, new IapContent(), IapPullParts.All);
        Assert.NotNull(content);
        content.CommandersIntent = "Contain the fire and protect the chemical store.";
        Assert.True((await iap.SaveDraftAsync(draft.EntityId!.Value, content)).Succeeded);
        Assert.Equal("Contain the fire and protect the chemical store.",
            CopOf(client).ActionPlans.Single().Content.CommandersIntent);

        var clock = new RemoteSimulationControl(client);
        await clock.SetTimeScaleAsync(10);
        await Until(() => clock.TimeScale == 10, TimeSpan.FromSeconds(5));
        Assert.Equal(server.Engine.SessionId, clock.SessionId);
    }

    [Fact]
    public void World_snapshots_and_reports_round_trip_in_the_wire_format()
    {
        var area = Wgs84.CreatePolygon(Wgs84.Circle(BarrowStreetScenario.FireLocation, 50));
        var snapshot = WorldSnapshot.Empty with
        {
            Hazards = [new WorldSnapshot.HazardView(Guid.NewGuid(), HazardKind.Plume, "Chlorine", BarrowStreetScenario.FireLocation, "chlorine",
                1.5, 7850, 1.5, area, [(HazardLevel.High, area)], TestHarness.Start)],
            Comms = new WorldSnapshot.CommsView([(BarrowStreetScenario.RailwayBridge, 45, "bridge")], [], [], [("Engine 7", "radio failed")], 2),
        };

        var copy = JsonSerializer.Deserialize<WorldSnapshot>(JsonSerializer.Serialize(snapshot, WireJson.Options), WireJson.Options)!;

        var hazard = Assert.Single(copy.Hazards);
        Assert.True(hazard.Footprint!.EqualsExact(area, 1e-9));
        Assert.Equal(HazardLevel.High, Assert.Single(hazard.Zones).Level);
        Assert.Equal(Wgs84.Srid, hazard.Footprint.SRID);
        Assert.Equal(("Engine 7", "radio failed"), Assert.Single(copy.Comms.Unreachable));
        Assert.Equal(2, copy.Comms.CallsWaiting);
    }
}
