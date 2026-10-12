using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Aar;
using Emergency_Response_Simulator.Simulation.Instructor;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Emergency_Response_Simulator.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 12: the multiplayer trainer completed. Roles played by people are theirs alone; every role has a desk; the
/// instructor sees who is overloaded and speaks to the players outside the world; the media and the politicians go
/// deeper; patched channels and emergency traffic hold on the voice radio; observers rate competency areas.
/// </summary>
public class MultiplayerCompletionTests
{
    private static IC2Service C2Of(SimulationConnection client) => RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);

    private static IInstructorService ConsoleOf(SimulationConnection client) =>
        RemoteProxy.Create<IInstructorService>(client, SimulationHubProtocol.Services.Instructor);

    private static IAssessmentService AssessorOf(SimulationConnection client) =>
        RemoteProxy.Create<IAssessmentService>(client, SimulationHubProtocol.Services.Assessment);

    private static List<SimEvent> MirrorOf(SimulationConnection client) => [.. client.Mirror.ReadAsync(client.Session!.SessionId).ToBlockingEnumerable()];

    private static void Ok(CommandResult result) => Assert.True(result.Succeeded, result.Error);

    private static async Task<Guid> OpenIncident(SimulationConnection ic, IncidentPriority priority) =>
        (await C2Of(ic).CreateIncidentAsync(IncidentType.Explosion, priority, BarrowStreetScenario.FireLocation, "Barrow Street")).EntityId!.Value;

    private static IReadOnlyList<DeskItem> DeskOf(SimulationConnection client)
    {
        var cop = CopOf(client);
        var present = cop.Participants.Where(p => p.Present).Select(p => p.Role).ToHashSet();
        var me = cop.Participants.Single(p => p.Id == client.Ticket!.ParticipantId);
        return RoleDesk.For(me.Role, cop, present.Contains, me.Id);
    }

    // ---- Roles: what a person plays is theirs ----

    [Fact]
    public async Task The_simulation_never_answers_the_media_for_a_pio_a_participant_plays()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var pio = await server.JoinAsync("Cass", UserRole.PublicInformation);
        var incident = await OpenIncident(ic, IncidentPriority.Critical);
        Ok(await C2Of(ic).AssignIcsPositionAsync(incident, IcsRole.PublicInformation, "", pio.Ticket!.ParticipantId));

        // Journalists ring after about five minutes; a simulated PIO would answer two minutes later. Cass is a person.
        await server.StepAsync(TimeSpan.FromMinutes(9));
        var stored = server.Store.All();
        Assert.Contains(stored, e => e.Payload is MediaEnquiryReceived);
        Assert.DoesNotContain(stored, e => e.Payload is PublicStatementIssued);

        // The enquiries wait on Cass's desk, not the IC's: the function is hers.
        await server.CaughtUpAsync(pio);
        await server.CaughtUpAsync(ic);
        Assert.Contains(DeskOf(pio), d => d.Function == "Public information");
        Assert.DoesNotContain(DeskOf(ic), d => d.Function == "Public information");

        // She answers; the desk clears.
        Ok(await C2Of(pio).IssuePublicStatementAsync("Emergency services are at Barrow Street. People nearby should stay indoors and keep windows closed.", incident));
        await server.StepAsync(TimeSpan.FromSeconds(10));
        await server.CaughtUpAsync(pio);
        Assert.DoesNotContain(DeskOf(pio), d => d.Function == "Public information");
        Assert.Equal($"PIO Cass", server.Store.All().Select(e => e.Payload).OfType<PublicStatementIssued>().Single().Issuer);
    }

    [Fact]
    public async Task Politicians_ask_for_the_pio_a_participant_plays_even_before_the_ic_names_them()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        await server.JoinAsync("Cass", UserRole.PublicInformation);
        await OpenIncident(ic, IncidentPriority.Critical);
        await server.StepAsync(TimeSpan.FromMinutes(5));
        var call = Assert.Single(server.Store.All().Select(e => e.Payload).OfType<PoliticalCallReceived>());
        Assert.Equal("Public Information Officer", call.To);
    }

    // ---- Every role's desk ----

    [Fact]
    public async Task Each_desk_holds_what_waits_for_its_role_and_unfilled_functions_land_on_the_ic()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        var incident = await OpenIncident(ic, IncidentPriority.High);
        var console = ConsoleOf(server.Instructor);
        Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Official, "Garda Inspector, Pearse Street",
            "Request authority to close the Grand Canal Street bridge. Traffic is backing up into the cordon.", IncidentId: incident)));
        Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Journalist, "RTÉ News", "Is there a danger to the public?", IncidentId: incident)));
        await server.StepAsync(TimeSpan.FromMinutes(1));
        await server.CaughtUpAsync(ic);
        await server.CaughtUpAsync(ops);

        // The IC decides approvals, and with no PIO in the session answers the press too; Operations has neither.
        var alex = DeskOf(ic);
        Assert.Contains(alex, d => d.Function == "Approvals" && d.Text.Contains("Grand Canal Street"));
        Assert.Contains(alex, d => d.Function == "Public information" && d.Text.Contains("RTÉ News"));
        var bea = DeskOf(ops);
        Assert.DoesNotContain(bea, d => d.Function is "Approvals" or "Public information");
        // Nobody is assigned to the incident: the units are Operations' to send.
        Assert.Contains(bea, d => d.Function == "Tasking units" && d.Urgency == DeskUrgency.Urgent && d.IncidentId == incident);

        // A PIO joins: the press moves to their desk.
        var pio = await server.JoinAsync("Cass", UserRole.PublicInformation);
        await server.CaughtUpAsync(ic);
        Assert.DoesNotContain(DeskOf(ic), d => d.Function == "Public information");
        await server.CaughtUpAsync(pio);
        Assert.Contains(DeskOf(pio), d => d.Function == "Public information");

        // The instructor's monitor counts every player's desk.
        await server.CaughtUpAsync(server.Instructor);
        var monitor = PlayerMonitor.Build(MirrorOf(server.Instructor), CopOf(server.Instructor), server.Engine.SimTime);
        Assert.True(monitor.Players.Single(p => p.Name == "Alex").Waiting >= 1);
        Assert.True(monitor.Players.Single(p => p.Name == "Bea").Waiting >= 1);
        Assert.NotNull(monitor.Players.Single(p => p.Name == "Alex").Oldest);
    }

    [Fact]
    public async Task A_desk_piled_high_shows_its_player_as_overloaded()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var incident = await OpenIncident(ic, IncidentPriority.Medium);
        var console = ConsoleOf(server.Instructor);
        for (var i = 1; i <= PlayerMonitor.OverloadedAt; i++)
            Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Official, $"Official {i}", $"Request number {i}. Please decide.", IncidentId: incident)));
        await server.StepAsync(TimeSpan.FromMinutes(1));
        await server.CaughtUpAsync(server.Instructor);
        var alex = PlayerMonitor.Build(MirrorOf(server.Instructor), CopOf(server.Instructor), server.Engine.SimTime).Players.Single(p => p.Name == "Alex");
        Assert.True(alex.Overloaded, $"{alex.Waiting} waiting");
    }

    // ---- Exercise control ----

    [Fact]
    public async Task The_instructor_speaks_to_the_players_outside_the_world()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        var console = ConsoleOf(server.Instructor);

        Ok(await console.SendExerciseMessageAsync("Pause for a teaching point: look at the wind."));
        var direct = await console.SendExerciseMessageAsync("Bea, take the Ops briefing in five minutes.", ops.Ticket!.ParticipantId);
        Ok(direct);
        Assert.Equal("Sent to Bea", direct.Detail);
        Assert.False((await console.SendExerciseMessageAsync("  ")).Succeeded);
        Assert.False((await console.SendExerciseMessageAsync("Hello", Guid.NewGuid())).Succeeded);
        Assert.False((await ConsoleOf(ic).SendExerciseMessageAsync("I'm not the instructor")).Succeeded); // the console is the instructor's

        // The players' streams carry them; nobody in the world hears them (no radio or chat traffic).
        await server.CaughtUpAsync(ic);
        var messages = MirrorOf(ic).Select(e => e.Payload).OfType<ExerciseControlMessage>().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Null(messages[0].ToParticipantId);
        Assert.Equal(ops.Ticket.ParticipantId, messages[1].ToParticipantId);
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is CommsLogged c && c.Text.Contains("teaching point"));

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Timeline, t => t.Text == "Exercise control to Bea: Bea, take the Ops briefing in five minutes.");
    }

    // ---- The media and the politicians ----

    [Fact]
    public async Task Public_confidence_falls_when_the_press_is_ignored_and_rises_with_clear_advice()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var incident = await OpenIncident(ic, IncidentPriority.Critical);

        // Nobody answers the journalists: once they run the story anyway, confidence falls.
        await server.StepAsync(TimeSpan.FromMinutes(17));
        var changes = server.Store.All().Where(e => e.Payload is PublicConfidenceChanged).ToList();
        Assert.All(changes, e => Assert.Equal(EventVisibility.Truth, e.Visibility));
        Assert.Contains(changes, e => e.Payload is PublicConfidenceChanged { Change: < 0 } c && c.Reason.Contains("without a word from command"));
        var low = ((PublicConfidenceChanged)changes[^1].Payload).Confidence;
        Assert.True(low < MediaCoverageSystem.Start);

        // The coverage reaches command as a media report; trainees never see the number.
        await server.CaughtUpAsync(ic);
        Assert.Contains(MirrorOf(ic), e => e.Payload is ReportReceived { Source: ReportSource.Media } r && r.Claim.StartsWith("Coverage:"));
        Assert.DoesNotContain(MirrorOf(ic), e => e.Payload is PublicConfidenceChanged);

        // A statement with clear protective advice wins some back.
        Ok(await C2Of(ic).IssuePublicStatementAsync("People near Barrow Street should stay indoors and close windows and doors.", incident));
        await server.StepAsync(TimeSpan.FromSeconds(10));
        var raised = server.Store.All().Select(e => e.Payload).OfType<PublicConfidenceChanged>().Last();
        Assert.Equal(8, raised.Change);
        Assert.Contains("clear protective advice", raised.Reason);
        Assert.Equal(low + 8, server.Session.Services.GetRequiredService<IWorldSnapshotSource>().Snapshot.PublicConfidence);

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Findings, f => f.Text.StartsWith($"Public confidence ended at {low + 8} of 100"));
    }

    [Fact]
    public async Task Approving_a_politicians_visit_while_the_hazard_is_live_is_a_weakness()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        await OpenIncident(ic, IncidentPriority.Critical);

        // A Critical incident running 25 minutes: someone wants to come and see. Nobody has put the fire out.
        await server.StepAsync(PoliticalPressureSystem.VisitAfterCritical + TimeSpan.FromMinutes(1));
        var request = server.Store.All().Select(e => e.Payload).OfType<ApprovalRequested>().Single(a => a.Subject.StartsWith("Visit to the scene"));
        Assert.Contains(request.RequestedBy, new[] { PoliticalPressureSystem.Td, PoliticalPressureSystem.MinistersOffice });
        await server.CaughtUpAsync(ic);
        Assert.Contains(DeskOf(ic), d => d.Function == "Approvals" && d.Text.Contains("Visit to the scene"));

        Ok(await C2Of(ic).DecideApprovalAsync(request.ApprovalId, true, "Yes, we'll meet her at the cordon."));
        await server.StepAsync(TimeSpan.FromSeconds(10));
        var decided = Assert.Single(server.Store.All().Where(e => e.Payload is PoliticalVisitDecided));
        Assert.Equal(EventVisibility.Truth, decided.Visibility);
        Assert.True(((PoliticalVisitDecided)decided.Payload) is { Approved: true, HazardLive: true });

        // The party turns up at the cordon, wanting to go forward.
        await server.StepAsync(PoliticalPressureSystem.VisitArrivesAfter);
        Assert.Contains(server.Store.All(), e => e.Payload is AlertRaised { Category: AlertCategory.Safety } a && a.Title.EndsWith("at the cordon"));

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("visit to the scene while the hazard was live"));
        Assert.Contains(server.Store.All(), e => e.Payload is PublicConfidenceChanged { Change: < 0 } c && c.Reason.Contains("visit took officers away"));
    }

    // ---- Voice ----

    [Fact]
    public async Task Patched_channels_are_one_talk_group_on_the_voice_radio()
    {
        await using var server = await StartAsync();
        var lou = await server.JoinAsync("Lou", UserRole.Logistics);     // FIRE CMD 1, FIRE TAC 3
        var dee = await server.JoinAsync("Dee", UserRole.Dispatcher);    // FIRE CMD 1, AMB OPS 1, GARDA OPS
        var sam = await server.JoinAsync("Sam", UserRole.Safety);        // FIRE TAC 2, FIRE CMD 1
        var heard = new List<VoiceTalk>();
        var samHeard = new List<VoiceTalk>();
        dee.VoiceStarted += (_, talk) => { lock (heard) heard.Add(talk); };
        sam.VoiceStarted += (_, talk) => { lock (samHeard) samHeard.Add(talk); };

        Ok(await C2Of(lou).PatchChannelsAsync(RadioPlan.FireTac3, RadioPlan.Ambulance));
        for (var i = 0; i < 120 && !server.Cop.Patches.Any(p => p.ActiveFrom is not null); i++)
            await server.Engine.StepAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(server.Cop.Patches, p => p.ActiveFrom is not null);

        // Lou on TAC 3 is heard by Dee, who listens on the ambulance channel it's patched to; Sam, on neither, hears nothing.
        var grant = await lou.VoiceKeyDownAsync(RadioPlan.FireTac3);
        Assert.True(grant.Granted, grant.Reason);
        await Until(() => { lock (heard) return heard.Any(t => t.TransmissionId == grant.TransmissionId); }, TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(samHeard, t => t.TransmissionId == grant.TransmissionId);

        // One talker across the patch.
        var refused = await dee.VoiceKeyDownAsync(RadioPlan.Ambulance);
        Assert.False(refused.Granted);
        Assert.Equal("AMB OPS 1 is patched to FIRE TAC 3, where Lou is talking.", refused.Reason);
        await lou.VoiceKeyUpAsync(grant.TransmissionId!.Value);
        var after = await dee.VoiceKeyDownAsync(RadioPlan.Ambulance);
        Assert.True(after.Granted, after.Reason);
    }

    [Fact]
    public async Task Under_emergency_traffic_only_those_working_the_emergency_key_up()
    {
        await using var server = await StartAsync();
        var lou = await server.JoinAsync("Lou", UserRole.Logistics);
        var sam = await server.JoinAsync("Sam", UserRole.Safety);
        Ok(await C2Of(sam).DeclareEmergencyTrafficAsync(RadioPlan.FireCommand, true));
        await Until(() => server.Cop.Channels.Single(c => c.Info.Id == RadioPlan.FireCommand).EmergencyTraffic, TimeSpan.FromSeconds(10));

        var refused = await lou.VoiceKeyDownAsync(RadioPlan.FireCommand);
        Assert.False(refused.Granted);
        Assert.StartsWith("Emergency traffic on", refused.Reason);
        var safety = await sam.VoiceKeyDownAsync(RadioPlan.FireCommand);
        Assert.True(safety.Granted, safety.Reason);
        await sam.VoiceKeyUpAsync(safety.TransmissionId!.Value);

        Ok(await C2Of(sam).DeclareEmergencyTrafficAsync(RadioPlan.FireCommand, false));
        await Until(() => !server.Cop.Channels.Single(c => c.Info.Id == RadioPlan.FireCommand).EmergencyTraffic, TimeSpan.FromSeconds(10));
        for (var i = 0; i < 40 && server.Cop.Channels.Single(c => c.Info.Id == RadioPlan.FireCommand).IsBusy(server.Engine.SimTime); i++)
            await server.Engine.StepAsync(TimeSpan.FromSeconds(1));
        var lifted = await lou.VoiceKeyDownAsync(RadioPlan.FireCommand);
        Assert.True(lifted.Granted, lifted.Reason);
    }

    // ---- Observer assessment ----

    [Fact]
    public async Task Observers_rate_competency_areas_and_the_review_scores_each_area()
    {
        await using var server = await StartAsync();
        var olly = await server.JoinAsync("Olly", UserRole.Observer);
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var alex = ic.Ticket!.ParticipantId;
        Ok(await AssessorOf(olly).RecordNoteAsync(alex, "Prioritised the casualties", AssessmentRating.Competent, area: Competency.DecisionMaking));
        Ok(await AssessorOf(olly).RecordNoteAsync(alex, "Slow to commit the second alarm", AssessmentRating.Developing, area: Competency.DecisionMaking));
        Ok(await AssessorOf(server.Instructor).RecordNoteAsync(alex, "No read-back asked for", AssessmentRating.NotYetCompetent, area: Competency.Communication));
        Ok(await AssessorOf(olly).RecordNoteAsync(alex, "Calm on the radio", AssessmentRating.Competent)); // overall: in the score, not an area

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Equal(new[]
        {
            (Competency.DecisionMaking, 2, 75, AssessmentRating.Competent),
            (Competency.Communication, 1, 0, AssessmentRating.NotYetCompetent),
        }, report.Competencies.Select(c => (c.Area, c.Ratings, c.Score, c.Level)).OrderBy(c => c.Area == Competency.Communication));
        Assert.Contains(report.Notes, n => n.Area == Competency.Communication && n.Note == "No read-back asked for");
        Assert.Contains(report.Timeline, t => t.Text.Contains("[Decision making]: Competent"));
        Assert.Contains("Competency,", AarExport.Csv(report));
        Assert.Contains("<h2>Competencies</h2>", AarExport.Html(report, new AarSignOff("Alex", "Incident Commander", "Olly", null)));
    }

    // ---- Workspaces, finalised ----

    [Fact]
    public void Every_role_has_a_finished_workspace_a_desk_and_a_radio()
    {
        foreach (var role in Enum.GetValues<UserRole>())
        {
            var workspace = RoleWorkspaces.For(role);
            Assert.Contains(workspace.Focus, workspace.Panels);
            Assert.Contains(workspace.Board, workspace.Boards);
            Assert.Contains(WorkspacePanel.Desk, workspace.Panels);
            Assert.Contains(WorkspacePanel.Session, workspace.Panels);
            Assert.NotEmpty(VoiceChannels.For(role));
            if (Roles.IsPlayer(role))
                Assert.NotEmpty(PermissionMatrix.FunctionsOf(role, _ => true)); // every playing role has work of its own
        }
        Assert.Equal(WorkspacePanel.Desk, RoleWorkspaces.For(UserRole.IncidentCommander).Focus);
        Assert.Equal(WorkspacePanel.Assess, RoleWorkspaces.For(UserRole.Observer).Focus);
    }
}
