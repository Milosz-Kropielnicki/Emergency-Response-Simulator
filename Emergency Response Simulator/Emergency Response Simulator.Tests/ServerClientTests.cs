using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.SignalR;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 11 M1: the server/client split. A real server (in memory, Barrow Street with the world models) and real
/// SignalR clients over the test transport.
/// </summary>
public class ServerClientTests
{
    [Fact]
    public async Task Two_clients_see_the_same_COP_as_the_server()
    {
        await using var server = await StartAsync();
        var a = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var b = await server.JoinAsync("Bea", UserRole.Operations);
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
        await using var server = await StartAsync();
        var trainee = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var instructor = server.Instructor;
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
        await using var server = await StartAsync();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => server.JoinAsync("Mallory", UserRole.Instructor, pin: "0000"));
        Assert.Contains("PIN", refused.Message);
    }

    [Fact]
    public async Task A_dropped_client_reconnects_and_resumes_with_no_gaps_or_duplicates()
    {
        await using var server = await StartAsync();
        var client = await server.JoinAsync("Alex", UserRole.IncidentCommander);
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
        var fresh = await server.JoinAsync("Bea", UserRole.Dispatcher);
        await server.CaughtUpAsync(fresh);
        await server.CaughtUpAsync(client);
        Assert.Equal(Fingerprint(CopOf(fresh)), Fingerprint(CopOf(client)));
        Assert.Equal(Fingerprint(server.Cop), Fingerprint(CopOf(client)));
    }

    [Fact]
    public async Task A_command_returns_once_the_clients_own_COP_shows_its_result()
    {
        await using var server = await StartAsync();
        var client = await server.JoinAsync("Alex", UserRole.IncidentCommander);
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
        // 47th: PredictPlumeAsync (Phase 13); 48–53: public, media & legal (Phase 14); 54–59: logistics & finance (Phase 15)
        // 60–66: medical, mass casualty & public health (Phase 16); 67–78: evacuation, shelter & infrastructure (Phase 17)
        Assert.Equal(78, typeof(IC2Service).GetMethods().Length);
        Assert.Contains(CommandDispatcher.Callable, c => c.Method.Name == nameof(IIapService.PullFromCopAsync));
        Assert.DoesNotContain(CommandDispatcher.Callable, c => c.Method.Name == nameof(IAarService.ReplayToAsync)); // runs on the client

        await using var server = await StartAsync();
        var client = await server.JoinAsync("Pat", UserRole.Planning);
        var c2 = RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);
        var iap = RemoteProxy.Create<IIapService>(client, SimulationHubProtocol.Services.Iap);
        var opened = RemoteProxy.Create<IC2Service>(server.Instructor, SimulationHubProtocol.Services.C2); // opening incidents isn't Planning's
        var incident = (await opened.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
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

        var clock = new RemoteSimulationControl(server.Instructor); // the clock is the instructor's
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
