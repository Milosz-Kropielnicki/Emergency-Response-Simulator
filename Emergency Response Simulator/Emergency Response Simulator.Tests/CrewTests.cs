using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Crews;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 8: human factors and crew management.</summary>
public class CrewTests
{
    private static readonly GeoPoint Dublin = TestHarness.Dublin;

    /// <summary>Runs scripted actions inside a tick, at or after the given time from the start.</summary>
    private sealed class Script(params (TimeSpan At, Action<SimulationContext> Act)[] steps) : ISimulationSystem
    {
        private readonly List<(TimeSpan At, Action<SimulationContext> Act)> _steps = [.. steps];
        public int Order => 50;

        public void Update(SimulationContext context)
        {
            foreach (var step in _steps.Where(s => context.SimTime - TestHarness.Start >= s.At).ToList())
            {
                _steps.Remove(step);
                step.Act(context);
            }
        }
    }

    /// <summary>Crew options without random mishaps, so tests see only what they set up.</summary>
    private static CrewOptions Options(Action<CrewOptions>? configure = null)
    {
        var options = new CrewOptions { DistressPerHour = 0, Collapses = false };
        configure?.Invoke(options);
        return options;
    }

    private static async Task<Guid> CrewAsync(TestHarness harness, string callsign, UnitType type = UnitType.Engine, double onShiftHours = 2,
        string[]? capabilities = null, int size = 5)
    {
        var id = Guid.NewGuid();
        capabilities ??= ["BreathingApparatus"];
        await harness.Perceived(new UnitRegistered(id, callsign, type, null, Dublin, "Station 3", size, capabilities));
        await harness.Perceived(new CrewRostered(id, CrewRoster.Generate(callsign, type, size, capabilities), TimeSpan.FromHours(onShiftHours),
            CrewRoster.ShiftLength(type)));
        return id;
    }

    /// <summary>Dispatches the units and waits until they are all working at the scene.</summary>
    private static async Task AtSceneAsync(TestHarness harness, Guid incident, params Guid[] units)
    {
        foreach (var unit in units)
            Assert.True((await harness.C2.DispatchAsync(unit, incident)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => units.All(u => harness.World.Units[u].Phase == ResponsePhase.Operating), TimeSpan.FromMinutes(5)));
    }

    private static async Task<List<T>> PayloadsAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Select(e => e.Payload).OfType<T>().ToList();

