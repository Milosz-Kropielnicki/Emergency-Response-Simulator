using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 3: routing, AVL feed, derived alerts, AVL + GIS integration.</summary>
public class AvlRoutingTests
{
    internal const double Spacing = 200; // metres between grid streets

    /// <summary>
    /// A 6 × 6 grid of two-way local streets 200 m apart, south-west corner at <see cref="TestHarness.Dublin"/>.
    /// Row r runs east–west, column c runs north–south. Row 3 is a one-way primary road eastbound.
    /// </summary>
    internal static RoadNetwork Grid(Action<List<GisFeature>>? customise = null)
    {
        var features = new List<GisFeature>();
        for (var r = 0; r < 6; r++)
        {
            var oneway = r == 3;
            features.Add(Road($"Row {r}", Enumerable.Range(0, 6).Select(c => At(r, c)), oneway ? "primary" : "residential",
                oneway ? "yes" : null));
        }
        for (var c = 0; c < 6; c++)
            features.Add(Road($"Col {c}", Enumerable.Range(0, 6).Select(r => At(r, c)), "residential"));
        customise?.Invoke(features);
        return RoadNetwork.Build(features);
    }

    internal static GeoPoint At(int row, int col) =>
        GeoMath.Destination(GeoMath.Destination(TestHarness.Dublin, 0, row * Spacing), 90, col * Spacing);

    internal static GisFeature Road(string name, IEnumerable<GeoPoint> points, string highway, string? oneway = null)
    {
        var feature = new GisFeature
        {
            Name = name,
            Geometry = Wgs84.Factory.CreateLineString(points.Select(p => p.ToPoint().Coordinate).ToArray()),
            Properties = { ["highway"] = highway },
        };
        if (oneway is not null) feature.Properties["oneway"] = oneway;
        return feature;
    }

    private static RouteOptions Options(params Geometry[] avoid) =>
        new(UnitType.Engine, TestHarness.Start, Emergency: true, Avoid: avoid);

    private static Geometry Line(GeoPoint a, GeoPoint b) =>
        Wgs84.Factory.CreateLineString([a.ToPoint().Coordinate, b.ToPoint().Coordinate]);

    /// <summary>A short north–south line across row <paramref name="row"/>, halfway between column <paramref name="col"/> and the next.</summary>
    private static GeoPoint[] Across(int row, int col)
    {
        var mid = GeoMath.Destination(At(row, col), 90, Spacing / 2);
        return [GeoMath.Destination(mid, 180, 25), GeoMath.Destination(mid, 0, 25)];
    }

    // ---- Routing ----

    [Fact]
    public void Routes_follow_the_grid_and_prefer_the_faster_road()
    {
        var network = Grid();
        Assert.Equal(36, network.NodeCount);

        var route = network.Route(At(0, 0), At(3, 5), Options(), new FreeFlowTraffic())!;

        // 3 blocks north + 5 east = 1600 m; the primary road on row 3 is faster than residential rows.
        Assert.Equal(1600, route.DistanceMeters, 5.0);
        Assert.Contains(route.Legs, l => l.RoadName == "Row 3");
        Assert.All(route.Legs, l => Assert.True(l.EdgeId >= 0));
    }

    [Fact]
    public void One_way_streets_are_only_driven_against_when_nothing_else_works()
    {
        var network = Grid();
        var westbound = network.Route(At(3, 5), At(3, 0), Options(), new FreeFlowTraffic())!;
        Assert.DoesNotContain(westbound.Legs, l => l.RoadName == "Row 3"); // goes round via another row

        // A dead-end with only the one-way road: contraflow is used rather than failing.
        var island = RoadNetwork.Build([Road("Oneway", [At(0, 0), At(0, 1)], "residential", "yes")]);
        var against = island.Route(At(0, 1), At(0, 0), Options(), new FreeFlowTraffic());
        Assert.NotNull(against);
    }

    [Fact]
    public void Declared_closures_are_avoided_and_cost_time()
    {
        var network = Grid();
        var direct = network.Route(At(0, 0), At(0, 5), Options(), new FreeFlowTraffic())!;

        // Close row 0 between columns 2 and 3 with a line across it.
        var across = Across(0, 2);
        var closure = Line(across[0], across[1]);
        var detour = network.Route(At(0, 0), At(0, 5), Options(closure), new FreeFlowTraffic())!;

        Assert.Equal(1000, direct.DistanceMeters, 5.0);
        Assert.Equal(1400, detour.DistanceMeters, 5.0); // up one block, along, back down
        Assert.True(detour.Duration > direct.Duration);
    }

    [Fact]
    public void Off_network_points_connect_to_the_nearest_road()
    {
        var network = Grid();
        var offRoad = GeoMath.Destination(At(1, 1), 45, 40);

        var route = network.Route(offRoad, At(4, 4), Options(), new FreeFlowTraffic())!;

        Assert.Equal(-1, route.Legs[0].EdgeId);
        Assert.Equal(RoadNetwork.OffRoadSpeedKph, route.Legs[0].SpeedKph);
        Assert.Equal(offRoad, route.Path[0]);
    }

