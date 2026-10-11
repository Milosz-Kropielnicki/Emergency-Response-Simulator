using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Aar;
using Emergency_Response_Simulator.Simulation.Services;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 9: after-action review and metrics, worked out from the event stream.</summary>
public class AarTests
{
    private static readonly GeoPoint Dublin = TestHarness.Dublin;

    private static Task Wait(TestHarness harness, double seconds) => harness.RunAsync(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(5));

    private static Task<AarReport> ReportAsync(TestHarness harness) => harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test exercise");

    private static async Task<Guid> CallAsync(TestHarness harness, GeoPoint? at = null)
    {
        var id = Guid.NewGuid();
        await harness.Perceived(new CallReceived(id, "999 caller", "Smoke and a loud bang from a warehouse", at ?? Dublin, 80));
        return id;
    }

    // ---- Decision timeline ----

    [Fact]
    public async Task Every_command_is_on_the_decision_timeline_with_what_prompted_it_and_how_long_it_took()
    {
        var harness = new TestHarness();
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.5, 3));
        await CallAsync(harness);
        var engine = await harness.RegisterUnitAsync();
        await Wait(harness, 90);
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        await Wait(harness, 30);
        await harness.C2.DispatchAsync(engine, incident);
        await Wait(harness, 20);
        await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Attack the fire from the east side");

        var report = await ReportAsync(harness);

        Assert.Equal(3, report.Decisions.Count);
        var opened = report.Decisions[0];
        Assert.Equal("Incident", opened.Category);
        Assert.StartsWith("First report nearby", opened.Trigger);
        Assert.Equal(TimeSpan.FromSeconds(90), opened.ResponseTime);
        Assert.Equal(Rating.Good, opened.Rating);
        Assert.Contains("No incident open", opened.Known);
        Assert.Contains("3 hurt", opened.Truth);

        var dispatched = report.Decisions[1];
        Assert.Equal("Dispatch", dispatched.Category);
        Assert.Equal(TimeSpan.FromSeconds(30), dispatched.ResponseTime);
        Assert.Equal(Rating.Good, dispatched.Rating);
        Assert.Contains("Engine 12", dispatched.Decision);

        Assert.Equal(report.Decisions.Count, report.Timeline.Count(t => t.IsDecision));
        Assert.Contains(report.Timeline, t => t.IsTruth && t.Category == "TRUTH");
        Assert.Contains("+1:30", report.Outcomes[0].KeyActions![0]);
    }

    [Fact]
    public async Task Slow_decisions_and_tasks_given_to_unqualified_crews_are_rated_poor()
    {
        var harness = new TestHarness();
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.5, 0));
        await CallAsync(harness);
        var engine = await harness.RegisterUnitAsync("Engine 14");
        await harness.Perceived(new CrewRostered(engine, Simulation.Crews.CrewRoster.Generate("Engine 14", UnitType.Engine, 4, ["BreathingApparatus"]),
            TimeSpan.FromHours(2), TimeSpan.FromHours(10)));
        await Wait(harness, 6 * 60);
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        await Wait(harness, 4 * 60);
        await harness.C2.DispatchAsync(engine, incident);
        await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Decontaminate the casualties");

        var report = await ReportAsync(harness);

        Assert.All(report.Decisions.Take(2), d => Assert.Equal(Rating.Poor, d.Rating));
        Assert.Contains("hazardous materials", report.Decisions[2].Note);
        Assert.Equal(Rating.Poor, report.Decisions[2].Rating);
        var opening = report.Branches.Single(b => b.Topic == "Opening the incident");
        Assert.Equal(Rating.Poor, opening.Rating);
        Assert.Contains("later than recommended", opening.Effect);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.StartsWith("Opening the incident"));
        Assert.NotEqual(Competence.Competent, report.Overall);
    }

    // ---- Metrics ----

    [Fact]
    public async Task Communication_errors_are_counted_and_judged()
    {
        var harness = new TestHarness();
        var engine = await harness.RegisterUnitAsync();
        var unclear = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            unclear.Add(id);
            await harness.Perceived(new CommsLogged(id, RadioPlan.FireCommand, "Engine 12", "Control", "… fire … east …", CommsQuality.Garbled, 4, engine));
        }
        await harness.Perceived(new CommsLogged(Guid.NewGuid(), RadioPlan.FireCommand, "Engine 12", "Control", "Engine 12 on scene", CommsQuality.Clear, 3, engine));
        await harness.C2.RequestRepeatAsync(unclear[0]);
        await harness.C2.TransmitAsync(RadioPlan.FireCommand, "Engine 12", "Engine 12, Control, um, please be advised, report your status");
        await harness.C2.TransmitAsync(RadioPlan.FireCommand, "Engine 12", "Engine 12, Control, status report please");
        await Wait(harness, 10);

        var metrics = (await ReportAsync(harness)).Metrics.ToDictionary(m => m.Name);

        Assert.Equal("3 of 4", metrics["Field messages heard broken or garbled"].Display);
        var repeats = metrics["\"Say again\" on unclear messages"];
        Assert.Equal("1 of 3", repeats.Display);
        Assert.Equal(Rating.Fair, repeats.Rating);
        // Without a comms system the transmissions aren't logged with notes, so discipline is judged on what was logged.
        Assert.True(metrics.ContainsKey("Radio discipline"));
    }

    [Fact]
    public async Task Commitment_delays_and_heatmaps_come_from_the_avl_feed()
    {
        var harness = new TestHarness();
        var engine = await harness.RegisterUnitAsync();
        var spare = await harness.RegisterUnitAsync("Engine 7");
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        await harness.C2.DispatchAsync(engine, incident);
        await harness.Perceived(new UnitStatusChanged(engine, UnitStatus.EnRoute));
        var jam = GeoMath.Destination(Dublin, 0, 600);
        // Stuck in traffic for three minutes, then on scene for ten.
        for (var i = 0; i < 18; i++)
        {
            await harness.Perceived(new UnitPositionReported(engine, jam, 2, 0, TimeSpan.FromMinutes(4)));
            await Wait(harness, 10);
        }
        await harness.Perceived(new UnitStatusChanged(engine, UnitStatus.OnScene));
        for (var i = 0; i < 10; i++)
        {
            await harness.Perceived(new UnitPositionReported(engine, Dublin, 0, 0, null));
            await Wait(harness, 60);
        }

        var report = await ReportAsync(harness);

        var usage = report.Units.Single(u => u.UnitId == engine);
        Assert.True(usage.Committed > 0.9, $"{usage.Committed:P0}");
        Assert.Contains(usage.Spans, s => s.Status == UnitStatus.EnRoute);
        Assert.Equal(0, report.Units.Single(u => u.UnitId == spare).Committed);
        var delay = Assert.Single(report.Delays);
        Assert.Equal("Engine 12", delay.Callsign);
        Assert.True(delay.Lost >= TimeSpan.FromMinutes(2.5));
        Assert.Equal("Slow traffic", delay.Cause);

        var commitment = report.Heatmaps.Single(h => h.Title == "Resource commitment");
        var delays = report.Heatmaps.Single(h => h.Title == "Delays en route");
        Assert.True(commitment.Max >= 9, $"{commitment.Max}");
        Assert.True(delays.Max >= 2.5 && delays.Max < commitment.Max);
        Assert.NotEmpty(commitment.Hotspots);
        Assert.Contains(report.Metrics, m => m.Name == "Units never committed" && m.Display == "1 of 2");
    }

    // ---- Branch points ----

    [Theory]
    [InlineData(30, Rating.Good)]
    [InlineData(-1, Rating.Poor)]
    public async Task The_evacuation_signal_is_judged_against_the_warning_and_the_collapse(int signalAfterSeconds, Rating expected)
    {
        var harness = new TestHarness();
        var engine = await harness.RegisterUnitAsync();
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        await harness.C2.DispatchAsync(engine, incident);
        await harness.Perceived(new UnitStatusChanged(engine, UnitStatus.Operating));
        await harness.Perceived(new ReportReceived(Guid.NewGuid(), incident, ReportSource.FieldUnit, "Engine 12",
            "Roof's sagging at the rear. The building is going: recommend we pull everyone out.", Confidence.High, VerificationStatus.Reported, Dublin, 30, engine));
        if (signalAfterSeconds >= 0)
        {
            await Wait(harness, signalAfterSeconds);
            await harness.C2.SignalEvacuationAsync(incident);
        }
        await Wait(harness, 240);
        await harness.Truth(new StructureCollapsed(Guid.NewGuid(), Dublin, "roof comes down"));
        if (signalAfterSeconds < 0)
            await harness.Truth(new FirefighterInDistress(Guid.NewGuid(), engine, Guid.NewGuid(), "trapped by the collapse", Dublin, 12));

        var branch = (await ReportAsync(harness)).Branches.Single(b => b.Topic == "Evacuating the building");

        Assert.Equal(expected, branch.Rating);
        Assert.Contains(expected == Rating.Good ? "Every crew was out" : "1 firefighter(s) trapped", branch.Effect);
        if (expected == Rating.Poor) Assert.Contains("before the collapse", branch.Effect);
    }

    [Fact]
    public async Task A_mayday_is_judged_on_the_rescue_team_and_emergency_traffic()
    {
        var harness = new TestHarness();
        var engine = await harness.RegisterUnitAsync("Engine 4");
        var rescuers = await harness.RegisterUnitAsync("Engine 12");
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        foreach (var unit in new[] { engine, rescuers })
        {
            await harness.C2.DispatchAsync(unit, incident);
            await harness.Perceived(new UnitStatusChanged(unit, UnitStatus.Operating));
        }
        var mayday = Guid.NewGuid();
        await harness.Perceived(new MaydayDeclared(mayday, engine, "Firefighter Byrne", "Firefighter Byrne (Engine 4): trapped, 12 min of air"));
        await Wait(harness, 150);
        Assert.True((await harness.C2.DeployRescueTeamAsync(mayday, rescuers)).Succeeded);

        var report = await ReportAsync(harness);

        var rescue = report.Metrics.Single(m => m.Name == "Mayday to rescue team");
        Assert.Equal(Rating.Fair, rescue.Rating);
        Assert.Equal(Rating.Poor, report.Metrics.Single(m => m.Name == "Emergency traffic declared for Maydays").Rating);
        Assert.Contains(report.Branches, b => b.Topic == "Emergency traffic for Firefighter Byrne" && b.Rating == Rating.Poor);
        var decision = report.Decisions.Single(d => d.Category == "Safety");
        Assert.StartsWith("Mayday from Engine 4", decision.Trigger);
        Assert.Contains("1 Mayday active", decision.Known);
    }

    // ---- Exports ----

    [Fact]
    public async Task Reports_export_to_html_for_certification_and_csv_for_analysis()
    {
        var harness = new TestHarness();
        await CallAsync(harness);
        var engine = await harness.RegisterUnitAsync();
        await Wait(harness, 60);
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        await harness.C2.DispatchAsync(engine, incident);
        await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Hold the east side, \"no entry\" <now>");
        var report = await ReportAsync(harness);

        var html = AarExport.Html(report, new AarSignOff("Pat Murphy", "Incident commander", "S. Kelly", "Good situational awareness.", Competence.Developing));
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Pat Murphy", html);
        Assert.Contains("Good situational awareness.", html);
        Assert.Contains("Decision timeline", html);
        Assert.Contains("Engine 12", html);
        Assert.Contains("&lt;now&gt;", html);
        Assert.DoesNotContain("<now>", html);
        Assert.Contains("Assessor signature", html);

        var csv = AarExport.Csv(report);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Section,Time,Category,Item,Result,Standard,Rating,Detail", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Decision,") && l.Contains("\"\"no entry\"\""));
        Assert.Contains(lines, l => l.StartsWith("Metric,Dispatch,") || l.StartsWith("Metric,,Dispatch"));
    }

    [Fact]
    public async Task The_same_session_always_gives_the_same_report()
    {
        var harness = new TestHarness();
        await CallAsync(harness);
        var engine = await harness.RegisterUnitAsync();
        await Wait(harness, 100);
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin)).EntityId!.Value;
        await harness.C2.DispatchAsync(engine, incident);

        var first = await ReportAsync(harness);
        var second = await ReportAsync(harness);
        Assert.Equal(AarExport.Csv(first), AarExport.Csv(second));
    }

    // ---- Branch re-runs ----

    [Fact]
    public async Task The_recommended_response_and_no_response_can_be_re_run_in_the_same_scenario()
    {
        var runner = new BranchRunner();
        var recommended = await runner.RunAsync(BranchPolicy.Recommended, TestHarness.Start, TimeSpan.FromMinutes(12), runs: 1);
        var none = await runner.RunAsync(BranchPolicy.NoResponse, TestHarness.Start, TimeSpan.FromMinutes(12), runs: 1);

        Assert.Equal("Recommended response", recommended.Label);
        Assert.NotNull(recommended.FirstDispatchMinutes);
        Assert.True(recommended.FirstDispatchMinutes < 4, $"{recommended.FirstDispatchMinutes}");
        Assert.Contains(recommended.KeyActions!, a => a.Contains("INC-00001"));
        Assert.Contains(recommended.KeyActions!, a => a.Contains("Ambulance"));
        Assert.Null(none.FirstDispatchMinutes);
        Assert.Empty(none.KeyActions!);
        Assert.True(none.Injured > 0);
    }

    [Fact]
    public async Task Branch_comparison_is_offered_only_when_the_scenario_can_be_re_run()
    {
        var harness = new TestHarness();
        Assert.False(harness.Aar.CanCompareBranches);
        Assert.Empty(await harness.Aar.CompareBranchesAsync(harness.Engine.SessionId));
        Assert.True(BranchRunner.CanRun("barrow-street"));
        Assert.False(BranchRunner.CanRun(null));
        Assert.True(new AarService(harness.Store, branches: new BranchRunner()).CanCompareBranches);
    }
}
