using System.Net;
using System.Text;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Instructor;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.Systems;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 19: scenario design and adaptive training. Designed scenarios validate, load and run with timed, conditional and
/// branching steps; the library ships scenarios a session can open; difficulty adapts to how the trainees cope; random
/// events come; real weather and historical incidents feed scenarios; and the incidents within an incident play out.
/// </summary>
public class ScenarioTests
{
    private static readonly ScriptedInjectSystem NoScript = new(ScenarioCatalog.FreePlayName, []);

    /// <summary>A harness with an empty script and the instructor's service over it.</summary>
    private static (TestHarness Harness, InstructorService Instructor) Session(params ISimulationSystem[] more)
    {
        ISimulationSystem[] systems = [new ScriptedInjectSystem(ScenarioCatalog.FreePlayName, []), .. more];
        var harness = new TestHarness(systems);
        return (harness, new InstructorService(harness.Engine, systems, harness.Cop));
    }

    private static async Task<List<T>> PayloadsAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Select(e => e.Payload).OfType<T>().ToList();

    private static ScenarioAction Call(string summary) => new("call", new Dictionary<string, string> { ["summary"] = summary });

    private static ScenarioDefinition Branching() => new()
    {
        Key = "branching", Name = "Branching test",
        Steps =
        [
            new("a", "Opening call", new ScenarioTrigger(TriggerKind.AtTime, 1), [Call("opening call")]),
            new("b", "Road closed in time", new ScenarioTrigger(TriggerKind.WhenCondition, 2, Condition: ConditionKind.ClosureDeclared, WindowMinutes: 3), [Call("closure praised")]),
            new("c", "Too late", new ScenarioTrigger(TriggerKind.AfterStep, 1, StepId: "b", Outcome: StepOutcome.Expired), [Call("pile-up because nobody closed the road")]),
            new("d", "Follow-up", new ScenarioTrigger(TriggerKind.AfterStep, 2, StepId: "a"), [Call("follow-up call")]),
        ],
    };

    private static async Task<List<string>> CallsAsync(TestHarness harness) => [.. (await PayloadsAsync<CallReceived>(harness)).Select(c => c.Summary)];

    // ---- Designed scenarios ----

    [Fact]
    public void The_shipped_scenarios_validate_round_trip_and_join_the_catalogue()
    {
        Assert.True(ScenarioLibrary.BuiltIn.Count >= 2);
        foreach (var scenario in ScenarioLibrary.BuiltIn)
        {
            Assert.Empty(ScenarioDesign.Validate(scenario, InjectTemplates.All));
            Assert.Equal(scenario.ToJson(), ScenarioDefinition.FromJson(scenario.ToJson()).ToJson());
            Assert.Contains(ScenarioCatalog.All, s => s.Key == ScenarioLibrary.Prefix + scenario.Key && s.Name == scenario.Name);
            Assert.Equal(scenario.Steps.Count + 1, ScenarioCompiler.Compile(scenario, 1).Count);
        }
        Assert.NotNull(ScenarioLibrary.Find("library:stadium-secondary-device"));
    }

    [Fact]
    public void Validation_finds_what_would_go_wrong()
    {
        var scenario = new ScenarioDefinition
        {
            Name = "Broken",
            Steps =
            [
                new("a", "A", new ScenarioTrigger(TriggerKind.AfterStep, 0, StepId: "b"), [new ScenarioAction("no-such-inject")]),
                new("b", "B", new ScenarioTrigger(TriggerKind.AfterStep, 0, StepId: "a"), [new ScenarioAction("wind-shift", new Dictionary<string, string> { ["from"] = "Up" })]),
                new("c", "C", new ScenarioTrigger(TriggerKind.AfterStep, 0, StepId: "ghost"), [Call("x")]),
                new("d", "D", new ScenarioTrigger(TriggerKind.AfterStep, 0, StepId: "c", Outcome: StepOutcome.Expired), [Call("x")]),
                new("d", "D again", new ScenarioTrigger(TriggerKind.AtTime, 1), []),
            ],
        };
        var problems = ScenarioDesign.Validate(scenario, InjectTemplates.All);
        Assert.Contains(problems, p => p.Contains("there is no inject \"no-such-inject\""));
        Assert.Contains(problems, p => p.Contains("Wind from must be one of"));
        Assert.Contains(problems, p => p.Contains("waits on itself"));
        Assert.Contains(problems, p => p.Contains("doesn't exist (ghost)"));
        Assert.Contains(problems, p => p.Contains("has no window to expire in"));
        Assert.Contains(problems, p => p.Contains("Two steps are called d"));
        Assert.Contains(problems, p => p.Contains("does nothing"));
    }

