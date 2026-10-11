using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.AspNetCore.SignalR;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 11 M2: sessions, identity, attribution and concurrency, over a real in-process server.</summary>
public class SessionTests
{
    private static IC2Service C2Of(SimulationConnection client) => RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);

    [Fact]
    public async Task A_session_goes_from_lobby_to_running_to_ended_and_only_the_instructor_moves_it_on()
    {
        await using var server = await StartAsync(start: false);
        Assert.Equal(6, server.Session.Code.Length);
        var trainee = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        Assert.Equal(SessionState.Lobby, trainee.Session!.State);

        // In the lobby the trainee can look but not act, and can't start the exercise.
        var early = await C2Of(trainee).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation);
        Assert.False(early.Succeeded);
        Assert.Contains("hasn't started", early.Error);
        Assert.Contains("instructor", (await Assert.ThrowsAsync<HubException>(() => trainee.StartSessionAsync())).Message);
        await Assert.ThrowsAnyAsync<Exception>(() => new RemoteSimulationControl(trainee).StartAsync());

        await server.Instructor.StartSessionAsync();
        await Until(() => trainee.Session.State == SessionState.Running, TimeSpan.FromSeconds(5)); // pushed to everyone
        Assert.True(server.Engine.IsRunning);
        Assert.True((await C2Of(trainee).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).Succeeded);

        await server.Instructor.EndSessionAsync("Exercise complete");
        await Until(() => trainee.Session.State == SessionState.Ended, TimeSpan.FromSeconds(5));
        Assert.False(server.Engine.IsRunning);
        var late = await C2Of(trainee).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.Low, BarrowStreetScenario.FireLocation);
        Assert.Contains("ended", late.Error);
        await Assert.ThrowsAnyAsync<Exception>(() => server.JoinAsync("Late Lou", UserRole.Dispatcher));

        var events = server.Store.All().Select(e => e.Payload).ToList();
        Assert.Contains(events, p => p is SessionOpened { Scenario: BarrowStreetScenario.Key });
        Assert.Equal(2, events.OfType<ParticipantJoined>().Count());
        Assert.Contains(events, p => p is SessionEnded { Reason: "Exercise complete" });
    }

    [Fact]
    public async Task Every_command_event_carries_its_actor_stamped_by_the_server()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        var icId = ic.Ticket!.ParticipantId;
        var opsId = ops.Ticket!.ParticipantId;

        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        await C2Of(ic).AssignIncidentCommanderAsync(incident, "Alex");
        await server.CaughtUpAsync(ops);
        var cop = CopOf(ops);
        var engine = cop.Units.First(u => u.Type == UnitType.Engine && u.Agency?.AiControlled != true);
        await C2Of(ops).DispatchAsync(engine.Id, incident);
        await C2Of(ops).IssueOrderAsync(incident, OrderTargetKind.Unit, engine.Id, "Interior attack, ground floor");
        await C2Of(ic).DeclareZoneAsync(ZoneType.PoliceCordon, "Cordon", Wgs84Square(), incident);
        var iap = RemoteProxy.Create<IIapService>(ops, SimulationHubProtocol.Services.Iap);
        await iap.StartOperationalPeriodAsync(incident, server.Engine.SimTime, server.Engine.SimTime.AddHours(2));
        await server.StepAsync(TimeSpan.FromMinutes(1)); // the world answers: those events are nobody's command

        var stored = server.Store.All();
        var commands = stored.Where(e => e.Source is EventSources.C2 or EventSources.Iap).ToList();
        Assert.True(commands.Count >= 6);
        Assert.All(commands, e => Assert.NotNull(e.ActorId));
        Assert.Equal(icId, commands.Single(e => e.Payload is IncidentCreated).ActorId);
        Assert.Equal(UserRole.IncidentCommander, commands.Single(e => e.Payload is IncidentCreated).ActorRole);
        Assert.Equal(opsId, commands.Single(e => e.Payload is UnitDispatched).ActorId);
        Assert.Equal(UserRole.Operations, commands.Single(e => e.Payload is OrderIssued).ActorRole);
        Assert.Equal(opsId, commands.Single(e => e.Payload is OperationalPeriodStarted).ActorId);
        Assert.Equal(icId, commands.Single(e => e.Payload is ZoneDeclared).ActorId);
        // What the world does in reply is nobody's command (starting the clock was the instructor's, and says so).
        Assert.All(stored.Where(e => e.Source == EventSources.Engine && e.Payload is not (SimulationStarted or SimulationPaused or TimeScaleChanged)
                                     || e.Visibility == EventVisibility.Truth), e => Assert.Null(e.ActorId));
        Assert.Equal(server.Instructor.Ticket!.ParticipantId, stored.First(e => e.Payload is SimulationStarted).ActorId);

        // The clients see the same stamps (they travel with the event).
        await server.CaughtUpAsync(ops);
        var mirrored = ops.Mirror.ReadAsync(ops.Session!.SessionId).ToBlockingEnumerable().Single(e => e.Payload is IncidentCreated);
        Assert.Equal(icId, mirrored.ActorId);
    }

    private static IReadOnlyList<Core.Geo.GeoPoint> Wgs84Square() =>
        [.. new[] { 45.0, 135, 225, 315 }.Select(b => Core.Geo.GeoMath.Destination(BarrowStreetScenario.FireLocation, b, 200))];

    [Fact]
    public async Task Two_players_dispatching_the_same_unit_at_once_exactly_one_succeeds()
    {
        await using var server = await StartAsync();
        var a = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var b = await server.JoinAsync("Bea", UserRole.Operations);
        var incident = (await C2Of(a).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        var units = CopOf(a).Units.Where(u => u.Agency?.AiControlled != true && u.Status == UnitStatus.Available).Take(6).ToList();

        foreach (var unit in units)
        {
            var results = await Task.WhenAll(C2Of(a).DispatchAsync(unit.Id, incident), C2Of(b).DispatchAsync(unit.Id, incident));
            Assert.Single(results, r => r.Succeeded);
            Assert.Contains("not available", results.Single(r => !r.Succeeded).Error);
        }
        Assert.Equal(units.Count, server.Store.All().Count(e => e.Payload is UnitDispatched));
    }

    [Fact]
    public async Task Exclusive_roles_are_held_by_one_person_and_can_be_assigned_or_handed_over()
    {
        await using var server = await StartAsync();
        var a = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var b = await server.JoinAsync("Bea", UserRole.Operations);
        var c = await server.JoinAsync("Cal", UserRole.Dispatcher);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => server.JoinAsync("Dee", UserRole.IncidentCommander));
        Assert.Contains("held by Alex", refused.Message);
        await server.JoinAsync("Eve", UserRole.Dispatcher); // dispatchers can be several

        // Alex hands command to Bea and takes Operations; the instructor makes Cal the Safety Officer.
        await a.HandOverRoleAsync(b.Ticket!.ParticipantId, UserRole.Operations);
        await server.Instructor.AssignRoleAsync(c.Ticket!.ParticipantId, UserRole.Safety);
        await Assert.ThrowsAsync<HubException>(() => c.AssignRoleAsync(a.Ticket!.ParticipantId, UserRole.Logistics));
        await Assert.ThrowsAsync<HubException>(() => server.Instructor.AssignRoleAsync(c.Ticket.ParticipantId, UserRole.IncidentCommander)); // Bea has it

        await server.CaughtUpAsync(c);
        var people = CopOf(c).Participants.ToDictionary(p => p.DisplayName);
        Assert.Equal(UserRole.Operations, people["Alex"].Role);
        Assert.Equal(UserRole.IncidentCommander, people["Bea"].Role);
        Assert.Equal(UserRole.Safety, people["Cal"].Role);

        // Commands now carry the new roles.
        await C2Of(b).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation);
        var created = server.Store.All().Single(e => e.Payload is IncidentCreated);
        Assert.Equal((b.Ticket.ParticipantId, UserRole.IncidentCommander), (created.ActorId!.Value, created.ActorRole!.Value));
        var handover = server.Store.All().Single(e => e.Payload is RoleHandedOver);
        Assert.Equal(a.Ticket!.ParticipantId, handover.ActorId);
    }

    [Fact]
    public async Task A_dropped_participant_keeps_their_place_within_the_grace_period()
    {
        await using var server = await StartAsync();
        var client = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var id = client.Ticket!.ParticipantId;

        await client.DropAsync();
        await Until(() => client.Connected && client.Reconnects > 0, TimeSpan.FromSeconds(15));

        Assert.Equal(id, client.Ticket!.ParticipantId); // the same person, by their token
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is ParticipantLeft);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => server.JoinAsync("Dee", UserRole.IncidentCommander));
        Assert.Contains("held by Alex", refused.Message);
    }

    [Fact]
    public async Task A_participant_away_longer_than_the_grace_period_counts_as_gone_and_rejoins_on_return()
    {
        await using var server = await StartAsync(settings: "--Server:DropGrace=00:00:00.500");
        var client = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var id = client.Ticket!.ParticipantId;

        await client.DropAsync(); // comes back after about 2 s: longer than the grace period
        await Until(() => server.Store.All().Any(e => e.Payload is ParticipantLeft { ParticipantId: var left } && left == id), TimeSpan.FromSeconds(10));
        await Until(() => client.Connected && client.Reconnects > 0, TimeSpan.FromSeconds(15));
        await server.CaughtUpAsync(client);

        Assert.Equal(2, server.Store.All().Count(e => e.Payload is ParticipantJoined { ParticipantId: var joined } && joined == id));
        Assert.True(CopOf(client).Participants.Single(p => p.Id == id).Present);
    }

    [Fact]
    public async Task The_AAR_credits_each_participant_and_filters_the_timeline_by_them()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        await server.CaughtUpAsync(ops);
        foreach (var unit in CopOf(ops).Units.Where(u => u.Agency?.AiControlled != true && u.Status == UnitStatus.Available).Take(3))
            await C2Of(ops).DispatchAsync(unit.Id, incident);
        await server.StepAsync(TimeSpan.FromMinutes(2));

        // During the session the review is the instructor's; once it has ended, trainees can open it too.
        Assert.False((await Record.ExceptionAsync(() => new ClientAarService(ops).BuildReportAsync(ops.Session!.SessionId))) is null);
        await server.Instructor.EndSessionAsync("Debrief");
        await Until(() => ops.Session!.State == SessionState.Ended, TimeSpan.FromSeconds(5));
        var report = await new ClientAarService(ops).BuildReportAsync(ops.Session!.SessionId);

        var alex = report.Participants.Single(p => p.Name == "Alex");
        var bea = report.Participants.Single(p => p.Name == "Bea");
        Assert.Contains(report.Participants, p => p.Roles == "Instructor");
        Assert.Equal("Incident Commander", alex.Roles);
        Assert.True(alex.Commands == 1, $"Alex: {alex.Commands} commands, {alex.Focus}");
        Assert.True(bea.Commands == 3, $"Bea: {bea.Commands} commands, {bea.Focus}");
        Assert.True(bea.Focus == "Dispatch 3", $"Bea focus: {bea.Focus}");

        Assert.All(report.DecisionsOf(bea.Id), d => Assert.Equal("Bea (Operations Section Chief)", d.Actor));
        var beaTimeline = report.TimelineOf(bea.Id).ToList();
        Assert.Equal(3, beaTimeline.Count(t => t.IsDecision));
        Assert.All(beaTimeline, t => Assert.Equal(bea.Id, t.ActorId));
        Assert.DoesNotContain(report.TimelineOf(alex.Id), t => t.Category == "UNIT DISPATCHED");
        Assert.Equal(report.Timeline.Count, report.TimelineOf(null).Count());
    }

    [Fact]
    public async Task Sessions_on_one_server_are_kept_apart()
    {
        await using var server = await StartAsync();
        var second = server.NewConnection();
        await second.ConnectAsync();
        var ticket = await second.CreateSessionAsync(new CreateSessionRequest(ScenarioCatalog.FreePlay, "Free play", "Ines", Pin));
        Assert.NotEqual(server.Session.Code, ticket.Code);
        Assert.NotEqual(server.Session.Id, ticket.SessionId);

        var inFirst = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        await C2Of(inFirst).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation, name: "Only in the first");
        await second.StartFeedAsync();
        await Task.Delay(500);

        Assert.DoesNotContain(CopOf(second).Incidents, i => i.Name == "Only in the first");
        Assert.All(second.Mirror.ReadAsync(ticket.SessionId).ToBlockingEnumerable(), e => Assert.Equal(ticket.SessionId, e.SessionId));
        await Assert.ThrowsAnyAsync<Exception>(() => second.CreateSessionAsync(new CreateSessionRequest("no-such-scenario", "", "Ines", Pin)));
    }
}
