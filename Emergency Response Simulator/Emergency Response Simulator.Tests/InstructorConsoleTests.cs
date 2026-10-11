using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Instructor;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 11 M4: the instructor console, over a real in-process server running Barrow Street.</summary>
public class InstructorConsoleTests
{
    private static IC2Service C2Of(SimulationConnection client) => RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);

    private static IInstructorService ConsoleOf(SimulationConnection client) =>
        RemoteProxy.Create<IInstructorService>(client, SimulationHubProtocol.Services.Instructor);

    private static async Task<int> InjectNumber(IInstructorService console, string startsWith) =>
        (await console.ListInjectsAsync()).Single(i => i.Description.StartsWith(startsWith, StringComparison.Ordinal)).Number;

    private static void Ok(CommandResult result) => Assert.True(result.Succeeded, result.Error);

    /// <summary>
    /// The exit criterion: the instructor runs Barrow Street live: fires an early inject, holds another, injects a wind
    /// shift, approves mutual aid as the Regional Duty Officer, and phones the IC as a journalist. All of it appears
    /// correctly in the review, and none of it in the trainee's stream except what reached them in the world.
    /// </summary>
    [Fact]
    public async Task The_instructor_runs_barrow_street_live_and_the_review_shows_it_all()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var console = ConsoleOf(server.Instructor);

        // An early inject (scripted for minute 7) and a held one (minute 8.5).
        var neighbour = await InjectNumber(console, "Neighbour warns of chemical storage");
        var evacuation = await InjectNumber(console, "Garda inspector asks for authority to evacuate");
        Ok(await console.FireInjectAsync(neighbour));
        Ok(await console.HoldInjectAsync(evacuation));
        Assert.False((await console.FireInjectAsync(neighbour)).Succeeded); // once only

        // A wind shift from the library.
        Ok(await console.FireAdHocAsync("wind-shift", new Dictionary<string, string> { ["from"] = "NW", ["speed"] = "11" }));

        // The Regional Duty Officer: taken over, so the mutual-aid decision waits for the instructor.
        Ok(await console.TakeOverAsync(AiRoles.RegionalDutyOfficer));
        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        var request = await C2Of(ic).RequestResourcesAsync(incident, ResourceRequestKind.MutualAid, UnitType.Engine, 2, "Second alarm");
        Ok(request);
        await server.StepAsync(TimeSpan.FromMinutes(4)); // well past the 2.5 minutes the agreement allows
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is ResourceRequestDecided);
        var waiting = Assert.Single(await console.ListWaitingAsync());
        Assert.Equal(AiRoleItemKind.ResourceRequest, waiting.Kind);
        Assert.Equal("Regional Duty Officer", waiting.Role);
        Ok(await console.DecideAsync(new AiRoleDecision(waiting.Id, Approve: true, Quantity: 2, "Kildare sending two pumps")));

        // A journalist rings the IC.
        Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Journalist, "RTÉ News", "Is there a danger to people living in Ringsend?", IncidentId: incident)));

        await server.StepAsync(TimeSpan.FromMinutes(6)); // past the held inject's minute 8.5
        var stored = server.Store.All();
        var decided = (ResourceRequestDecided)stored.Single(e => e.Payload is ResourceRequestDecided).Payload;
        Assert.True(decided.Approved);
        Assert.Equal("Regional Duty Officer", decided.DecidedBy);
        Assert.Contains(stored, e => e.Payload is MediaEnquiryReceived { Outlet: "RTÉ News" });
        Assert.DoesNotContain(stored, e => e.Payload is ApprovalRequested { Subject: "Authority to evacuate Barrow Street apartments" }); // held
        Assert.Contains(stored, e => e.Payload is ReportReceived r && r.Claim.Contains("chlorine-based cleaning chemicals"));            // fired early
        Assert.Contains(stored, e => e.Payload is WeatherChanged { WindFromDegrees: 315, WindSpeedMps: 11 });

        // Everything the instructor did is ground truth, stamped with them; the trainee sees only the world's side of it.
        var instructorId = server.Instructor.Ticket!.ParticipantId;
        Assert.All(stored.Where(e => e.Payload is InjectFired { Number: 0 } or InjectHeld or InstructorRolePlay or AiRoleTakenOver), e =>
        {
            Assert.Equal(EventVisibility.Truth, e.Visibility);
            Assert.Equal(instructorId, e.ActorId);
        });
        await server.CaughtUpAsync(ic);
        var traineeSees = ic.Mirror.ReadAsync(ic.Session!.SessionId).ToBlockingEnumerable().ToList();
        Assert.DoesNotContain(traineeSees, e => e.Payload is InjectFired or InjectHeld or InstructorRolePlay or AiRoleTakenOver);
        Assert.Contains(CopOf(ic).MediaEnquiries, m => m.Outlet == "RTÉ News");

        // The review.
        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        var actions = report.Instructor;
        Assert.Contains(actions, a => a.Kind == "Inject fired early" && a.Text.Contains("Neighbour warns of chemical storage"));
        Assert.Contains(actions, a => a.Kind == "Inject held" && a.Text.Contains("authority to evacuate"));
        Assert.Contains(actions, a => a.Kind == "Ad-hoc inject" && a.Text.Contains("Wind shifts to NW 11 m/s"));
        Assert.Contains(actions, a => a.Kind == "Took over" && a.Text == "Regional Duty Officer");
        Assert.Contains(actions, a => a.Kind == "Decision as Regional Duty Officer" && a.Text.StartsWith("Approved"));
        Assert.Contains(actions, a => a.Kind == "Role-play (journalist)" && a.Text.Contains("RTÉ News"));
        Assert.All(actions.Where(a => a.Kind != "Inject (on time)"), a => Assert.Contains("Ines", a.By));
        Assert.DoesNotContain(actions, a => a.Text.Contains("authority to evacuate") && a.Kind.StartsWith("Inject (on time)"));

        // In the timeline, what the role-play produced in the world is marked as such.
        Assert.Contains(report.Timeline, t => t.RolePlay && !t.IsTruth && t.Actor == "Instructor role-play as Regional Duty Officer" && t.Text.Contains("approved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Timeline, t => t.RolePlay && t.Actor == "Instructor role-play as RTÉ News" && !t.IsTruth);
        // The trainee's own request isn't.
        Assert.DoesNotContain(report.Timeline, t => t.RolePlay && t.IsDecision);
    }

    [Fact]
    public async Task Only_the_instructor_has_the_console()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var refused = await Assert.ThrowsAsync<HubException>(() => ConsoleOf(ic).ListInjectsAsync());
        Assert.Contains("instructor", refused.Message);
        var viaCommand = await ConsoleOf(ic).TakeOverAsync(AiRoles.Media);
        Assert.False(viaCommand.Succeeded);
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is AiRoleTakenOver);
    }

    [Fact]
    public async Task Held_injects_wait_released_ones_fire_and_skipped_ones_never_do()
    {
        await using var server = await StartAsync();
        var console = ConsoleOf(server.Instructor);
        var rumour = await InjectNumber(console, "Social media rumour");   // 2.5 min
        var police = await InjectNumber(console, "Passing police patrol"); // 5 min
        Ok(await console.HoldInjectAsync(rumour));
        Ok(await console.SkipInjectAsync(police));
        Assert.False((await console.HoldInjectAsync(police)).Succeeded);
        Assert.False((await console.FireInjectAsync(police)).Succeeded);

        await server.StepAsync(TimeSpan.FromMinutes(6));
        var script = await console.ListInjectsAsync();
        Assert.Equal(InjectStatus.Held, script.Single(i => i.Number == rumour).Status);
        Assert.Equal(InjectStatus.Skipped, script.Single(i => i.Number == police).Status);
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is ReportReceived r && r.SourceName == "Social media post");

        // Released after its time: it goes off at the next tick.
        Ok(await console.ReleaseInjectAsync(rumour));
        await server.StepAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(server.Store.All(), e => e.Payload is ReportReceived r && r.SourceName == "Social media post");
        Assert.Equal(InjectStatus.Fired, (await console.ListInjectsAsync()).Single(i => i.Number == rumour).Status);
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is ReportReceived r && r.SourceName == "Police 14");

        // Scripted injects that went off on time are recorded too, by the scenario rather than the instructor.
        var onTime = server.Store.All().Where(e => e.Payload is InjectFired { Early: false, Number: > 0 }).ToList();
        Assert.NotEmpty(onTime);
        Assert.All(onTime.Where(e => e.Source == EventSources.Scenario), e => Assert.Null(e.ActorId));
    }

    [Fact]
    public async Task Ad_hoc_injects_check_what_they_are_given()
    {
        await using var server = await StartAsync();
        var console = ConsoleOf(server.Instructor);
        Assert.NotEmpty(await console.ListTemplatesAsync());
        Assert.False((await console.FireAdHocAsync("no-such-thing")).Succeeded);
        Assert.False((await console.FireAdHocAsync("wind-shift", new Dictionary<string, string> { ["from"] = "Up" })).Succeeded);
        Assert.False((await console.FireAdHocAsync("wind-shift", new Dictionary<string, string> { ["speed"] = "fast" })).Succeeded);
        Assert.False((await console.FireAdHocAsync("breakdown", new Dictionary<string, string> { ["callsign"] = "Engine 999" })).Succeeded);
        Ok(await console.FireAdHocAsync("breakdown", new Dictionary<string, string> { ["callsign"] = "Engine 12", ["fault"] = "gearbox" }));
        Assert.Contains(server.Store.All(), e => e.Payload is UnitBrokeDown { Fault: "gearbox" });
        Ok(await console.FireAdHocAsync("rumour")); // all defaults
    }

    [Fact]
    public async Task Taking_over_an_agency_puts_its_answers_in_the_instructors_hands_until_handed_back()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var console = ConsoleOf(server.Instructor);
        await server.CaughtUpAsync(ic);
        var north = CopOf(ic).Agencies.Single(a => a.Name == "City Fire Brigade, North District").Id;
        var key = AiRoles.Agency(north);
        var roles = await console.ListAiRolesAsync();
        Assert.Contains(roles, r => r.Key == key && r.System.Contains("AgencyAiSystem") && !r.TakenOver);
        Assert.Contains(roles, r => r.Key == AiRoles.RegionalDutyOfficer && r.System == "CommandResponseSystem");

        Ok(await console.TakeOverAsync(key));
        Assert.False((await console.TakeOverAsync(key)).Succeeded);
        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        Ok(await C2Of(ic).RequestAgencyAssistanceAsync(north, incident, 2, "Second engine at Barrow Street"));
        await server.StepAsync(TimeSpan.FromMinutes(3));
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is AgencyAssistanceDecided);

        var item = Assert.Single(await console.ListWaitingAsync());
        Assert.Equal(AiRoleItemKind.Assistance, item.Kind);
        Assert.Equal(1, (await console.ListAiRolesAsync()).Single(r => r.Key == key).Waiting);
        Ok(await console.DecideAsync(new AiRoleDecision(item.Id, Approve: true, Quantity: 1)));
        var granted = (AgencyAssistanceDecided)server.Store.All().Single(e => e.Payload is AgencyAssistanceDecided).Payload;
        Assert.Equal(1, granted.Granted);
        Assert.Empty(await console.ListWaitingAsync());

        // Handed back: the agency answers by itself again.
        Ok(await console.HandBackAsync(key));
        Ok(await C2Of(ic).RequestAgencyAssistanceAsync(north, incident, 1, "And another"));
        await server.StepAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(2, server.Store.All().Count(e => e.Payload is AgencyAssistanceDecided));
        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Instructor, a => a.Kind == "Handed back");
    }

    [Fact]
    public async Task Outside_organisations_answer_in_the_instructors_words()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var console = ConsoleOf(server.Instructor);
        Ok(await console.TakeOverAsync(AiRoles.OutsideOrganisations));
        Ok(await C2Of(ic).NotifyAsync("St. James's Hospital", "Expect up to 10 casualties from Barrow Street"));
        await server.StepAsync(TimeSpan.FromMinutes(4));
        Assert.DoesNotContain(server.Store.All(), e => e.Payload is NotificationAnswered);
        var item = Assert.Single(await console.ListWaitingAsync());
        Assert.False((await console.DecideAsync(new AiRoleDecision(item.Id, true))).Succeeded); // a reply needs words
        Ok(await console.DecideAsync(new AiRoleDecision(item.Id, true, Text: "We can take six; the burns unit is full.")));
        await server.StepAsync(TimeSpan.FromSeconds(30));
        var answer = (NotificationAnswered)server.Store.All().Single(e => e.Payload is NotificationAnswered).Payload;
        Assert.Equal("St. James's Hospital: We can take six; the burns unit is full.", answer.Reply);
    }

    [Fact]
    public async Task Role_play_on_the_radio_and_the_999_line_reaches_command_and_is_marked_in_the_review()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var console = ConsoleOf(server.Instructor);
        Assert.False((await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Radio, "", "Hello"))).Succeeded);
        var radio = await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Radio, "Engine 4", "We can see flames on the roof of the warehouse"));
        Ok(radio);
        Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Caller, "Mrs. Kelly", "My son is in the building next to the fire, he's not answering his phone")));
        Ok(await console.RolePlayAsync(new RolePlayRequest(RolePlayKind.Official, "Port Duty Manager", "Request authority to close the basin to all boats. Please advise.")));
        await server.StepAsync(TimeSpan.FromMinutes(3));

        var stored = server.Store.All();
        Assert.Contains(stored, e => e.Payload is CallReceived { Caller: "Mrs. Kelly" });
        Assert.Contains(stored, e => e.Payload is ApprovalRequested { RequestedBy: "Port Duty Manager", Subject: "Request authority to close the basin to all boats" });
        // Like any transmission it goes out on a real channel: heard (maybe broken up, if the channel was busy) or lost.
        var heard = stored.Any(e => e.Payload is CommsLogged c && c.MessageId == radio.EntityId);
        Assert.True(heard || stored.Any(e => e.Payload is TransmissionLost l && l.MessageId == radio.EntityId));

        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        Assert.Contains(report.Timeline, t => t.RolePlay && t.Actor == "Instructor role-play as Mrs. Kelly");
        if (heard) Assert.Contains(report.Timeline, t => t.RolePlay && t.Actor == "Instructor role-play as Engine 4");
        Assert.Contains(report.Timeline, t => t.RolePlay && t.Actor == "Instructor role-play as Port Duty Manager");
        Assert.Equal(3, report.Instructor.Count(a => a.Kind.StartsWith("Role-play")));
    }

    [Fact]
    public async Task The_player_monitor_shows_activity_idleness_alerts_channels_and_bookmarks()
    {
        await using var server = await StartAsync();
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        await server.JoinAsync("Olly", UserRole.Observer);
        var incident = (await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).EntityId!.Value;
        await C2Of(ic).UpdateIncidentAsync(incident, priority: IncidentPriority.Critical);
        Ok(await ConsoleOf(server.Instructor).BookmarkAsync("Alex opened the incident quickly", ic.Ticket!.ParticipantId));
        await server.StepAsync(TimeSpan.FromMinutes(6));

        await server.CaughtUpAsync(server.Instructor);
        var events = server.Instructor.Mirror.ReadAsync(server.Session.Id).ToBlockingEnumerable().ToList();
        var monitor = PlayerMonitor.Build(events, CopOf(server.Instructor), server.Engine.SimTime);
        var alex = monitor.Players.Single(p => p.Name == "Alex");
        var bea = monitor.Players.Single(p => p.Name == "Bea");
        Assert.Equal(2, alex.Commands);
        Assert.True(alex.Idle); // nothing for six minutes
        Assert.Contains("INC-", alex.LastAction);
        Assert.Equal(0, bea.Commands);
        Assert.True(bea.Idle);
        Assert.False(monitor.Players.Single(p => p.Name == "Olly").Idle); // watching isn't idling
        Assert.DoesNotContain(monitor.Players, p => p.Role == UserRole.Instructor);
        Assert.Equal(2, monitor.Feed.Count(f => f.Who.StartsWith("Alex")));
        Assert.NotEmpty(monitor.Channels);
        Assert.Contains(monitor.Bookmarks, b => b.Note == "Alex opened the incident quickly");

        // A fresh command clears the idle flag.
        await C2Of(ops).UpdateIncidentAsync(incident, casualtiesReported: 3);
        await server.CaughtUpAsync(server.Instructor);
        events = server.Instructor.Mirror.ReadAsync(server.Session.Id).ToBlockingEnumerable().ToList();
        Assert.False(PlayerMonitor.Build(events, CopOf(server.Instructor), server.Engine.SimTime).Players.Single(p => p.Name == "Bea").Idle);
    }
}
