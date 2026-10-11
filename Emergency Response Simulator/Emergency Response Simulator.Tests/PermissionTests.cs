using System.Reflection;
using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server;
using Microsoft.Extensions.DependencyInjection;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.ViewModels;
using Microsoft.AspNetCore.SignalR;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 11 M3: the permission matrix, the IC's unfilled functions, ICS staffing, role handover and workspaces.</summary>
public class PermissionTests
{
    private static IC2Service C2Of(SimulationConnection client) => RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);

    private static readonly UserRole[] Players = Enum.GetValues<UserRole>().Where(r => r != UserRole.Instructor).ToArray();

    /// <summary>Every C2 and IAP method a client can reach, as (service, method).</summary>
    private static IEnumerable<(string Service, MethodInfo Method)> Commands => CommandDispatcher.Callable
        .Where(c => c.Service is SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap);

    [Fact]
    public void Every_command_is_placed_in_the_matrix_and_every_function_has_a_holder()
    {
        foreach (var (service, method) in Commands)
            Assert.True(PermissionMatrix.Find(CommandDispatcher.Contracts[service], method.Name) is not null, $"{service}.{method.Name} has no entry");
        Assert.All(PermissionMatrix.Commands, c => Assert.NotNull(c.Contract.GetMethod(c.Method)));
        Assert.Equal(Commands.Count(), PermissionMatrix.Commands.Count());

        // Every role that plays (not observers) has something only it, or its function's holders, can do.
        foreach (var role in Players.Where(r => r != UserRole.Observer))
            Assert.Contains(PermissionMatrix.Commands, c => c.Owners.Contains(role));

        // An unknown method is refused, so a new command has to be placed in the matrix before anyone can use it.
        Assert.False(PermissionMatrix.Decide(UserRole.Instructor, typeof(IC2Service), "MadeUpAsync", _ => true).Allowed);
    }

    /// <summary>
    /// The matrix's rules, stated independently of its data: observers give no commands; the instructor gives any; a role
    /// gives a command if it holds the function (or the command is anyone's); the IC also gives any whose function has a
    /// holder nobody present fills.
    /// </summary>
    [Fact]
    public void The_matrix_follows_its_rules_for_every_command_and_role()
    {
        foreach (var command in PermissionMatrix.Commands)
            foreach (var role in Enum.GetValues<UserRole>())
                foreach (var everyoneHere in new[] { true, false })
                {
                    bool Filled(UserRole r) => everyoneHere || r == role;
                    var decision = PermissionMatrix.Decide(role, command.Contract, command.Method, Filled);
                    var expected = role switch
                    {
                        UserRole.Instructor => true,
                        UserRole.Observer => false,
                        UserRole.IncidentCommander => command.Anyone || command.Owners.Contains(role) || command.Owners.Any(o => !Filled(o)),
                        _ => command.Anyone || command.Owners.Contains(role),
                    };
                    Assert.True(expected == decision.Allowed, $"{role} · {command.Method} · everyone here {everyoneHere}: {decision.Reason}");
                    if (!decision.Allowed) Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                }

        // Spot checks of what the matrix says.
        static bool May(UserRole role, string method, bool everyoneHere = true) =>
            PermissionMatrix.Decide<IC2Service>(role, method, r => everyoneHere).Allowed;
        Assert.False(May(UserRole.Operations, nameof(IC2Service.IssuePublicStatementAsync)));
        Assert.True(May(UserRole.PublicInformation, nameof(IC2Service.IssuePublicStatementAsync)));
        Assert.False(May(UserRole.IncidentCommander, nameof(IC2Service.IssuePublicStatementAsync)));
        Assert.True(May(UserRole.IncidentCommander, nameof(IC2Service.IssuePublicStatementAsync), everyoneHere: false));
        Assert.True(May(UserRole.Dispatcher, nameof(IC2Service.DispatchAsync)));
        Assert.False(May(UserRole.Planning, nameof(IC2Service.DispatchAsync)));
        Assert.True(May(UserRole.Logistics, nameof(IC2Service.PatchChannelsAsync)));
        Assert.True(May(UserRole.Safety, nameof(IC2Service.SignalEvacuationAsync)));
        Assert.True(May(UserRole.Observer, nameof(IC2Service.TransmitAsync)) is false);
        Assert.True(PermissionMatrix.Decide<IIapService>(UserRole.Planning, nameof(IIapService.SubmitAsync), _ => true).Allowed);
        Assert.False(PermissionMatrix.Decide<IIapService>(UserRole.Planning, nameof(IIapService.ApproveAsync), _ => true).Allowed);
        Assert.True(PermissionMatrix.Decide<IIapService>(UserRole.IncidentCommander, nameof(IIapService.ApproveAsync), _ => true).Allowed);
    }

    /// <summary>
    /// The exit criterion: every command by every role, sent straight to the server's dispatcher as that participant (no
    /// client, no UI), is refused for permission exactly when the matrix says so, with everyone present and with the IC
    /// nearly alone.
    /// </summary>
    [Fact]
    public async Task The_server_enforces_the_matrix_for_every_command_and_role()
    {
        await using var server = await StartAsync();
        foreach (var role in Players)
            await server.JoinAsync($"{role} player", role, feed: false);
        await CheckEveryCommand(server, "everyone present");

        // Everyone but the IC leaves: their functions fall to the IC.
        foreach (var participant in server.Session.Participants.Where(p => p.Role is not (UserRole.IncidentCommander or UserRole.Instructor or UserRole.Operations)))
            await server.Session.LeaveAsync(participant);
        await CheckEveryCommand(server, "only the IC and Operations");
    }

    private static async Task CheckEveryCommand(InProcessServer server, string situation)
    {
        var checkedCount = 0;
        foreach (var participant in server.Session.Participants.Where(p => p.Present).ToList())
            foreach (var (service, method) in Commands)
            {
                var decision = CommandAuthorizer.Decide(server.Session, participant.Role, CommandDispatcher.Contracts[service], method.Name);
                var args = CommandDispatcher.WireParameters(method).Select(p => Dummy(p.ParameterType, server)).ToArray();
                string? refusal = null;
                try
                {
                    await server.Dispatcher.InvokeAsync(server.Session, participant, service, method.Name, args, CancellationToken.None);
                }
                catch (Exception ex) // a refusal is a HubException; a service may also throw on a made-up argument
                {
                    refusal = ex is HubException ? ex.Message : $"({ex.GetType().Name})";
                }
                var refusedForPermission = refusal is not null && refusal == decision.Reason;
                Assert.True(decision.Allowed != refusedForPermission,
                    $"{situation}: {participant.Role} · {service}.{method.Name}: matrix {(decision.Allowed ? "allows" : "refuses")}, server said \"{refusal ?? "ok"}\"");
                checkedCount++;
            }
        Assert.True(checkedCount >= Commands.Count() * 2);
    }

    /// <summary>A well-formed argument that names nothing real, so commands fail validation (or do something harmless).</summary>
    private static JsonElement Dummy(Type type, InProcessServer server)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        object? value =
            underlying == typeof(Guid) ? Guid.NewGuid()
            : underlying == typeof(string) ? "Permission check"
            : underlying == typeof(GeoPoint) ? BarrowStreetScenario.FireLocation
            : underlying == typeof(DateTimeOffset) ? server.Engine.SimTime
            : underlying == typeof(IReadOnlyList<GeoPoint>) ? new List<GeoPoint>()
            : underlying == typeof(IReadOnlyList<string>) ? new List<string>()
            : underlying == typeof(IapContent) ? new IapContent()
            : underlying.IsEnum ? Enum.GetValues(underlying).GetValue(0)
            : underlying == typeof(bool) ? false
            : underlying == typeof(int) ? 1
            : underlying == typeof(double) ? 1.0
            : null;
        return JsonSerializer.SerializeToElement(value, type, WireJson.Options);
    }

    [Fact]
    public async Task An_operations_player_cannot_issue_a_public_statement_even_bypassing_the_ui()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        JsonElement[] Statement(string text) => [JsonSerializer.SerializeToElement(text, WireJson.Options)];

        // A raw hub call, as a modified client would send it: refused by the server, nothing recorded.
        var refused = await Assert.ThrowsAsync<HubException>(() =>
            ops.CallAsync(SimulationHubProtocol.Services.C2, nameof(IC2Service.IssuePublicStatementAsync), Statement("Stay indoors.")));
        Assert.Contains("Public information", refused.Message);
        var viaProxy = await C2Of(ops).IssuePublicStatementAsync("Stay indoors, close windows.");
        Assert.False(viaProxy.Succeeded);
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is PublicStatementIssued);

        // With no PIO in the session the function is the IC's ...
        Assert.True((await C2Of(ic).IssuePublicStatementAsync("Crews are at the scene on Barrow Street.")).Succeeded);

        // ... and once a PIO joins, only the PIO's.
        var pio = await server.JoinAsync("Cass", UserRole.PublicInformation);
        Assert.False((await C2Of(ic).IssuePublicStatementAsync("A second statement.")).Succeeded);
        Assert.True((await C2Of(pio).IssuePublicStatementAsync("Avoid the area of Barrow Street.")).Succeeded);
        Assert.False((await C2Of(ops).IssuePublicStatementAsync("One more try.")).Succeeded);

        var statements = server.Store.All().Where(e => e.Payload is PublicStatementIssued).ToList();
        Assert.Equal([UserRole.IncidentCommander, UserRole.PublicInformation], statements.Select(e => e.ActorRole!.Value));
    }

    [Fact]
    public async Task The_client_disables_exactly_what_the_server_would_refuse()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        await server.CaughtUpAsync(ic);
        await server.CaughtUpAsync(ops);

        foreach (var client in new[] { ic, ops })
        {
            var permissions = new Permissions(CopOf(client), client);
            var record = server.Session.Find(client.Ticket!.ParticipantId)!;
            foreach (var (service, method) in Commands)
            {
                var contract = CommandDispatcher.Contracts[service];
                var server_ = CommandAuthorizer.Decide(server.Session, record.Role, contract, method.Name).Allowed;
                var client_ = service == SimulationHubProtocol.Services.C2 ? permissions.C2(method.Name) : permissions.Iap(method.Name);
                Assert.True(server_ == client_, $"{record.Role} · {method.Name}: server {server_}, client {client_}");
            }
        }
        Assert.False(new Permissions(CopOf(ops), ops).C2(nameof(IC2Service.IssuePublicStatementAsync)));
        Assert.Contains("Public information", new Permissions(CopOf(ic), ic).Inherited);

        // A standalone session has one person in every role.
        Assert.True(new Permissions(CopOf(ops), null).C2(nameof(IC2Service.IssuePublicStatementAsync)));
    }

    [Fact]
    public async Task Staffing_an_ics_position_with_a_participant_gives_them_that_role()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var bea = await server.JoinAsync("Bea", UserRole.Observer);
        var beaId = bea.Ticket!.ParticipantId;
        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;

        // Operations is unfilled, so the IC organises groups himself ...
        Assert.True((await C2Of(ic).FormGroupAsync(incident, "Fire Group", IcsGroupKind.Group)).Succeeded);
        Assert.False((await C2Of(bea).FormGroupAsync(incident, "Medical Group", IcsGroupKind.Group)).Succeeded);

        // ... until he puts Bea in the Operations position: a blank name means hers, and she takes the role.
        var staffed = await C2Of(ic).AssignIcsPositionAsync(incident, IcsRole.Operations, "", beaId);
        Assert.True(staffed.Succeeded, staffed.Error);
        Assert.Equal(UserRole.Operations, server.Session.Find(beaId)!.Role);
        var assigned = server.Store.All().Last(e => e.Payload is IcsPositionAssigned).Payload as IcsPositionAssigned;
        Assert.Equal(("Bea", beaId), (assigned!.Name, assigned.UserId!.Value));
        Assert.Contains(server.Store.All(), e => e.Payload is RoleAssigned r && r.ParticipantId == beaId && r.Role == UserRole.Operations);

        Assert.True((await C2Of(bea).FormGroupAsync(incident, "Medical Group", IcsGroupKind.Group)).Succeeded);
        Assert.False((await C2Of(ic).FormGroupAsync(incident, "Search Group", IcsGroupKind.Group)).Succeeded);
        await server.CaughtUpAsync(ic);
        Assert.Equal(beaId, CopOf(ic).FindIncident(incident)!.Command.Holders[IcsRole.Operations]);

        // A position someone else holds can't be given away under them; nor can it go to someone not in the session.
        var cal = await server.JoinAsync("Cal", UserRole.Observer);
        Assert.False((await C2Of(ic).AssignIcsPositionAsync(incident, IcsRole.Operations, "", cal.Ticket!.ParticipantId)).Succeeded);
        Assert.False((await C2Of(ic).AssignIcsPositionAsync(incident, IcsRole.Safety, "", Guid.NewGuid())).Succeeded);
        Assert.Equal(UserRole.Observer, server.Session.Find(cal.Ticket.ParticipantId)!.Role);
        // Without a participant it stays a name on the chart, as before.
        Assert.True((await C2Of(ic).AssignIcsPositionAsync(incident, IcsRole.Safety, "Stn Off. Kelly")).Succeeded);
    }

    [Fact]
    public async Task A_role_handover_follows_the_crew_handover_model()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var bea = await server.JoinAsync("Bea", UserRole.Observer);
        var cal = await server.JoinAsync("Cal", UserRole.Observer);
        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        await server.CaughtUpAsync(ic);
        var engine = CopOf(ic).Units.First(u => u.Type == UnitType.Engine && u.Agency?.AiControlled != true && u.Status == UnitStatus.Available);
        Assert.True((await C2Of(ic).DispatchAsync(engine.Id, incident)).Succeeded);
        var order = await C2Of(ic).IssueOrderAsync(incident, OrderTargetKind.Unit, engine.Id, "Report the water supply");
        Assert.True(order.Succeeded, order.Error);

        // A full briefing: Alex keeps command for five minutes of simulation time, then Bea has it and was told the open business.
        var started = server.Engine.SimTime;
        await ic.HandOverRoleAsync(bea.Ticket!.ParticipantId, UserRole.Observer, fullBriefing: true);
        var begun = (RoleHandoverBegun)server.Store.All().Last(e => e.Payload is RoleHandoverBegun).Payload;
        Assert.Equal(started + TimeSpan.FromMinutes(5), begun.CompletesAt);
        await Task.Delay(400); // a few broadcaster ticks: nothing happens while the clock stands still
        Assert.Equal(UserRole.IncidentCommander, server.Session.Find(ic.Ticket!.ParticipantId)!.Role);
        Assert.True((await C2Of(ic).UpdateIncidentAsync(incident, priority: IncidentPriority.Critical)).Succeeded);
        Assert.False((await C2Of(bea).UpdateIncidentAsync(incident, priority: IncidentPriority.High)).Succeeded);
        await Assert.ThrowsAsync<HubException>(() => ic.HandOverRoleAsync(cal.Ticket!.ParticipantId, UserRole.Observer)); // one at a time

        await server.StepAsync(TimeSpan.FromMinutes(5));
        await Until(() => server.Store.All().Any(e => e.Payload is RoleHandedOver), TimeSpan.FromSeconds(10));
        var briefed = (RoleHandedOver)server.Store.All().Single(e => e.Payload is RoleHandedOver).Payload;
        Assert.True(briefed.Briefed);
        Assert.Equal(begun.HandoverId, briefed.HandoverId);
        Assert.Contains(briefed.Items!, i => i.Contains("open order"));
        Assert.Equal(UserRole.IncidentCommander, server.Session.Find(bea.Ticket.ParticipantId)!.Role);
        Assert.True((await C2Of(bea).UpdateIncidentAsync(incident, priority: IncidentPriority.High)).Succeeded);

        // A quick changeover is done in a minute and a half and passes nothing on; the review says what Cal never heard.
        await bea.HandOverRoleAsync(cal.Ticket!.ParticipantId, UserRole.Observer, fullBriefing: false);
        await server.StepAsync(TimeSpan.FromMinutes(1.5));
        await Until(() => server.Store.All().Count(e => e.Payload is RoleHandedOver) == 2, TimeSpan.FromSeconds(10));
        var quick = (RoleHandedOver)server.Store.All().Last(e => e.Payload is RoleHandedOver).Payload;
        Assert.False(quick.Briefed);
        Assert.NotEmpty(quick.Items!);

        // A handover whose taker leaves is called off.
        await cal.HandOverRoleAsync(bea.Ticket.ParticipantId, UserRole.Observer);
        await server.Session.LeaveAsync(server.Session.Find(bea.Ticket.ParticipantId)!);
        await Until(() => server.Store.All().Any(e => e.Payload is RoleHandoverCancelled), TimeSpan.FromSeconds(10));
        Assert.Equal(UserRole.IncidentCommander, server.Session.Find(cal.Ticket.ParticipantId)!.Role);

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("briefed Bea fully"));
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("Cal was never told"));
    }

    [Fact]
    public async Task Handovers_in_the_lobby_happen_at_once()
    {
        await using var server = await StartAsync(start: false);
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var bea = await server.JoinAsync("Bea", UserRole.Observer);
        await ic.HandOverRoleAsync(bea.Ticket!.ParticipantId, UserRole.Observer);
        Assert.Equal(UserRole.IncidentCommander, server.Session.Find(bea.Ticket.ParticipantId)!.Role);
        Assert.Null(((RoleHandedOver)server.Store.All().Single(e => e.Payload is RoleHandedOver).Payload).HandoverId);
    }

    [Fact]
    public void Every_role_has_a_workspace_over_the_same_cop()
    {
        foreach (var role in Enum.GetValues<UserRole>())
        {
            var workspace = RoleWorkspaces.For(role);
            Assert.Contains(workspace.Focus, workspace.Panels);
            Assert.Contains(WorkspacePanel.Session, workspace.Panels);
            Assert.Contains(workspace.Board, workspace.Boards);
        }
        Assert.Equal(Enum.GetValues<WorkspacePanel>(), RoleWorkspaces.Full.Panels);

        // Each puts the panel its own work is on in reach.
        Assert.Contains(WorkspacePanel.Agencies, RoleWorkspaces.For(UserRole.PublicInformation).Panels);
        Assert.Contains(WorkspacePanel.Crews, RoleWorkspaces.For(UserRole.Safety).Panels);
        Assert.Contains(WorkspacePanel.Ics, RoleWorkspaces.For(UserRole.Operations).Panels);
        Assert.Contains(WorkspacePanel.Comms, RoleWorkspaces.For(UserRole.Dispatcher).Panels);
        Assert.Equal(WorkspaceBoard.Instructor, RoleWorkspaces.For(UserRole.Instructor).Board);
        Assert.Equal(RoleWorkspaces.All.Count + 1, RoleWorkspaces.Choices().Count);
    }
}