    private static Task FireAsync(TestHarness harness) =>
        harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, Severity: 0.9, ActualCasualties: 0));

    // ---- Rosters and qualifications ----

    [Fact]
    public void Rosters_are_the_same_every_session_and_follow_the_unit()
    {
        var engine = CrewRoster.Generate("Engine 4", UnitType.Engine, 5, ["BreathingApparatus"]);
        Assert.Equal(engine.Select(m => m.Name), CrewRoster.Generate("Engine 4", UnitType.Engine, 5, ["BreathingApparatus"]).Select(m => m.Name));
        Assert.Equal(5, engine.Count);
        Assert.Equal(CrewRole.Officer, engine[0].Role);
        Assert.True(engine.Count(m => m.Qualifications.Contains(Qualifications.BreathingApparatus)) >= 4);
        Assert.Equal(5, engine.Select(m => m.Name.Split(' ')[^1]).Distinct().Count());
        Assert.NotEqual(engine.Select(m => m.Name), CrewRoster.Generate("Engine 4", UnitType.Engine, 5, ["BreathingApparatus"], salt: 1).Select(m => m.Name));

        var hazmat = CrewRoster.Generate("Hazmat 2", UnitType.Hazmat, 6, ["Hazmat"], lapsed: [Qualifications.Hazmat]);
        Assert.Equal(4, hazmat.Count(m => m.Qualifications.Contains(Qualifications.Hazmat)));
        Assert.Single(hazmat, m => m.Lapsed?.Contains(Qualifications.Hazmat) == true);

        var ambulance = CrewRoster.Generate("Ambulance 21", UnitType.AmbulanceAls, 2, ["ALS"], lapsed: [Qualifications.Als]);
        Assert.Equal(CrewRole.Paramedic, ambulance[0].Role);
        Assert.DoesNotContain(ambulance, m => m.Qualifications.Contains(Qualifications.Als));
        Assert.All(ambulance, m => Assert.Contains(Qualifications.Bls, m.Qualifications));
    }

    [Theory]
    [InlineData("Decontaminate the casualties at the cordon", Qualifications.Hazmat, 2)]
    [InlineData("Plug the leaking drum", Qualifications.Hazmat, 2)]
    [InlineData("Water rescue from the canal at Grand Canal Dock", Qualifications.Swiftwater, 2)]
    [InlineData("Shore up the collapsed wall and search the void", Qualifications.Usar, 2)]
    [InlineData("Interior attack on the first floor", Qualifications.BreathingApparatus, 2)]
    [InlineData("Paramedic to the casualty clearing station", Qualifications.Als, 1)]
    public void Orders_say_what_they_need(string order, string qualification, int members)
    {
        var needed = Qualifications.RequiredFor(order);
        Assert.NotNull(needed);
        Assert.Equal(qualification, needed.Qualification);
        Assert.Equal(members, needed.Members);
        Assert.Null(Qualifications.RequiredFor("Set up exposure protection on the east side"));
    }

    [Fact]
    public void The_plan_flags_a_task_given_to_a_crew_not_qualified_for_it()
    {
        var incident = new Incident { Id = Guid.NewGuid(), Number = "INC-00241", Type = IncidentType.HazmatRelease, Location = Dublin.ToPoint() };
        var crew = new Crew { UnitId = Guid.NewGuid() };
        crew.Members.AddRange(CrewRoster.Generate("Engine 14", UnitType.Engine, 4, ["BreathingApparatus"])
            .Select(m => new CrewMember { Id = m.MemberId, Name = m.Name, Role = m.Role, Qualifications = m.Qualifications }));
        var unit = new Unit { Id = crew.UnitId, Name = "Engine 14", Callsign = "Engine 14", Crew = crew, AssignedIncidentId = incident.Id };
        var assignment = new IapAssignment { UnitId = unit.Id, Callsign = "Engine 14", Assignment = "Plug the leaking drum in unit 4" };

        Assert.Equal("has no hazardous materials technician qualified (needs 2)", IapCompliance.ViabilityProblem(assignment, incident, unit));
        assignment.Assignment = "Interior attack on the first floor";
        Assert.Null(IapCompliance.ViabilityProblem(assignment, incident, unit));
    }

    [Fact]
    public async Task Units_without_a_crew_list_get_one()
    {
        var harness = new TestHarness(new CrewSystem(Options()));
        var unit = await harness.RegisterUnitAsync("Relief Engine 9");

        await harness.RunAsync(TimeSpan.FromSeconds(10));

        var crew = harness.Cop.FindUnit(unit)!.Crew;
        Assert.NotNull(crew);
        Assert.Equal(5, crew.Members.Count);
        Assert.True(crew.ShiftEnd > TestHarness.Start);
        Assert.Equal(5, harness.World.Units[unit].Crew.OnDuty);
    }

    [Fact]
    public async Task A_crew_declines_a_task_it_has_nobody_qualified_for()
    {
        var harness = new TestHarness(cop => [new CommandResponseSystem(), new CrewSystem(Options()), new AttentionMonitor(cop, new AttentionOptions())]);
        var incident = await harness.CreateIncidentAsync();
        var engine = await CrewAsync(harness, "Engine 14");
        var hazmat = await CrewAsync(harness, "Hazmat 2", UnitType.Hazmat, capabilities: ["Hazmat"], size: 6);
        await harness.C2.DispatchAsync(engine, incident);
        await harness.C2.DispatchAsync(hazmat, incident);

        var refused = (await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Decontaminate the casualties at the cordon")).EntityId;
        var accepted = (await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, hazmat, "Decontaminate the casualties at the cordon")).EntityId;
        await harness.RunAsync(TimeSpan.FromMinutes(1));

        var declined = harness.Cop.Orders.Single(o => o.Id == refused);
        Assert.Equal(OrderStatus.Declined, declined.Status);
        Assert.Contains("hazardous materials", declined.DeclineReason);
        Assert.Equal(OrderStatus.Acknowledged, harness.Cop.Orders.Single(o => o.Id == accepted).Status);
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Engine 14 can't do it");
        Assert.Contains("Decontaminate the casualties at the cordon", harness.World.Units[hazmat].Crew.Tasks);
    }

    // ---- Fatigue and stress ----

    [Fact]
    public void Tired_stressed_crews_are_slower_make_more_mistakes_and_do_less()
    {
        var fresh = new WorldUnit { Callsign = "Engine 4", Type = UnitType.Engine };
        var spent = new WorldUnit { Callsign = "Engine 7", Type = UnitType.Engine };
        foreach (var unit in new[] { fresh, spent })
            unit.Crew.Roster(CrewRoster.Generate(unit.Callsign, unit.Type, 5, ["BreathingApparatus"]), TestHarness.Start, TimeSpan.FromHours(10), TestHarness.Start);
        foreach (var member in spent.Crew.Members)
        {
            member.Fatigue = 0.9;
            member.Stress = 0.8;
        }
        spent.Crew.Members[^1].State = ResponderState.StoodDown;

        Assert.True(CrewFactors.SlowFactor(spent) > CrewFactors.SlowFactor(fresh) * 1.5);
        Assert.Equal(0, CrewFactors.ErrorRate(fresh));
        Assert.True(CrewFactors.ErrorRate(spent) > 0.15);
        Assert.True(CrewFactors.Effectiveness(spent, TestHarness.Start) < CrewFactors.Effectiveness(fresh, TestHarness.Start) * 0.5);

        spent.Crew.Withdrawn = true;
        fresh.Crew.Withdrawn = true;
        Assert.True(CrewFactors.Effectiveness(fresh, TestHarness.Start) < 0.7);
        Assert.Equal(1, CrewFactors.SlowFactor(new WorldUnit { Callsign = "No crew model" }));
    }

    [Fact]
    public async Task Working_crews_tire_and_say_so_late()
    {
        var harness = new TestHarness(new HazardSystem(), new UnitResponseSystem(), new CrewSystem(Options(o =>
        {
            o.InteriorFatigue = 1.2;
            o.OtherWorkFatigue = 0.8; // in case the crew puts the fire out first
        })));
        var incident = await harness.CreateIncidentAsync();
        var engine = await CrewAsync(harness, "Engine 4", onShiftHours: 4);
        await FireAsync(harness);
        await AtSceneAsync(harness, incident, engine);
        var before = harness.World.Units[engine].Crew.Fatigue;

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.FindUnit(engine)!.Crew!.Condition == CrewCondition.Tired, TimeSpan.FromMinutes(40)));

        var crew = harness.World.Units[engine].Crew;
        Assert.True(crew.Fatigue >= 0.6, $"fatigue {crew.Fatigue:F2}");
        Assert.True(crew.Fatigue > before + 0.2);
        // The truth crossed into "tired" before the crew admitted it.
        var events = await harness.EventsAsync();
        var reallyTired = events.First(e => e.Payload is CrewWelfareChanged { Band: "tired" });
        var admitted = events.First(e => e.Payload is CrewConditionReported { Condition: CrewCondition.Tired });
        Assert.True(admitted.SimTime > reallyTired.SimTime);
        Assert.Contains("tired", harness.Cop.FindUnit(engine)!.Crew!.ConditionNote);
    }

    [Fact]
    public async Task Rehab_rests_a_crew_and_puts_it_back_to_work()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new CrewSystem(Options(o => o.RehabTime = TimeSpan.FromMinutes(10))));
        var incident = await harness.CreateIncidentAsync();
        var engine = await CrewAsync(harness, "Engine 4", onShiftHours: 9);
        Assert.False((await harness.C2.SendToRehabAsync(engine)).Succeeded); // not at a scene yet
        await AtSceneAsync(harness, incident, engine);
        var before = harness.World.Units[engine].Crew.Fatigue;

        Assert.True((await harness.C2.SendToRehabAsync(engine)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(ResponsePhase.Rehab, harness.World.Units[engine].Phase);
        Assert.True(harness.Cop.FindUnit(engine)!.Crew!.InRehab);
        Assert.Equal(UnitStatus.OnScene, harness.Cop.FindUnit(engine)!.Status);
        Assert.False((await harness.C2.SendToRehabAsync(engine)).Succeeded); // already there

        Assert.True(await harness.RunUntilAsync(() => harness.World.Units[engine].Phase == ResponsePhase.Operating, TimeSpan.FromMinutes(12)));
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.True(harness.World.Units[engine].Crew.Fatigue < before);
        var crew = harness.Cop.FindUnit(engine)!.Crew!;
        Assert.False(crew.InRehab);
        Assert.NotNull(crew.LastRehabEnded);
        Assert.Equal(UnitStatus.Operating, harness.Cop.FindUnit(engine)!.Status);
        Assert.Single(await PayloadsAsync<CrewRehabEnded>(harness));
    }

    [Fact]
    public async Task An_acute_stress_reaction_takes_someone_off_the_crew()
    {
        var harness = new TestHarness(cop => [new CrewSystem(Options()), new AttentionMonitor(cop, new AttentionOptions()),
            new Script((TimeSpan.FromSeconds(10), context => context.World.Units.Values.Single().Crew.Members[2].Stress = 0.95))]);
        var engine = await CrewAsync(harness, "Engine 4");

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.FindUnit(engine)!.Crew!.OnDuty == 4, TimeSpan.FromMinutes(20)));
        await harness.RunAsync(TimeSpan.FromSeconds(5));

        var crew = harness.Cop.FindUnit(engine)!.Crew!;
        Assert.Equal(MemberStatus.StoodDown, crew.Members[2].Status);
        Assert.Contains("acute stress reaction", (await PayloadsAsync<CrewMemberStoodDown>(harness)).Single().Reason);
        Assert.Contains(harness.Cop.Alerts, a => a.Title == $"Engine 4: {crew.Members[2].Name} stood down");
    }

    [Fact]
    public async Task Peer_support_waits_for_the_crew_to_come_off_the_line()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new CrewSystem(Options(o => o.PeerSupportTravelTime = TimeSpan.FromMinutes(2))),
            new Script((TimeSpan.FromSeconds(5), context =>
            {
                foreach (var member in context.World.Units.Values.Single().Crew.Members) member.Stress = 0.7;
            })));
        var incident = await harness.CreateIncidentAsync();
        var engine = await CrewAsync(harness, "Engine 4");
        await AtSceneAsync(harness, incident, engine);

        Assert.True((await harness.C2.ArrangePeerSupportAsync(engine)).Succeeded);
        Assert.False((await harness.C2.ArrangePeerSupportAsync(engine)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(4));
        Assert.Empty(await PayloadsAsync<PeerSupportGiven>(harness));

        await harness.C2.SendToRehabAsync(engine);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.FindUnit(engine)!.Crew!.PeerSupportGivenAt is not null, TimeSpan.FromMinutes(2)));
        Assert.True(harness.World.Units[engine].Crew.Stress < 0.4);
    }

    // ---- Shifts, relief and handover ----

    [Fact]
    public async Task Crews_at_their_station_change_watch_at_shift_end()
    {
        var harness = new TestHarness(new CrewSystem(Options()));
        var engine = await CrewAsync(harness, "Engine 4", onShiftHours: 9.9);
        var outgoing = harness.Cop.FindUnit(engine)!.Crew!.Members.Select(m => m.Name).ToList();

        await harness.RunAsync(TimeSpan.FromMinutes(10));

        var relieved = Assert.Single(await PayloadsAsync<CrewRelieved>(harness));
        Assert.True(relieved.Briefed);
        var crew = harness.Cop.FindUnit(engine)!.Crew!;
        Assert.NotEqual(outgoing, crew.Members.Select(m => m.Name));
        Assert.False(crew.PastShiftEnd(TestHarness.Start + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task A_rushed_handover_loses_what_the_old_crew_knew_and_a_briefed_one_does_not()
    {
        var harness = new TestHarness(new CommandResponseSystem(), new CrewSystem(Options(o => o.ReliefTravelTime = TimeSpan.FromMinutes(1))));
        var incident = await harness.CreateIncidentAsync();
        var rushed = await CrewAsync(harness, "Engine 4");
        var briefed = await CrewAsync(harness, "Engine 12");
        foreach (var unit in new[] { rushed, briefed })
        {
            await harness.C2.DispatchAsync(unit, incident);
            Assert.True((await harness.C2.AssignChannelAsync(unit, RadioPlan.FireTac2)).Succeeded);
            await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, unit, "Cover the east side with a ground monitor");
        }
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Contains("Cover the east side with a ground monitor", harness.World.Units[rushed].Crew.Tasks);

        Assert.True((await harness.C2.RequestReliefAsync(rushed, fullBriefing: false)).Succeeded);
        Assert.True((await harness.C2.RequestReliefAsync(briefed, fullBriefing: true)).Succeeded);
        Assert.False((await harness.C2.RequestReliefAsync(rushed, fullBriefing: true)).Succeeded); // one is already coming
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Crews.Count(c => c.RelievedAt is not null) == 2, TimeSpan.FromMinutes(15)));

        // The rushed crew came up on its normal channel; command still thinks it is on the tactical one.
        Assert.Equal(RadioPlan.FireCommand, harness.World.Units[rushed].Channel);
        Assert.Equal(RadioPlan.FireTac2, harness.Cop.FindUnit(rushed)!.Channel);
        var lost = Assert.Single(await PayloadsAsync<HandoverInformationLost>(harness));
        Assert.Equal(rushed, lost.UnitId);
        Assert.Contains($"to work on {RadioPlan.FireTac2}", lost.Items);

        Assert.Equal(RadioPlan.FireTac2, harness.World.Units[briefed].Channel);
        Assert.Contains("Cover the east side with a ground monitor", harness.World.Units[briefed].Crew.Tasks);
        Assert.Empty(harness.World.Units[briefed].Crew.ForgottenTasks);
    }

    // ---- Accountability ----

    [Fact]
    public async Task A_par_counts_every_crew_and_flags_one_that_does_not_answer()
    {
        var harness = new TestHarness(cop => [new UnitResponseSystem(), new CrewSystem(Options()), new AttentionMonitor(cop, new AttentionOptions())]);
        var incident = await harness.CreateIncidentAsync();
        var engine4 = await CrewAsync(harness, "Engine 4");
        var engine7 = await CrewAsync(harness, "Engine 7", size: 4);
        await AtSceneAsync(harness, incident, engine4, engine7);
        await harness.Truth(new UnitRadioFailed(engine7, Failed: true));

        var result = await harness.C2.RequestParAsync(incident);
        Assert.True(result.Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(3));

        var par = harness.Cop.ParChecks.Single();
        Assert.Equal(2, par.Expected.Count);
        var answer = par.Responses[engine4];
        Assert.Equal(5, answer.Accounted);
        Assert.Equal(5, answer.Expected);
        Assert.False(par.Complete);
        Assert.Equal(new[] { "Engine 7" }, par.Outstanding);
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "PAR incomplete: no answer from Engine 7");
    }

    [Fact]
    public async Task Pars_rehab_and_shift_ends_come_due()
    {
        var harness = new TestHarness(cop => [new UnitResponseSystem(), new CrewSystem(Options()),
            new AttentionMonitor(cop, new AttentionOptions { WorkCycle = TimeSpan.FromMinutes(5), ParInterval = TimeSpan.FromMinutes(4) })]);
        var incident = await harness.CreateIncidentAsync();
        var engine4 = await CrewAsync(harness, "Engine 4", onShiftHours: 9.9);
        var engine12 = await CrewAsync(harness, "Engine 12");
        await AtSceneAsync(harness, incident, engine4, engine12);

        await harness.RunAsync(TimeSpan.FromMinutes(7));

        var titles = harness.Cop.Alerts.Select(a => a.Title).ToList();
        Assert.Contains("Engine 4 due for rehab", titles);
        Assert.Contains("INC-00241: PAR due", titles);
        Assert.Contains("Engine 4 past the end of its shift", titles);
        Assert.DoesNotContain("Engine 12 past the end of its shift", titles);
    }

    [Fact]
    public async Task Emergency_traffic_holds_routine_traffic_and_lets_the_mayday_through()
    {
        var harness = new TestHarness(new CommsSystem(new CommsOptions { BackgroundGarble = 0, BackgroundBroken = 0 }), new Script(
            (TimeSpan.FromSeconds(5), context =>
            {
                foreach (var unit in context.World.Units.Values)
                    CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: still working on the east side", null);
                var first = context.World.Units.Values.First();
                CommsNet.Voice(context, first, $"MAYDAY MAYDAY MAYDAY, {first.Callsign}, firefighter trapped", null, priority: 10);
            })));
        for (var i = 1; i <= 4; i++)
        {
            var id = Guid.NewGuid();
            await harness.Perceived(new UnitRegistered(id, $"Engine {i}", UnitType.Engine, null, Dublin, "Station 3", 4, []));
            await harness.Truth(new RadioBatteryChanged(id, 1));
        }
        Assert.True((await harness.C2.DeclareEmergencyTrafficAsync(RadioPlan.FireCommand, true)).Succeeded);
        Assert.False((await harness.C2.DeclareEmergencyTrafficAsync(RadioPlan.FireCommand, true)).Succeeded);

        await harness.RunAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
        var heard = harness.Cop.CommsLog.Where(m => !m.FromControl).ToList();
        Assert.Single(heard);
        Assert.StartsWith("MAYDAY", heard[0].Text);
        Assert.True(harness.Cop.Channels.Single(c => c.Info.Id == RadioPlan.FireCommand).EmergencyTraffic);

        Assert.True((await harness.C2.DeclareEmergencyTrafficAsync(RadioPlan.FireCommand, false)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        Assert.Equal(5, harness.Cop.CommsLog.Count(m => !m.FromControl));
    }

    // ---- Collapse, Mayday and rescue ----

    [Fact]
    public async Task A_collapse_traps_a_crew_working_inside_and_a_rescue_team_gets_them_out()
    {
        var options = Options(o =>
        {
            o.Collapses = true;
            o.CollapseAfterMin = o.CollapseAfterMax = TimeSpan.FromMinutes(6);
        });
        var harness = new TestHarness(cop => [new HazardSystem(), new UnitResponseSystem(), new CrewSystem(options),
            new AttentionMonitor(cop, new AttentionOptions())]);
        var incident = await harness.CreateIncidentAsync();
        var engine4 = await CrewAsync(harness, "Engine 4");
        var ladder = await CrewAsync(harness, "Ladder 3", UnitType.Ladder, capabilities: ["Rescue"], size: 4);
        await FireAsync(harness);
        await AtSceneAsync(harness, incident, engine4);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Reports.Any(r => r.Claim.Contains("Roof's sagging")), TimeSpan.FromMinutes(5)));
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Maydays.Any(), TimeSpan.FromMinutes(5)));
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        Assert.Single(await PayloadsAsync<StructureCollapsed>(harness));
        var mayday = harness.Cop.Maydays.First();
        Assert.Equal(engine4, mayday.UnitId);
        Assert.Contains("trapped by the collapse", mayday.Details);
        Assert.Contains(harness.Cop.Alerts, a => a.Title.StartsWith("MAYDAY: "));
        Assert.Contains(harness.Cop.FindUnit(engine4)!.Crew!.Members, m => m.Status == MemberStatus.InDistress);

        Assert.False((await harness.C2.DeployRescueTeamAsync(mayday.Id, engine4)).Succeeded); // not the crew in trouble
        Assert.False((await harness.C2.DeployRescueTeamAsync(mayday.Id, ladder)).Succeeded); // not at the scene yet
        await harness.RunAsync(TimeSpan.FromSeconds(70));
        Assert.Contains(harness.Cop.Alerts, a => a.Title.StartsWith("No rescue team for"));

        await AtSceneAsync(harness, incident, ladder);
        Assert.True((await harness.C2.DeployRescueTeamAsync(mayday.Id, ladder)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Maydays.All(m => !m.Active), TimeSpan.FromMinutes(20)));

        Assert.Contains("is out", harness.Cop.Maydays.First().Outcome);
        Assert.DoesNotContain(harness.World.Units[engine4].Crew.Members, m => m.State == ResponderState.Trapped);
        Assert.Null(harness.World.Units[ladder].Crew.Rescuing);
        // Everyone at the scene felt it.
        Assert.True(harness.World.Units[engine4].Crew.Stress > 0.2);
    }

    [Fact]
    public async Task Evacuating_on_the_warning_means_nobody_is_inside_when_it_comes_down()
    {
        var options = Options(o =>
        {
            o.Collapses = true;
            o.CollapseAfterMin = o.CollapseAfterMax = TimeSpan.FromMinutes(7);
        });
        var harness = new TestHarness(new HazardSystem(), new UnitResponseSystem(), new CrewSystem(options));
        var incident = await harness.CreateIncidentAsync();
        var engine4 = await CrewAsync(harness, "Engine 4");
        var engine12 = await CrewAsync(harness, "Engine 12");
        await FireAsync(harness);
        await AtSceneAsync(harness, incident, engine4, engine12);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Reports.Any(r => r.Claim.Contains("Roof's sagging")), TimeSpan.FromMinutes(5)));

        var signal = await harness.C2.SignalEvacuationAsync(incident);
        Assert.True(signal.Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.ParChecks.SingleOrDefault()?.Complete == true, TimeSpan.FromMinutes(3)));
        var par = harness.Cop.ParChecks.Single();
        Assert.Equal("Evacuation signal", par.Reason);
        Assert.True(par.AllAccounted);
        Assert.All(new[] { engine4, engine12 }, u => Assert.True(harness.World.Units[u].Crew.Withdrawn));

        Assert.True(await harness.RunUntilAsync(() => harness.World.Cascades.Any(c => c.Effect.Contains("collapse")), TimeSpan.FromMinutes(5)));
        await harness.RunAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(await PayloadsAsync<FirefighterInDistress>(harness));
        Assert.Contains(incident, harness.World.DefensiveIncidents);
    }

    [Fact]
    public async Task Command_can_declare_a_mayday_for_someone_missing_at_a_par()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new CrewSystem(Options()));
        var incident = await harness.CreateIncidentAsync();
        var engine = await CrewAsync(harness, "Engine 4");
        await AtSceneAsync(harness, incident, engine);
        // Lost in the smoke, and their radio never got the Mayday out (the crew's own calls are not modelled here).
        var member = harness.World.Units[engine].Crew.Members[3];
        await harness.Truth(new FirefighterInDistress(Guid.NewGuid(), engine, member.Id, "lost in the smoke", Dublin, AirMinutes: 15));
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        harness.World.Distress.Values.Single().Calls = 99; // no more calls

        await harness.C2.RequestParAsync(incident);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.ParChecks.Single().Complete, TimeSpan.FromMinutes(2)));
        var answer = harness.Cop.ParChecks.Single().Responses[engine];
        Assert.Equal(4, answer.Accounted);
        Assert.Equal(new[] { member.Title }, answer.Missing);
        Assert.Equal(MemberStatus.Missing, harness.Cop.FindUnit(engine)!.Crew!.Members[3].Status);

        var declared = await harness.C2.DeclareMaydayAsync(engine, member.Title, "");
        Assert.True(declared.Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        Assert.True(harness.World.Distress.Values.Single().Heard);
        Assert.False((await harness.C2.DeclareMaydayAsync(engine, member.Title, "")).Succeeded);
    }
}