    [Theory]
    [InlineData(null, RoadClass.Primary, 55)]
    [InlineData("30", RoadClass.Primary, 37.5)]
    [InlineData("20 mph", RoadClass.Primary, 20 * 1.609 * 1.25)]
    [InlineData("30 mph", RoadClass.Local, 35)] // limit plus margin exceeds the class speed
    [InlineData("signals", RoadClass.Local, 35)]
    public void Speeds_come_from_road_class_capped_near_the_posted_limit(string? maxspeed, RoadClass roadClass, double expected) =>
        Assert.Equal(expected, RoadNetwork.SpeedFor(roadClass, maxspeed), 3);

    [Fact]
    public void Peak_traffic_slows_main_roads_most_and_blue_lights_recover_some_delay()
    {
        var traffic = new TimeOfDayTraffic();
        var peak = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 7, 8, 0, 0)));
        var night = peak.AddHours(-6);

        Assert.Equal(1.0, traffic.SpeedFactor(RoadClass.Primary, night, emergency: false));
        Assert.Equal(0.45, traffic.SpeedFactor(RoadClass.Primary, peak, emergency: false));
        Assert.True(traffic.SpeedFactor(RoadClass.Local, peak, false) > traffic.SpeedFactor(RoadClass.Primary, peak, false));
        Assert.Equal(Math.Sqrt(0.45), traffic.SpeedFactor(RoadClass.Primary, peak, emergency: true), 6);
    }

    // ---- Units driving routes ----

    private static async Task<(TestHarness Harness, Guid Unit, Guid Incident)> Responding(RoadNetwork network, GeoPoint station, GeoPoint target)
    {
        var routing = new RoutingService(new FreeFlowTraffic(), new ServiceCollection().BuildServiceProvider());
        routing.Use(network);
        var harness = new TestHarness(cop => [new UnitResponseSystem(routing), new AttentionMonitor(cop, new AttentionOptions(), routing)]);
        var unit = Guid.NewGuid();
        var incident = Guid.NewGuid();
        await harness.Perceived(new UnitRegistered(unit, "Engine 12", UnitType.Engine, null, station, "Station", 5, []));
        await harness.Perceived(new IncidentCreated(incident, "INC-1", IncidentType.StructureFire, IncidentPriority.High, target, null, null));
        await harness.C2.DispatchAsync(unit, incident);
        return (harness, unit, incident);
    }

    [Fact]
    public async Task Units_drive_the_road_network_and_report_route_position_speed_heading_and_eta()
    {
        var (harness, unit, _) = await Responding(Grid(), At(0, 0), At(0, 5));
        await Step(harness, TimeSpan.FromSeconds(60));

        var cop = harness.Cop.FindUnit(unit)!;
        Assert.Equal(UnitStatus.EnRoute, cop.Status);
        Assert.Equal(1000, cop.RouteDistanceMeters!.Value, 5.0);
        Assert.NotNull(cop.PlannedRoute);
        Assert.True(cop.SpeedKph > 0);
        Assert.Equal("E", GeoMath.CompassPoint(cop.Heading));

        // Every fix lies on row 0, the road being driven.
        var fixes = (await harness.Aar.GetTimelineAsync(harness.Engine.SessionId, false))
            .Select(e => e.Payload).OfType<UnitPositionReported>().ToList();
        Assert.True(fixes.Count >= 2);
        Assert.All(fixes, f => Assert.Equal(At(0, 0).Latitude, f.Location.Latitude, 4));

        await Step(harness, TimeSpan.FromMinutes(3));
        Assert.Contains(harness.Cop.FindUnit(unit)!.Status, new[] { UnitStatus.OnScene, UnitStatus.Operating });
        Assert.True(GeoMath.DistanceMeters(GeoPoint.FromPoint(harness.Cop.FindUnit(unit)!.Location!), At(0, 5)) < 1);
    }

    [Fact]
    public async Task An_unreported_obstruction_stops_the_unit_which_reports_it_and_re_routes_later()
    {
        var (harness, unit, _) = await Responding(Grid(), At(0, 0), At(0, 5));
        await harness.Truth(new RoadObstructed(Guid.NewGuid(), Across(0, 2), "lorry shed its load"));
        var firstEta = (TimeSpan?)null;

        // Turnout, then drive two blocks to the obstruction and stop there.
        for (var i = 0; i < 120 && harness.World.Units[unit].HeldUpSince is null; i++)
        {
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));
            firstEta ??= harness.Cop.FindUnit(unit)!.Eta;
        }
        Assert.NotNull(harness.World.Units[unit].HeldUpSince);
        Assert.Equal(0, harness.Cop.FindUnit(unit)!.SpeedKph);

        await Step(harness, UnitResponseSystem.ObstructionAssessTime + TimeSpan.FromSeconds(2));

        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Engine 12" && r.Claim.Contains("blocked") && r.Claim.Contains("Re-routing"));
        Assert.Equal(1400 - 400, harness.Cop.FindUnit(unit)!.RouteDistanceMeters!.Value, 60.0); // detour from the obstruction
        Assert.DoesNotContain(harness.Cop.Alerts, a => a.Title.Contains("stopped en route")); // assessed within 90 s
        Assert.NotNull(firstEta);
    }

    [Fact]
    public async Task A_closure_declared_mid_journey_makes_units_re_plan_and_command_is_told_of_the_delay()
    {
        var (harness, unit, _) = await Responding(Grid(), At(0, 0), At(0, 5));
        await Step(harness, TimeSpan.FromSeconds(50));
        var before = harness.Cop.FindUnit(unit)!.RouteDistanceMeters!.Value;

        // Close row 0 ahead of the unit, forcing a detour.
        await harness.C2.DeclareZoneAsync(ZoneType.RoadClosure, "Closure", Across(0, 3));
        await Step(harness, TimeSpan.FromSeconds(2));

        var after = harness.Cop.FindUnit(unit)!;
        Assert.True(after.RouteDistanceMeters > before - 60); // longer route despite having already driven some way
        var timeline = await harness.Aar.GetTimelineAsync(harness.Engine.SessionId, false);
        Assert.Contains(timeline, e => e.Payload is RouteReported { Reason: "Re-routed: road closure declared" });
    }

    [Fact]
    public async Task A_broken_down_unit_shows_stopped_on_avl_then_reports_and_goes_out_of_service()
    {
        var (harness, unit, _) = await Responding(Grid(), At(0, 0), At(5, 5));
        await Step(harness, TimeSpan.FromSeconds(60));
        await harness.Truth(new UnitBrokeDown(unit, "engine overheating"));

        await Step(harness, TimeSpan.FromSeconds(100));
        var stopped = Assert.Single(harness.Cop.Alerts, a => a.Title == "Engine 12 stopped en route");
        Assert.Equal(unit, stopped.UnitId);
        Assert.Equal(UnitStatus.EnRoute, harness.Cop.FindUnit(unit)!.Status); // command doesn't know why yet

        await Step(harness, TimeSpan.FromSeconds(30));
        Assert.Equal(UnitStatus.OutOfService, harness.Cop.FindUnit(unit)!.Status);
        Assert.Contains(harness.Cop.Reports, r => r.Claim.Contains("engine overheating"));
    }

    [Fact]
    public async Task Avl_loss_is_flagged_within_a_minute_long_before_the_comms_timeout()
    {
        var (harness, unit, _) = await Responding(Grid(), At(0, 0), At(5, 5));
        await Step(harness, TimeSpan.FromSeconds(55));
        await harness.Truth(new UnitRadioFailed(unit, Failed: true));

        await Step(harness, TimeSpan.FromSeconds(70));

        var lost = Assert.Single(harness.Cop.Alerts, a => a.Title == "Engine 12: AVL signal lost");
        Assert.Equal(AlertCategory.CommunicationFailure, lost.Category);
        Assert.DoesNotContain(harness.Cop.Alerts, a => a.Title.Contains("not heard from")); // that comes at 4 min
    }

    [Fact]
    public async Task Fixes_far_from_the_reported_route_raise_a_deviation_alert()
    {
        var (harness, unit, _) = await Responding(Grid(), At(0, 0), At(0, 5));
        await Step(harness, TimeSpan.FromSeconds(50));

        // Two fixes 400 m north of the planned route along row 0.
        foreach (var _ in new[] { 1, 2 })
        {
            await harness.Perceived(new UnitPositionReported(unit, At(2, 1), 40, 0, TimeSpan.FromMinutes(3)));
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Single(harness.Cop.Alerts, a => a.Title == "Engine 12 off planned route");
    }

    [Fact]
    public async Task Avl_service_keeps_the_latest_fix_and_a_moving_trail()
    {
        var harness = new TestHarness();
        var avl = new AvlService(harness.Cop, harness.Store, harness.Engine.SessionId);
        var unit = Guid.NewGuid();
        await harness.Perceived(new UnitRegistered(unit, "Engine 12", UnitType.Engine, null, At(0, 0), null, 4, []));

        foreach (var col in new[] { 0, 0, 1, 2 }) // a stationary repeat is not added to the trail
            await harness.Perceived(new UnitPositionReported(unit, At(0, col), 40, 90, null));

        Assert.Equal(At(0, 2), avl.GetLatest(unit)!.Location);
        Assert.Equal(3, avl.GetTrail(unit).Count);
        Assert.Single(avl.LatestFixes);
    }

    private static async Task Step(TestHarness harness, TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(1))
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));
    }
}
