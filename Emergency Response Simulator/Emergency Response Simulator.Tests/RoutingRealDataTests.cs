using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Skips unless the main database has imported roads (GisImport has been run).</summary>
public sealed class RoadDataFactAttribute : FactAttribute
{
    public RoadDataFactAttribute()
    {
        if (DublinRoads.Network is null)
            Skip = "No imported road network in ConnectionStrings:Ers; run the GIS import.";
    }
}

public static class DublinRoads
{
    private static readonly Lazy<RoadNetwork?> Lazy = new(Load);

    public static RoadNetwork? Network => Lazy.Value;

    private static RoadNetwork? Load()
    {
        var connectionString = ErsConfiguration.Load().GetConnectionString("Ers");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try
        {
            var options = new DbContextOptionsBuilder<ErsDbContext>();
            ServiceCollectionExtensions.ConfigureErsDb(options, connectionString);
            var gis = new GisService(new PooledDbContextFactory<ErsDbContext>(options.Options));
            var roads = gis.GetAllFeaturesAsync(GisLayerKeys.Roads).GetAwaiter().GetResult();
            return roads.Count > 1000 ? RoadNetwork.Build(roads) : null;
        }
        catch
        {
            return null;
        }
    }
}

public class RoutingRealDataTests(ITestOutputHelper output)
{
    private static readonly GeoPoint Station3 = new(53.3498, -6.2603);
    private static readonly GeoPoint Station8 = new(53.3331, -6.2489);
    private static readonly DateTimeOffset Night = new(2026, 10, 8, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MorningPeak = new(2026, 10, 7, 8, 0, 0, TimeSpan.FromHours(1)); // a Wednesday

    [RoadDataFact]
    public void Routes_across_central_dublin_follow_the_roads()
    {
        var network = DublinRoads.Network!;
        var route = network.Route(Station3, BarrowStreetScenario.FireLocation,
            new RouteOptions(UnitType.Engine, Night), new TimeOfDayTraffic())!;

        var straight = GeoMath.DistanceMeters(Station3, BarrowStreetScenario.FireLocation);
        output.WriteLine($"{network.NodeCount} nodes, {network.EdgeCount} edges");
        output.WriteLine($"Straight {straight:F0} m, route {route.DistanceMeters:F0} m, {route.Duration.TotalMinutes:F1} min");
        output.WriteLine("Via: " + string.Join(" → ", route.Legs.Select(l => l.RoadName).Where(n => n is not null).Distinct()));

        // The city-centre one-way system makes road distance up to ~2.5× the straight line here.
        Assert.InRange(route.DistanceMeters, straight * 1.05, straight * 2.5);
        Assert.InRange(route.Duration.TotalMinutes, 2, 12);
        Assert.True(route.Legs.Count(l => l.EdgeId >= 0) > 10);
    }

    [RoadDataFact]
    public async Task Barrow_street_responders_hit_the_unreported_lorry_and_command_learns_of_the_delay()
    {
        var routing = new RoutingService(new FreeFlowTraffic(), new ServiceCollection().BuildServiceProvider());
        routing.Use(DublinRoads.Network!);
        var harness = new TestHarness(cop => [BarrowStreetScenario.Create(),
            new Simulation.Systems.UnitResponseSystem(routing), new Simulation.Systems.AttentionMonitor(cop, new Simulation.Systems.AttentionOptions(), routing)]);
        await new DemoRosterScenario().SeedAsync(harness.Engine);

        // Trainee opens the incident from the second call and sends the two nearest engines.
        await Step(harness, TimeSpan.FromSeconds(110));
        var call = harness.Cop.Reports.Last(r => r.Source == ReportSource.EmergencyCall);
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High,
            BarrowStreetScenario.FireLocation, "Barrow Street", fromReportId: call.Id)).EntityId!.Value;
        foreach (var callsign in new[] { "Engine 4", "Engine 12" })
            await harness.C2.DispatchAsync(harness.Cop.Units.Single(u => u.Callsign == callsign).Id, incident);

        await Step(harness, TimeSpan.FromMinutes(12));

        foreach (var report in harness.Cop.Reports.Where(r => r.Source == ReportSource.FieldUnit))
            output.WriteLine($"{report.SourceName}: {report.Claim}");
        foreach (var alert in harness.Cop.Alerts)
            output.WriteLine($"ALERT {alert.Title}: {alert.Message}");
        foreach (var unit in harness.Cop.Units.Where(u => u.AssignedIncidentId == incident))
            output.WriteLine($"{unit.Callsign}: {unit.Status}");

        // Engine 12 (first engine on the road at minute 6) breaks down: AVL shows it stop, then it reports.
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Engine 12 stopped en route");
        Assert.Equal(UnitStatus.OutOfService, harness.Cop.Units.Single(u => u.Callsign == "Engine 12").Status);

        // Engine 4 carries on, runs into the unreported lorry, reports it, re-routes and still arrives.
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Engine 4" && r.Claim.Contains("MacMahon Bridge blocked"));
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Engine 4 delayed: re-routed");
        Assert.Contains(harness.Cop.Units.Single(u => u.Callsign == "Engine 4").Status, new[] { UnitStatus.OnScene, UnitStatus.Operating });
    }

    private static async Task Step(TestHarness harness, TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(1))
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));
    }

    [RoadDataFact]
    public void Peak_traffic_slows_the_same_route()
    {
        var network = DublinRoads.Network!;
        var night = network.Route(Station8, BarrowStreetScenario.FireLocation, new RouteOptions(UnitType.Engine, Night), new TimeOfDayTraffic())!;
        var peak = network.Route(Station8, BarrowStreetScenario.FireLocation, new RouteOptions(UnitType.Engine, MorningPeak), new TimeOfDayTraffic())!;

        output.WriteLine($"Night {night.Duration.TotalMinutes:F1} min, peak {peak.Duration.TotalMinutes:F1} min");
        Assert.True(peak.Duration > night.Duration * 1.2);
    }

    [RoadDataFact]
    public void Scenario_obstruction_blocks_a_real_road_and_forces_a_longer_route()
    {
        var network = DublinRoads.Network!;
        var obstruction = Wgs84.Factory.CreateLineString(
            BarrowStreetScenario.ObstructionLine.Select(p => p.ToPoint().Coordinate).ToArray());

        var blocked = network.BlockedEdges([obstruction]);
        var names = blocked.Select(id => network.Edge(id).Name).Where(n => n is not null).Distinct().ToList();
        output.WriteLine($"Blocked {blocked.Count} edges on: {string.Join(", ", names)}");

        var normal = network.Route(Station3, BarrowStreetScenario.FireLocation, new RouteOptions(UnitType.Engine, Night), new FreeFlowTraffic())!;
        var detour = network.Route(Station3, BarrowStreetScenario.FireLocation,
            new RouteOptions(UnitType.Engine, Night, Avoid: [obstruction]), new FreeFlowTraffic())!;
        output.WriteLine($"Normal {normal.Duration.TotalMinutes:F1} min, detour {detour.Duration.TotalMinutes:F1} min");

        Assert.NotEmpty(blocked);
        Assert.Contains(normal.Legs, l => blocked.Contains(l.EdgeId)); // the normal route really uses that road
        Assert.DoesNotContain(detour.Legs, l => blocked.Contains(l.EdgeId));
        Assert.True(detour.Duration > normal.Duration);
    }
}
