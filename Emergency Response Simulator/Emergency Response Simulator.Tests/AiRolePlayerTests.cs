using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Instructor;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.DependencyInjection;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 11 M5: political pressure, and agencies run by participants, over a real in-process server.</summary>
public class AiRolePlayerTests
{
    private static IC2Service C2Of(SimulationConnection client) => RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);

    private static IInstructorService ConsoleOf(SimulationConnection client) =>
        RemoteProxy.Create<IInstructorService>(client, SimulationHubProtocol.Services.Instructor);

    private static void Ok(CommandResult result) => Assert.True(result.Succeeded, result.Error);

    private static List<PoliticalCallReceived> Calls(InProcessServer server) =>
        [.. server.Store.All().Select(e => e.Payload).OfType<PoliticalCallReceived>()];

    private static async Task<Guid> OpenIncident(SimulationConnection ic, IncidentPriority priority) =>
        (await C2Of(ic).CreateIncidentAsync(IncidentType.Explosion, priority, BarrowStreetScenario.FireLocation, "Barrow Street")).EntityId!.Value;

    // ---- Political pressure ----

    [Fact]
    public async Task Ignored_calls_climb_from_the_td_to_the_minister_who_then_goes_public()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        await OpenIncident(ic, IncidentPriority.Critical);

        await server.StepAsync(TimeSpan.FromMinutes(5)); // a Critical incident: the TD rings within about four minutes
        var td = Assert.Single(Calls(server));
        Assert.Equal((PoliticalOffice.LocalTd, PoliticalPressureSystem.Td, 1, "Incident Commander"), (td.Office, td.Caller, td.Pressure, td.To));
        Assert.Contains("Barrow Street", td.Message);

        await server.StepAsync(TimeSpan.FromMinutes(5.5)); // nobody calls back
        var council = Calls(server).Last();
        Assert.Equal((PoliticalOffice.CouncilChiefExecutive, 2), (council.Office, council.Pressure));
        Assert.Contains("Deputy Nolan tells me nobody has returned her call", council.Message);

        await server.StepAsync(TimeSpan.FromMinutes(5.5));
        var minister = Calls(server).Last();
        Assert.Equal((PoliticalOffice.MinistersOffice, 3), (minister.Office, minister.Pressure));
        Assert.Contains("make a statement herself", minister.Message);

        await server.StepAsync(TimeSpan.FromMinutes(5.5));
        var stored = server.Store.All();
        Assert.Contains(stored, e => e.Payload is ReportReceived r && r.Claim.Contains("not been briefed"));
        Assert.Contains(stored, e => e.Payload is AlertRaised a && a.Title.Contains("not been briefed"));
        Assert.Equal(3, stored.Count(e => e.Payload is PoliticalFallout));
        Assert.All(stored.Where(e => e.Payload is PoliticalFallout), e => Assert.Equal(EventVisibility.Truth, e.Visibility));

        // Then they leave command alone for a while.
        var before = Calls(server).Count;
        await server.StepAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(before, Calls(server).Count);

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.StartsWith("Political calls: 0 of 3 returned"));
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("went public without a briefing"));
    }

    [Fact]
    public async Task A_call_returned_eases_the_pressure_and_counts_as_a_timed_decision()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        await OpenIncident(ic, IncidentPriority.Critical);
        await server.StepAsync(TimeSpan.FromMinutes(5));
        var td = Assert.Single(Calls(server));

        Assert.False((await C2Of(ops).AnswerPoliticalCallAsync(td.CallId, "Not my call to make")).Succeeded); // Operations doesn't
        Assert.False((await C2Of(ic).AnswerPoliticalCallAsync(td.CallId, "  ")).Succeeded);
        Ok(await C2Of(ic).AnswerPoliticalCallAsync(td.CallId, "Crews are at the scene; residents nearby should keep windows closed."));
        Assert.False((await C2Of(ic).AnswerPoliticalCallAsync(td.CallId, "Again")).Succeeded);

        // No escalation; the next call comes a full interval later, from the TD again.
        await server.StepAsync(TimeSpan.FromMinutes(8));
        Assert.Single(Calls(server));
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is PoliticalFallout);
        await server.StepAsync(TimeSpan.FromMinutes(4));
        Assert.Equal(PoliticalOffice.LocalTd, Calls(server).Last().Office);

        await server.CaughtUpAsync(ic);
        Assert.NotNull(CopOf(ic).PoliticalCalls.First().AnsweredAt);
        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        var decision = report.Decisions.Single(d => d.Category == "Political");
        Assert.Equal(Rating.Good, decision.Rating);
        Assert.Contains("Deputy Ciara Nolan TD rang", decision.Trigger);
        Assert.Contains("Alex", decision.Actor);
    }

    [Fact]
    public async Task Calls_go_to_the_pio_once_there_is_one_and_the_pio_answers()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var pio = await server.JoinAsync("Cass", UserRole.PublicInformation);
        var incident = await OpenIncident(ic, IncidentPriority.Critical);
        Ok(await C2Of(ic).AssignIcsPositionAsync(incident, IcsRole.PublicInformation, "", pio.Ticket!.ParticipantId));
        await server.StepAsync(TimeSpan.FromMinutes(5));
        var call = Assert.Single(Calls(server));
        Assert.Equal("Public Information Officer", call.To);
        Ok(await C2Of(pio).AnswerPoliticalCallAsync(call.CallId, "We'll brief you at the top of the hour."));
    }

    [Fact]
    public async Task How_often_they_ring_follows_how_serious_the_incident_is()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var medium = await OpenIncident(ic, IncidentPriority.Medium);
        await server.StepAsync(TimeSpan.FromMinutes(8));
        Assert.Empty(Calls(server)); // a Critical incident would have had its first call by now
        await server.StepAsync(TimeSpan.FromMinutes(6));
        Assert.Equal(medium, Assert.Single(Calls(server)).IncidentId);

        await using var quiet = await StartAsync();
        var other = await quiet.JoinAsync("Alex", UserRole.IncidentCommander);
        await OpenIncident(other, IncidentPriority.Low);
        await quiet.StepAsync(TimeSpan.FromMinutes(30));
        Assert.Empty(Calls(quiet)); // a Low incident doesn't make the news
    }

    [Fact]
    public async Task The_instructor_can_play_the_politicians_and_silence_then_costs_nothing_they_dont_choose()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var console = ConsoleOf(server.Instructor);
        Assert.Contains(await console.ListAiRolesAsync(), r => r.Key == AiRoles.Politicians && r.System == nameof(PoliticalPressureSystem));
        Ok(await console.TakeOverAsync(AiRoles.Politicians));
        var incident = await OpenIncident(ic, IncidentPriority.Critical);
        await server.StepAsync(TimeSpan.FromMinutes(15));
        Assert.Empty(Calls(server));

        Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Politician, "Office of the Minister for Health",
            "The Minister wants to know if the hospitals can cope.", IncidentId: incident)));
        await server.StepAsync(TimeSpan.FromMinutes(1));
        var call = Assert.Single(Calls(server));
        Assert.Equal(PoliticalOffice.MinistersOffice, call.Office);
        await server.StepAsync(TimeSpan.FromMinutes(10));
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is PoliticalFallout);

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Timeline, t => t.RolePlay && t.Actor == "Instructor role-play as Office of the Minister for Health" && t.Category == "POLITICAL");
    }

    // ---- Agencies run by participants ----

    [Fact]
    public async Task A_participant_can_run_another_agency_task_its_units_and_answer_for_it()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var cal = await server.JoinAsync("Cal", UserRole.AgencyChief);
        var bea = await server.JoinAsync("Bea", UserRole.Observer);
        var console = ConsoleOf(server.Instructor);
        await server.CaughtUpAsync(ic);
        var north = CopOf(ic).Agencies.Single(a => a.Name == "City Fire Brigade, North District");
        var key = AiRoles.Agency(north.Id);
        var engine31 = CopOf(ic).Units.Single(u => u.Callsign == "Engine 31").Id;
        var engine4 = CopOf(ic).Units.Single(u => u.Callsign == "Engine 4").Id;
        var incident = await OpenIncident(ic, IncidentPriority.High);

        // Before: nobody runs it but the simulation, and an agency chief commands nothing.
        Assert.False((await C2Of(cal).DispatchAsync(engine31, incident)).Succeeded);
        Assert.False((await console.GiveToParticipantAsync(key, bea.Ticket!.ParticipantId)).Succeeded); // not an agency chief
        Assert.False((await console.GiveToParticipantAsync(AiRoles.Media, cal.Ticket!.ParticipantId)).Succeeded); // not an agency

        Ok(await console.GiveToParticipantAsync(key, cal.Ticket.ParticipantId));
        Assert.Equal("Cal", (await console.ListAiRolesAsync()).Single(r => r.Key == key).PlayedBy);
        Assert.False((await console.TakeOverAsync(key)).Succeeded); // Cal has it
        await server.CaughtUpAsync(ic);
        var seen = CopOf(ic).Agencies.Single(a => a.Id == north.Id);
        Assert.Equal((AgencyController.Participant, cal.Ticket.ParticipantId, "Cal"), (seen.Controller, seen.ControlledBy, seen.ControllerName));
        Assert.True(seen.AiControlled); // still another agency: not command's to dispatch

        // Cal tasks the agency's own units, and only those; command still can't.
        Ok(await C2Of(cal).DispatchAsync(engine31, incident));
        var notTheirs = await C2Of(cal).DispatchAsync(engine4, incident);
        Assert.Contains("isn't one of its units", notTheirs.Error);
        var engine33 = CopOf(ic).Units.Single(u => u.Callsign == "Engine 33").Id;
        Assert.False((await C2Of(ic).DispatchAsync(engine33, incident)).Succeeded);

        // Command asks the agency for units: it waits for Cal, who sends one.
        Ok(await C2Of(ic).RequestAgencyAssistanceAsync(north.Id, incident, 2, "Second engine"));
        await server.StepAsync(TimeSpan.FromMinutes(3));
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is AgencyAssistanceDecided);
        await server.CaughtUpAsync(cal);
        var request = CopOf(cal).AssistanceRequests.Single();
        Assert.False((await C2Of(ic).AnswerAgencyRequestAsync(request.Id, true)).Succeeded); // not the IC's to answer, even by fallback
        Assert.False((await C2Of(cal).AnswerAgencyRequestAsync(request.Id, true, quantity: 5)).Succeeded);
        Ok(await C2Of(cal).AnswerAgencyRequestAsync(request.Id, true, quantity: 1, "Engine 33 on its way."));
        await server.StepAsync(TimeSpan.FromSeconds(5));
        var decided = (AgencyAssistanceDecided)server.Store.All().Single(e => e.Payload is AgencyAssistanceDecided).Payload;
        Assert.Equal((1, "Engine 33 on its way."), (decided.Granted, decided.Reason));

        // An invitation to unified command: Cal declines in their own words.
        Ok(await C2Of(ic).InviteToUnifiedCommandAsync(incident, north.Id));
        await server.StepAsync(TimeSpan.FromMinutes(4));
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is UnifiedCommandDeclined or UnifiedCommandJoined);
        await server.CaughtUpAsync(cal);
        var invitation = CopOf(cal).UnifiedCommandInvitations.Single(i => i.Accepted is null);
        Ok(await C2Of(cal).AnswerAgencyRequestAsync(invitation.Id, false, text: "We'll stay with our own command for now."));
        await server.StepAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(server.Store.All(), e => e.Payload is UnifiedCommandDeclined { Reason: "We'll stay with our own command for now." });

        // The review credits Cal.
        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Equal(2, report.Decisions.Count(d => d.Actor?.StartsWith("Cal") == true && d.Category == "Multi-agency"));
    }

    [Fact]
    public async Task An_agency_goes_back_to_the_simulation_when_its_participant_leaves_or_is_handed_back()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var cal = await server.JoinAsync("Cal", UserRole.AgencyChief);
        var console = ConsoleOf(server.Instructor);
        await server.CaughtUpAsync(ic);
        var north = CopOf(ic).Agencies.Single(a => a.Name == "City Fire Brigade, North District").Id;
        var key = AiRoles.Agency(north);
        var incident = await OpenIncident(ic, IncidentPriority.High);

        Ok(await console.GiveToParticipantAsync(key, cal.Ticket!.ParticipantId));
        Ok(await console.HandBackAsync(key));
        await server.CaughtUpAsync(ic);
        Assert.Equal(AgencyController.Simulation, CopOf(ic).Agencies.Single(a => a.Id == north).Controller);

        Ok(await console.GiveToParticipantAsync(key, cal.Ticket.ParticipantId));
        Ok(await C2Of(ic).RequestAgencyAssistanceAsync(north, incident, 1, "One engine"));
        await server.Session.LeaveAsync(server.Session.Find(cal.Ticket.ParticipantId)!);
        await server.StepAsync(TimeSpan.FromMinutes(3)); // the simulation answers once Cal has gone
        Assert.Single(server.Store.All(), e => e.Payload is AgencyAssistanceDecided);
        await server.CaughtUpAsync(ic);
        Assert.Equal(AgencyController.Simulation, CopOf(ic).Agencies.Single(a => a.Id == north).Controller);
        Assert.Null((await console.ListAiRolesAsync()).Single(r => r.Key == key).PlayedBy);
    }
}
