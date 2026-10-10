using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 6: the basic hazard models on their own.</summary>
public class HazardModelTests
{
    private static readonly GeoPoint Origin = TestHarness.Dublin;
    private static readonly DateTimeOffset Start = TestHarness.Start;

    /// <summary>Wind from the west: it blows towards the east.</summary>
    private static readonly WorldWeather Westerly = new(WindFromDegrees: 270, WindSpeedMps: 7, TemperatureC: 14, RelativeHumidity: 0.7);

    private static void Run(IHazardModel model, TimeSpan total, WorldWeather weather, double rate = 0,
        IReadOnlyList<Suppression>? suppression = null, DateTimeOffset? from = null)
    {
        var at = from ?? Start;
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(10))
        {
            at += TimeSpan.FromSeconds(10);
            model.Step(new HazardStepInput(at, weather, rate, suppression), TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>How far the fire's extent (burning or burnt out) reaches from the origin towards a bearing.</summary>
    private static double Reach(FireSpreadModel fire, double bearing)
    {
        var footprint = fire.Footprint();
        var reach = 0.0;
        for (var d = 0.0; d <= 700; d += 5)
        {
            var point = GeoMath.Destination(Origin, bearing, d).ToPoint();
            if (footprint.Any(p => p.Contains(point))) reach = d;
        }
        return reach;
    }

    // ---- Fire ----

    [Fact]
    public void Fire_spreads_much_faster_downwind_than_upwind()
    {
        var fire = new FireSpreadModel(Origin, 0.4, new UniformTerrain(1.0), seed: 7);
        Run(fire, TimeSpan.FromMinutes(20), Westerly);

        var east = Reach(fire, 90);
        var west = Reach(fire, 270);
        Assert.True(fire.IsActive);
        Assert.True(east > 2 * west, $"downwind {east} m vs upwind {west} m");
        // Building-to-building spread: tens of metres in 20 minutes, not hundreds.
        Assert.InRange(east, 60, 250);
        Assert.Equal("E", fire.SpreadDirection());
    }

    /// <summary>A canal between the fire and the next block: water carries no fire.</summary>
    private sealed class CanalTerrain(double fromEast, double toEast) : IHazardTerrain
    {
        private readonly LocalGrid _plane = new(Origin, 1, 0);

        public double Fuel(GeoPoint point)
        {
            var (east, _) = _plane.ToLocal(point);
            return east >= fromEast && east <= toEast ? 0 : 1;
        }

        public double Elevation(GeoPoint point) => 0;
        public bool IsBuilding(GeoPoint point) => Fuel(point) > 0;
    }

    [Fact]
    public void Fire_does_not_cross_water()
    {
        var fire = new FireSpreadModel(Origin, 0.4, new CanalTerrain(40, 80), seed: 3);
        Run(fire, TimeSpan.FromMinutes(25), Westerly);

        Assert.True(Reach(fire, 90) <= 40, "the fire stopped at the canal bank");
        // The far bank can see the smoke, but nothing there is burning.
        Assert.True(fire.DistanceToFire(GeoMath.Destination(Origin, 90, 150)) > 100);
    }

    [Fact]
    public void Fire_crews_put_the_fire_out_and_without_them_it_keeps_growing()
    {
        var crews = Enumerable.Range(0, 3).Select(_ => new Suppression(Origin, 90, 1.5)).ToList();
        var fought = new FireSpreadModel(Origin, 0.4, new UniformTerrain(0.8), seed: 11);
        var unfought = new FireSpreadModel(Origin, 0.4, new UniformTerrain(0.8), seed: 11);

        Run(fought, TimeSpan.FromMinutes(20), Westerly with { WindSpeedMps = 3 }, suppression: crews);
        Run(unfought, TimeSpan.FromMinutes(10), Westerly with { WindSpeedMps = 3 });
        var halfway = unfought.AreaSquareMeters;
        Run(unfought, TimeSpan.FromMinutes(10), Westerly with { WindSpeedMps = 3 }, from: Start.AddMinutes(10));

        Assert.False(fought.IsActive);
        Assert.True(unfought.IsActive);
        Assert.True(unfought.AreaSquareMeters > halfway);
        Assert.True(unfought.AreaSquareMeters > fought.AreaSquareMeters);
    }

    [Fact]
    public void Fire_exposure_is_graded_by_distance()
    {
        var fire = new FireSpreadModel(Origin, 0.7, new UniformTerrain(1.0), seed: 1);

        Assert.Equal(HazardLevel.High, fire.ExposureAt(Origin).Level);
        Assert.Equal(HazardLevel.Moderate, fire.ExposureAt(GeoMath.Destination(Origin, 0, 50)).Level);
        Assert.Equal(HazardLevel.Low, fire.ExposureAt(GeoMath.Destination(Origin, 0, 150)).Level);
        Assert.Equal(HazardLevel.None, fire.ExposureAt(GeoMath.Destination(Origin, 0, 400)).Level);
    }

    // ---- Plume ----

    [Fact]
    public void Plume_lies_downwind_and_grows_when_the_release_rate_rises()
    {
        var plume = new PlumeModel(Origin, "chlorine");
        var light = Westerly with { WindSpeedMps = 3 };
        Run(plume, TimeSpan.FromMinutes(10), light, rate: 0.5);

        Assert.True(plume.IsActive);
        Assert.NotEqual(HazardLevel.None, plume.ExposureAt(GeoMath.Destination(Origin, 90, 400)).Level);
        Assert.Equal(HazardLevel.None, plume.ExposureAt(GeoMath.Destination(Origin, 270, 400)).Level);
        Assert.Equal(HazardLevel.None, plume.ExposureAt(GeoMath.Destination(Origin, 0, 400)).Level);
        Assert.Equal(3, plume.Zones.Count); // low, moderate, high
        var before = plume.AreaSquareMeters;

        Run(plume, TimeSpan.FromMinutes(1), light, rate: 2.0, from: Start.AddMinutes(10));
        Assert.True(plume.AreaSquareMeters > 2 * before, $"{before:N0} m² → {plume.AreaSquareMeters:N0} m²");
    }

    [Fact]
    public void Plume_only_reaches_as_far_as_the_wind_has_carried_it()
    {
        var plume = new PlumeModel(Origin, "chlorine");
        Run(plume, TimeSpan.FromSeconds(30), Westerly with { WindSpeedMps = 3 }, rate: 1);

        Assert.InRange(plume.FrontMeters, 80, 100);
        Assert.NotEqual(HazardLevel.None, plume.ExposureAt(GeoMath.Destination(Origin, 90, 50)).Level);
        Assert.Equal(HazardLevel.None, plume.ExposureAt(GeoMath.Destination(Origin, 90, 300)).Level);
    }

    [Fact]
    public void Plume_swings_with_the_wind_and_clears_after_the_release_stops()
    {
        var plume = new PlumeModel(Origin, "chlorine");
        Run(plume, TimeSpan.FromMinutes(10), Westerly with { WindSpeedMps = 3 }, rate: 1);
        var east = GeoMath.Destination(Origin, 90, 400);
        var south = GeoMath.Destination(Origin, 180, 400);
        Assert.NotEqual(HazardLevel.None, plume.ExposureAt(east).Level);

        var northerly = Westerly with { WindFromDegrees = 0, WindSpeedMps = 3 };
        Run(plume, TimeSpan.FromSeconds(10), northerly, rate: 1, from: Start.AddMinutes(10));
        Assert.Equal(HazardLevel.None, plume.ExposureAt(east).Level);
        Assert.NotEqual(HazardLevel.None, plume.ExposureAt(south).Level);

        // Release stops: the cloud thins and is gone within a few minutes.
        Run(plume, TimeSpan.FromMinutes(10), northerly, rate: 0, from: Start.AddMinutes(11));
        Assert.False(plume.IsActive);
        Assert.Equal(HazardLevel.None, plume.ExposureAt(south).Level);
    }

    [Fact]
    public void Substance_thresholds_rank_low_to_high()
    {
        Assert.True(Substance.Chlorine.Low < Substance.Chlorine.Moderate && Substance.Chlorine.Moderate < Substance.Chlorine.High);
        Assert.Same(Substance.Ammonia, Substance.For("Anhydrous ammonia"));
        Assert.Equal("acid fumes", Substance.For("acid fumes").Name);
    }

    // ---- Flood ----

    [Fact]
    public void Flood_water_runs_downhill()
    {
        // Ground falls away to the east by 1 m every 100 m.
        var plane = new LocalGrid(Origin, 1, 0);
        var flood = new FloodModel(Origin, new UniformTerrain(elevation: p => -plane.ToLocal(p).East / 100));
        Run(flood, TimeSpan.FromMinutes(30), Westerly, rate: 3);

        var downhill = flood.DepthAt(GeoMath.Destination(Origin, 90, 300));
        var uphill = flood.DepthAt(GeoMath.Destination(Origin, 270, 300));
        // A thin sheet runs away downhill; nothing climbs the slope.
        Assert.True(downhill > 0.005, $"downhill {downhill:F3} m");
        Assert.True(downhill > 10 * Math.Max(uphill, 0.0001), $"downhill {downhill:F3} m, uphill {uphill:F3} m");
        Assert.True(flood.IsActive);
    }

    [Fact]
    public void Flood_ponds_in_a_hollow_and_drains_once_the_inflow_stops()
    {
        var flood = new FloodModel(Origin, new UniformTerrain(elevation: p => GeoMath.DistanceMeters(p, Origin) * 0.02));
        Run(flood, TimeSpan.FromMinutes(20), Westerly, rate: 5);
        Assert.True(flood.MaxDepth >= FloodModel.ImpassableDepth);
        Assert.Contains(flood.CellsDeeperThan(FloodModel.ImpassableDepth), c => c.Depth > 0);
        Assert.Equal(HazardLevel.High, flood.ExposureAt(Origin).Level);

        var deepest = flood.MaxDepth;
        Run(flood, TimeSpan.FromMinutes(30), Westerly, rate: 0, from: Start.AddMinutes(20));
        Assert.True(flood.MaxDepth < deepest);
    }

    // ---- Grid ----

    [Fact]
    public void Local_grid_round_trips_cells_and_merges_outlines()
    {
        var grid = new LocalGrid(Origin, 15, 10);
        Assert.True(grid.TryIndex(GeoMath.Destination(Origin, 90, 31), out var index));
        var (east, north) = grid.LocalCentre(index);
        Assert.Equal(30, east, 3);
        Assert.Equal(0, north, 3);

        // Two neighbouring cells merge into one 30 × 15 m polygon.
        var outline = grid.Outline([grid.Index(10, 10), grid.Index(11, 10)]);
        var polygon = Assert.Single(outline);
        Assert.Equal(450, MetricProjection.For(Origin).AreaSquareMeters(polygon), 450 * 0.01);
    }
}
