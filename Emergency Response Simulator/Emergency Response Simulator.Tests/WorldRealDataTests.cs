using System.Diagnostics;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 6 on real Dublin data (read-only GIS; the event store stays in memory).</summary>
public class WorldRealDataTests(ITestOutputHelper output)
{
    [RoadDataFact]
    public async Task Barrow_street_world_runs_on_real_roads_and_terrain_fast_enough_for_real_time()
    {
        var options = new DbContextOptionsBuilder<ErsDbContext>();
        ServiceCollectionExtensions.ConfigureErsDb(options, ErsConfiguration.Load().GetConnectionString("Ers")!);
        var services = new ServiceCollection()
            .AddSingleton<IGisService>(new GisService(new PooledDbContextFactory<ErsDbContext>(options.Options)))
            .BuildServiceProvider();

        var terrain = new HazardTerrain(services);
        await terrain.LoadAsync();
        var routing = new RoutingService(new TrafficFeed(), services);
        routing.Use(DublinRoads.Network!);
        var live = new LiveTraffic();
        var feed = new TrafficFeed();

        var harness = new TestHarness(cop =>
        [
            BarrowStreetScenario.Create(demoAutoResponse: true), new WeatherSystem(), new HazardSystem(terrain),
            new InfrastructureSystem(routing), new UnitResponseSystem(routing, live), new MedicalSystem(routing),
            new CivilianSystem(terrain, routing: routing), new AgencyAiSystem(routing), new CommandResponseSystem(),
            new TrafficSystem(routing, live, feed), new HazardReportingSystem(routing), new CrewSystem(), new CommsSystem(routing: routing),
            new AttentionMonitor(cop, new AttentionOptions(), routing),
        ]);
        await new DemoRosterScenario().SeedAsync(harness.Engine);

        var stopwatch = Stopwatch.StartNew();
        const int steps = 25 * 60 / 6; // 25 minutes at 60× speed: 6 s per tick
        await harness.RunAsync(TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(6));
        var perStep = stopwatch.Elapsed.TotalMilliseconds / steps;
        output.WriteLine($"{perStep:F1} ms per 6 s step; terrain loaded: {terrain.IsLoaded}");
        foreach (var c in harness.World.Cascades) output.WriteLine($"{c.At - TestHarness.Start:mm\\:ss} {c.Cause} → {c.Effect}");
        foreach (var e in (await harness.EventsAsync()).Where(e => e.Payload is CrewConditionReported or MaydayDeclared or MaydayResolved or ParReported
                                                                   or FirefighterInDistress or StructureCollapsed or CrewWelfareChanged or CrewMemberStoodDown
                                                                   || e.Payload is ReportReceived report && report.Claim.Contains("Roof")))
            output.WriteLine($"{e.SimTime - TestHarness.Start:mm\\:ss} {e.Visibility} {e.Payload}");

        Assert.True(terrain.IsLoaded);
        Assert.Contains(harness.World.Hazards.Values, h => h.Kind == HazardKind.Fire);
        // The crew sent its size-up (over the radio it may arrive garbled, words missing, or not at all).
        Assert.True(harness.World.Radio.Sent.Values.Any(t => t.Text.Contains("size-up"))
                    || (await harness.EventsAsync()).Any(e => e.Payload is TransmissionLost { Text: var lost } && lost.Contains("size-up")));
        // The engine ticks every 100 ms of real time; a step must fit comfortably inside that.
        Assert.True(perStep < 50, $"{perStep:F1} ms per step");
    }
}