    [Fact]
    public async Task A_loaded_scenario_runs_its_timed_steps_and_takes_the_branch_the_trainees_earn()
    {
        // Nobody closes the road: the condition expires and its branch runs.
        var (harness, instructor) = Session();
        var loaded = await instructor.LoadScenarioAsync(Branching());
        Assert.True(loaded.Succeeded, loaded.Error);
        await harness.RunAsync(TimeSpan.FromMinutes(8));
        var calls = await CallsAsync(harness);
        Assert.Equal(["opening call", "follow-up call", "pile-up because nobody closed the road"], calls);
        Assert.Contains(await PayloadsAsync<ScenarioLoaded>(harness), s => s.Name == "Branching test");
        var script = await instructor.ListInjectsAsync();
        Assert.Equal(InjectStatus.Expired, script.Single(i => i.Description.StartsWith("Road closed in time")).Status);

        // They close it in time: the other branch.
        (harness, instructor) = Session();
        Assert.True((await instructor.LoadScenarioAsync(Branching())).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(3));
        Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.RoadClosure, "Pearse Street closed", [Dublin, GeoMath.Destination(Dublin, 90, 200)])).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(5));
        calls = await CallsAsync(harness);
        Assert.Contains("closure praised", calls);
        Assert.DoesNotContain("pile-up because nobody closed the road", calls);

        // A broken scenario is refused.
        Assert.False((await instructor.LoadScenarioAsync(new ScenarioDefinition { Name = "Empty" })).Succeeded);
    }

    [Fact]
    public async Task Optional_steps_wait_while_adaptive_difficulty_has_the_trainees_struggling()
    {
        var (harness, instructor) = Session();
        var scenario = new ScenarioDefinition
        {
            Name = "Optional", Training = new TrainingSettings(DifficultyMode.Adaptive, Level: 2),
            Steps = [new("extra", "Extra pressure", new ScenarioTrigger(TriggerKind.AtTime, 1), [Call("extra pressure")], Optional: true)],
        };
        Assert.True((await instructor.LoadScenarioAsync(scenario)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(5));
        Assert.Empty(await CallsAsync(harness));

        Assert.True((await instructor.SetTrainingAsync(new TrainingSettings(DifficultyMode.Adaptive, Level: 3))).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(["extra pressure"], await CallsAsync(harness));
        Assert.Equal(3, (await instructor.GetTrainingAsync()).Level);
    }

    [Fact]
    public async Task A_library_scenario_opens_a_networked_session_and_the_instructor_can_load_another_over_the_wire()
    {
        await using var server = await InProcessServer.StartAsync(ScenarioLibrary.Prefix + "docklands-chlorine-night");
        var console = RemoteProxy.Create<IInstructorService>(server.Instructor, SimulationHubProtocol.Services.Instructor);
        var script = await console.ListInjectsAsync();
        Assert.Contains(script, i => i.Description.StartsWith("Scenario: Night shift: chlorine at the docks"));
        Assert.Contains(script, i => i.Description.StartsWith("Too late: the leak worsens") && i.Conditional);

        await server.StepAsync(TimeSpan.FromSeconds(10));
        var training = await console.GetTrainingAsync();
        Assert.Equal(DifficultyMode.Adaptive, training.Mode); // from the library scenario's settings, applied at the start

        var loaded = await console.LoadScenarioAsync(Branching());
        Assert.True(loaded.Succeeded, loaded.Error);
        Assert.Contains(await console.ListInjectsAsync(), i => i.Description.StartsWith("Opening call"));
    }

    // ---- Adaptive difficulty and random events ----

    [Fact]
    public async Task Difficulty_rises_while_the_trainees_cope_and_falls_when_they_struggle()
    {
        var harness = new TestHarness(cop => [new AdaptiveDifficultySystem(cop)]);
        await harness.Truth(new TrainingSettingsChanged(new TrainingSettings(DifficultyMode.Adaptive, Level: 3)));
        await harness.RunAsync(AdaptiveDifficultySystem.AssessEvery * 2 + TimeSpan.FromSeconds(5));
        Assert.Equal(4, harness.World.Training.Level); // two clean assessments

        // Four incidents nobody is sent to.
        for (var i = 0; i < 4; i++) await harness.CreateIncidentAsync($"INC-0030{i}");
        await harness.RunAsync(AdaptiveDifficultySystem.AssessEvery);
        Assert.Equal(3, harness.World.Training.Level);
        var adjusted = (await PayloadsAsync<DifficultyAdjusted>(harness)).Last();
        Assert.Contains("4 incident(s) with nobody sent", adjusted.Reason);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal((3, 3), (report.Training.StartLevel, report.Training.EndLevel));
        Assert.Equal(3, report.Training.Scores.Count);
        Assert.Contains(report.Training.Adjustments, a => a.Contains("raised to 4"));
    }

    [Fact]
    public async Task Random_events_come_at_random_but_the_same_way_every_time()
    {
        async Task<List<string>> Run()
        {
            var harness = new TestHarness(new RandomEventSystem());
            var unit = await harness.RegisterUnitAsync();
            var incident = await harness.CreateIncidentAsync();
            Assert.True((await harness.C2.DispatchAsync(unit, incident)).Succeeded);
            await harness.Truth(new TrainingSettingsChanged(new TrainingSettings(RandomEvents: true, MeanMinutes: 5, Level: 5)));
            await harness.RunAsync(TimeSpan.FromMinutes(40), TimeSpan.FromSeconds(30));
            return [.. (await PayloadsAsync<RandomEventOccurred>(harness)).Select(e => $"{e.Kind}: {e.Description}")];
        }

        var first = await Run();
        Assert.True(first.Count >= 5, string.Join("; ", first));
        Assert.Equal(first, await Run());
        Assert.Contains(first, e => e.StartsWith("Weather"));
        Assert.Contains(first, e => e.StartsWith("Incident") || e.StartsWith("Equipment"));
    }

    // ---- Real-world data ----

    private sealed class FakeFeed(WeatherReading? reading) : IWeatherFeed
    {
        public int Calls { get; private set; }
        public Task<WeatherReading?> CurrentAsync(GeoPoint at, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(reading); }
        public Task<WeatherReading?> HistoricalAsync(GeoPoint at, DateTimeOffset when, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(reading); }
    }

    [Fact]
    public async Task Live_weather_replaces_the_scenarios_and_an_unreachable_service_leaves_it_standing()
    {
        var feed = new FakeFeed(new WeatherReading(135, 9, 6, 0.9, Start, "test"));
        var (harness, instructor) = Session(new RealWeatherSystem(feed));
        var scenario = Branching();
        scenario.Weather = new ScenarioWeather(270, 3, Source: WeatherSource.Live);
        Assert.True((await instructor.LoadScenarioAsync(scenario)).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((135, 9), (harness.World.Weather.WindFromDegrees, harness.World.Weather.WindSpeedMps));
        Assert.Contains(await PayloadsAsync<WeatherObserved>(harness), w => w.WindFromDegrees == 135);
        await harness.RunAsync(RealWeatherSystem.RefreshEvery);
        Assert.Equal(2, feed.Calls);

        (harness, instructor) = Session(new RealWeatherSystem(new FakeFeed(null)));
        Assert.True((await instructor.LoadScenarioAsync(scenario)).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((270, 3), (harness.World.Weather.WindFromDegrees, harness.World.Weather.WindSpeedMps));
        Assert.Contains(await PayloadsAsync<RandomEventOccurred>(harness), e => e.Description.Contains("unreachable"));
    }

    private sealed class Canned(string json) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Open_Meteo_current_and_archive_answers_are_read()
    {
        var current = new Canned("""{"current":{"time":"2026-10-17T09:00","temperature_2m":11.5,"relative_humidity_2m":82,"wind_speed_10m":6.4,"wind_direction_10m":240}}""");
        var reading = await new OpenMeteoWeatherFeed(new HttpClient(current)).CurrentAsync(Dublin);
        Assert.Equal((240, 6.4, 11.5, 0.82), (reading!.WindFromDegrees, reading.WindSpeedMps, reading.TemperatureC, reading.RelativeHumidity));
        Assert.Contains("wind_speed_unit=ms", current.Asked!.Query);

        var archive = new Canned("""{"hourly":{"time":["2011-10-24T00:00","2011-10-24T01:00","2011-10-24T02:00"],"temperature_2m":[10,10.5,11],"relative_humidity_2m":[90,91,95],"wind_speed_10m":[5,6,7],"wind_direction_10m":[150,160,170]}}""");
        var past = await new OpenMeteoWeatherFeed(new HttpClient(archive)).HistoricalAsync(Dublin, new DateTimeOffset(2011, 10, 24, 2, 30, 0, TimeSpan.Zero));
        Assert.Equal((170, 7.0), (past!.WindFromDegrees, past.WindSpeedMps));
        Assert.StartsWith("https://archive-api.open-meteo.com", archive.Asked!.ToString());

        Assert.Null(await new OpenMeteoWeatherFeed(new HttpClient(new Canned("not json"))).CurrentAsync(Dublin));
    }

    [Fact]
    public void Historical_incidents_become_a_scenario_with_the_archives_weather()
    {
        const string csv = """
            # Illustrative records, not real incidents
            time,type,latitude,longitude,casualties,description
            2011-10-24 18:00,house fire,53.3402,-6.2381,1,"Smoke from a terrace, someone trapped upstairs"
            2011-10-24 19:30,RTA,53.3511,-6.2603,2,
            2011-10-24 21:00,flood,not-a-number,-6.25,0,
            2011-10-24 20:00,meteor,53.34,-6.25,0,
            """;
        var (scenario, skipped) = HistoricalIncidentImporter.FromCsv(csv, "October night");
        Assert.Equal(2, scenario.Steps.Count);
        Assert.Equal(2, skipped.Count);
        Assert.Equal([1.0, 16.0], scenario.Steps.Select(s => s.Trigger.Minutes));
        Assert.Equal("StructureFire", scenario.Steps[0].Actions[0].Values!["type"]);
        Assert.Equal("Smoke from a terrace, someone trapped upstairs", scenario.Steps[0].Actions[0].Values!["call"]);
        Assert.Equal(WeatherSource.Historical, scenario.Weather.Source);
        Assert.Equal(new DateTimeOffset(2011, 10, 24, 18, 0, 0, TimeSpan.Zero), scenario.Weather.HistoricalAt);
        Assert.Empty(ScenarioDesign.Validate(scenario, InjectTemplates.All));
    }

    // ---- Incidents within the incident ----

    private static async Task<(TestHarness Harness, Guid Incident)> SceneAsync(params ISimulationSystem[] more)
    {
        var harness = new TestHarness([new UnitResponseSystem(), new CompoundThreatSystem(), .. more]);
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Explosion, Dublin, 0.4, 0));
        var incident = await harness.CreateIncidentAsync();
        return (harness, incident);
    }

    private static async Task OnSceneAsync(TestHarness harness, Guid incident, params Guid[] units)
    {
        foreach (var unit in units) Assert.True((await harness.C2.DispatchAsync(unit, incident)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => units.All(u => harness.Cop.FindUnit(u)?.Status is UnitStatus.OnScene or UnitStatus.Operating), TimeSpan.FromMinutes(5)));
    }

    private static Task FireAsync(TestHarness harness, string key, Dictionary<string, string> values) =>
        harness.Engine.ApplyAsync(context =>
        {
            foreach (var (visibility, payload) in InjectTemplates.Create(key, values)!.Produce(context)) context.Emit(visibility, payload, EventSources.Instructor);
            return true;
        });

    [Fact]
    public async Task A_secondary_device_is_spotted_and_goes_off_among_the_crews_unless_bomb_disposal_makes_it_safe()
    {
        var (harness, incident) = await SceneAsync();
        var ambulance = await harness.RegisterUnitAsync("Ambulance 14", UnitType.AmbulanceAls);
        await OnSceneAsync(harness, incident, ambulance);
        await FireAsync(harness, "secondary-device", new() { ["fuse"] = "15", ["distance"] = "40" });
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(await PayloadsAsync<ReportReceived>(harness), r => r.SourceName == "Ambulance 14" && r.Claim.Contains("suspicious item"));

        await harness.RunAsync(TimeSpan.FromMinutes(16));
        var blast = Assert.Single(await PayloadsAsync<SecondaryDeviceDetonated>(harness));
        Assert.Equal(1, blast.Responders);
        Assert.Contains(await PayloadsAsync<CasualtyInjured>(harness), c => c.Who == "a responder from Ambulance 14");
        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Responders exposed to a secondary device");
        Assert.Equal(1, report.Training.RespondersHurt);

        // With the bomb squad on scene in time, it never goes off.
        (harness, incident) = await SceneAsync();
        var squad = await harness.RegisterUnitAsync("EOD 1", UnitType.BombDisposal);
        await OnSceneAsync(harness, incident, squad);
        await FireAsync(harness, "secondary-device", new() { ["fuse"] = "15", ["distance"] = "40" });
        await harness.RunAsync(TimeSpan.FromMinutes(16));
        Assert.Single(await PayloadsAsync<SecondaryDeviceRenderedSafe>(harness));
        Assert.Empty(await PayloadsAsync<SecondaryDeviceDetonated>(harness));
        report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("made safe before it went off"));
    }

    [Fact]
    public async Task An_active_shooter_goes_on_until_armed_gardai_arrive()
    {
        var (harness, incident) = await SceneAsync();
        var armed = await harness.RegisterUnitAsync("ARU 1", UnitType.Swat);
        await FireAsync(harness, "active-shooter", new() { ["distance"] = "60" });
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(await PayloadsAsync<CallReceived>(harness), c => c.Summary.StartsWith("Shots fired"));
        Assert.Empty(await PayloadsAsync<ActiveShooterNeutralised>(harness));

        await OnSceneAsync(harness, incident, armed);
        await harness.RunAsync(CompoundThreatSystem.ArmedResponseTime + TimeSpan.FromSeconds(10));
        Assert.Single(await PayloadsAsync<ActiveShooterNeutralised>(harness));
        Assert.Contains(await PayloadsAsync<ReportReceived>(harness), r => r.SourceName == "ARU 1" && r.Claim.Contains("neutralised"));
        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Training.Complications, c => c.StartsWith("Active shooter stopped by armed gardaí"));
    }

    [Fact]
    public async Task A_responder_down_is_hurt_where_they_work_and_their_crewmate_calls_it_in()
    {
        var (harness, incident) = await SceneAsync();
        var ambulance = await harness.RegisterUnitAsync("Ambulance 14", UnitType.AmbulanceAls);
        await OnSceneAsync(harness, incident, ambulance);
        await FireAsync(harness, "responder-down", new() { ["cause"] = "assaulted by a bystander" });
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(await PayloadsAsync<CasualtyInjured>(harness), c => c.Who == "a responder from Ambulance 14" && c.Cause == "assaulted by a bystander");
        Assert.Contains(await PayloadsAsync<ReportReceived>(harness), r => r.Claim.Contains("one of our crew is down"));
        Assert.Single(await PayloadsAsync<ResponderDown>(harness));
        _ = NoScript;
    }
}
