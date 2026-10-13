using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 13: the dispersion model drives real releases, command can project one on what it knows, and the projection
/// says what it covers on the ground. The Python service has matching tests of the model itself; these are about the
/// engine using it, falling back without it, and replaying it.
/// </summary>
public class PlumeModelTests
{
    private static readonly GeoPoint Release = new(53.3395, -6.2365); // Barrow Street

    /// <summary>A hazard-model service that answers from a script, so tests do not need Python running.</summary>
    private sealed class FakePlumeService : IPlumeService
    {
        private readonly Func<PlumeRequest, PlumePrediction> _answer;

        public FakePlumeService(Func<PlumeRequest, PlumePrediction>? answer = null) =>
            _answer = answer ?? (request => Prediction(request));

        public List<PlumeRequest> Requests { get; } = [];
        public Exception? Fails { get; set; }

        public Task<PlumePrediction> PredictAsync(PlumeRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Fails is not null ? Task.FromException<PlumePrediction>(Fails) : Task.FromResult(_answer(request));
        }

        public Task<IReadOnlyList<SubstanceInfo>> GetSubstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubstanceInfo>>([Chlorine]);

        private static readonly SubstanceInfo Chlorine =
            new("chlorine", "chemical", 1.5, 5.8, 58, 70.9, null, "Heavier than air: it collects in low ground.");

        /// <summary>
        /// Three nested rectangles stretching downwind of the release, far enough apart to tell apart on a map. Shapes,
        /// not physics: the real maths is tested on the Python side.
        /// </summary>
        public static PlumePrediction Prediction(PlumeRequest request, double scale = 1.0)
        {
            var downwind = (request.Weather.WindFromDegrees + 180) % 360;
            PlumeZone Zone(string level, double length, double halfWidth, double threshold) =>
                new(level, Box(request.ReleaseLocation, downwind, length * scale, halfWidth * scale), threshold,
                    null, length * scale * halfWidth * scale * 2, length * scale, 60);

            return new PlumePrediction(
                "gaussian-plume-v1", DateTimeOffset.UnixEpoch,
                [Zone(PlumeLevels.High, 300, 60, 58), Zone(PlumeLevels.Moderate, 900, 180, 5.8), Zone(PlumeLevels.Low, 2400, 400, 1.5)],
                Chlorine, FrontMeters: 2400 * scale, EffectiveHeightMeters: 0, DenseGas: true, StabilityClass: request.Weather.StabilityClass,
                Urban: true);
        }

        /// <summary>A rectangle from the release, `length` metres downwind and `halfWidth` either side.</summary>
        private static IReadOnlyList<GeoPoint> Box(GeoPoint origin, double downwind, double length, double halfWidth)
        {
            var left = (downwind + 270) % 360;
            var right = (downwind + 90) % 360;
            var nearLeft = GeoMath.Destination(origin, left, halfWidth);
            var nearRight = GeoMath.Destination(origin, right, halfWidth);
            var farLeft = GeoMath.Destination(GeoMath.Destination(origin, downwind, length), left, halfWidth);
            var farRight = GeoMath.Destination(GeoMath.Destination(origin, downwind, length), right, halfWidth);
            return [nearLeft, farLeft, farRight, nearRight, nearLeft];
        }
    }

    /// <summary>A GIS database holding a few named places, for the overlay.</summary>
    private sealed class FakeGisService : IGisService
    {
        /// <summary>Built once, so a feature keeps its id across queries as it does in the database.</summary>
        private readonly List<(string Layer, GisFeature Feature)> _features;

        public FakeGisService(params (string Layer, string Name, GeoPoint At)[] features) =>
            _features = [.. features.Select(f => (f.Layer, new GisFeature
            {
                Id = Guid.NewGuid(), Name = f.Name, Geometry = f.At.ToPoint(),
                Properties = f.Layer == GisLayerKeys.Buildings ? new() { ["building"] = "residential" } : [],
            }))];

        public Task<IReadOnlyList<GisFeature>> FindFeaturesWithinAsync(
            Geometry area, IReadOnlyCollection<string> layerKeys, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GisFeature>>(
                [.. _features.Where(f => layerKeys.Contains(f.Layer) && area.Intersects(f.Feature.Geometry)).Select(f => f.Feature)]);

        public Task<IReadOnlyList<GisLayer>> GetLayersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GisLayer>>([]);

        public Task<IReadOnlyList<GisFeature>> GetFeaturesAsync(string layerKey, BoundingBox bounds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GisFeature>>([]);

        public Task<IReadOnlyList<GisFeature>> GetAllFeaturesAsync(string layerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GisFeature>>([]);

        public Task<IReadOnlyList<NearestFeature>> FindNearestAsync(
            GeoPoint point, IReadOnlyCollection<string> layerKeys, int maxResults = 5, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NearestFeature>>([]);

        public Task<double?> GetElevationAsync(GeoPoint point, CancellationToken cancellationToken = default) =>
            Task.FromResult<double?>(null);
    }

    private static TestHarness Harness(IPlumeService? plume, IGisService? gis = null) =>
        new(cop => [new PlumeDispersionSystem(plume, cop, gis), new HazardSystem()], plume, gis);

    /// <summary>Starts a real chlorine release, as a hazmat incident or a chemical store would.</summary>
    private static async Task<Guid> ReleaseAsync(TestHarness harness, double rate = 0.4, string substance = "chlorine")
    {
        var hazardId = Guid.NewGuid();
        await harness.Truth(new HazardStarted(hazardId, null, HazardKind.Plume, Release, "Chlorine release", rate, substance, 1500));
        return hazardId;
    }

    private static PlumeModel ModelOf(TestHarness harness, Guid hazardId) =>
        (PlumeModel)harness.World.Hazards[hazardId].Model!;

    /// <summary>
    /// Steps until the service's answer has been recorded and the model has adopted it (a tick after it is recorded,
    /// since events emitted in a tick apply at its end), or gives up.
    /// </summary>
    private static Task<bool> PredictedAsync(TestHarness harness, Guid hazardId) =>
        harness.RunUntilAsync(() => harness.World.Hazards[hazardId] is { Prediction: not null, Model: PlumeModel { UsingService: true } },
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5));

    // ---- Ground truth ----

    [Fact]
    public async Task The_dispersion_service_drives_the_real_cloud_and_the_engine_adopts_its_zones()
    {
        var plume = new FakePlumeService();
        var harness = Harness(plume);
        var hazardId = await ReleaseAsync(harness);

        Assert.True(await PredictedAsync(harness, hazardId), "the prediction never arrived");
        var predicted = Assert.Single(await harness.EventsAsync(), e => e.Payload is PlumePredicted).Payload as PlumePredicted;
        Assert.NotNull(predicted);
        Assert.Equal("gaussian-plume-v1", predicted.Model);
        Assert.Equal("first prediction", predicted.Reason);
        Assert.Equal("chemical", predicted.MaterialClass);
        Assert.True(predicted.DenseGas);
        Assert.Equal(3, predicted.Zones.Count);

        // Truth only: a trainee never sees where the cloud really is.
        Assert.Equal(EventVisibility.Truth, Assert.Single(await harness.EventsAsync(), e => e.Payload is PlumePredicted).Visibility);

        // The model now answers from the service's zones rather than its own maths.
        var model = ModelOf(harness, hazardId);
        Assert.True(model.UsingService);
        Assert.Equal("gaussian-plume-v1", model.ModelName);
        Assert.Equal(3, model.Zones.Count);
        Assert.NotEmpty(model.Footprint());

        // And so does exposure: 200 m downwind (east, for a westerly wind) is in the high zone.
        var inside = GeoMath.Destination(Release, 90, 200);
        Assert.Equal(HazardLevel.High, model.ExposureAt(inside).Level);
        Assert.Equal(58, model.ExposureAt(inside).Value); // at least the zone's threshold
        Assert.Equal(HazardLevel.None, model.ExposureAt(GeoMath.Destination(Release, 270, 500)).Level); // upwind is clear
    }

    [Fact]
    public async Task The_request_describes_the_real_release_and_the_real_weather()
    {
        var plume = new FakePlumeService();
        var harness = Harness(plume);
        var hazardId = await ReleaseAsync(harness, rate: 0.4);
        Assert.True(await PredictedAsync(harness, hazardId));

        var request = plume.Requests[0];
        Assert.Equal("chlorine", request.Substance);
        Assert.Equal(0.4, request.ReleaseRateKgPerSecond);
        Assert.Equal(1500, request.QuantityKg);
        Assert.Equal(harness.World.Weather.WindFromDegrees, request.Weather.WindFromDegrees);
        Assert.Equal(harness.World.Weather.WindSpeedMps, request.Weather.WindSpeedMps);
        Assert.NotNull(request.ElapsedMinutes);
        Assert.Contains(request.Weather.StabilityClass, new[] { "A", "B", "C", "D", "E", "F" });
    }

    [Fact]
    public async Task Without_the_service_the_in_engine_plume_carries_the_session()
    {
        // No service at all.
        var harness = Harness(plume: null);
        var hazardId = await ReleaseAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(5));

        var model = ModelOf(harness, hazardId);
        Assert.False(model.UsingService);
        Assert.Equal(PlumeModel.InEngineModel, model.ModelName);
        Assert.NotEmpty(model.Zones); // its own maths still produce a cloud
        Assert.DoesNotContain(await harness.EventsAsync(), e => e.Payload is PlumePredicted);
        Assert.Equal(HazardLevel.High, model.ExposureAt(GeoMath.Destination(Release, 90, 60)).Level);
    }

    [Fact]
    public async Task A_service_that_fails_is_tried_again_and_never_stalls_the_simulation()
    {
        var plume = new FakePlumeService { Fails = new HttpRequestException("connection refused") };
        var harness = Harness(plume);
        var hazardId = await ReleaseAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(6));

        Assert.DoesNotContain(await harness.EventsAsync(), e => e.Payload is PlumePredicted);
        Assert.True(plume.Requests.Count > 1, "it should keep trying");
        Assert.False(ModelOf(harness, hazardId).UsingService); // the fallback kept the cloud going
        Assert.NotEmpty(ModelOf(harness, hazardId).Zones);

        // Once it comes back, the service takes over.
        plume.Fails = null;
        Assert.True(await PredictedAsync(harness, hazardId));
        Assert.True(ModelOf(harness, hazardId).UsingService);
    }

    [Fact]
    public async Task A_wind_shift_and_a_revised_release_rate_each_bring_a_fresh_prediction()
    {
        var plume = new FakePlumeService();
        var harness = Harness(plume);
        var hazardId = await ReleaseAsync(harness);
        Assert.True(await PredictedAsync(harness, hazardId));

        // The wind backs 90°: the cloud is going somewhere else now.
        harness.World.Weather = harness.World.Weather with { WindFromDegrees = 180 };
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        var afterWind = (await harness.EventsAsync()).Select(e => e.Payload).OfType<PlumePredicted>().Last();
        Assert.Contains("wind shifted", afterWind.Reason);

        // Crews get the valve part-closed: a slower release.
        await harness.Truth(new HazardRateChanged(hazardId, 0.1, "Valve partly closed"));
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        var afterRate = (await harness.EventsAsync()).Select(e => e.Payload).OfType<PlumePredicted>().Last();
        Assert.Contains("release rate revised", afterRate.Reason);
        Assert.Equal(0.1, plume.Requests[^1].ReleaseRateKgPerSecond);

        // And it keeps asking as the cloud travels, without anything changing.
        var predictions = (await harness.EventsAsync()).Count(e => e.Payload is PlumePredicted);
        await harness.RunAsync(PlumeDispersionSystem.Interval * 2);
        Assert.True((await harness.EventsAsync()).Count(e => e.Payload is PlumePredicted) > predictions);
        Assert.Contains((await harness.EventsAsync()).Select(e => e.Payload).OfType<PlumePredicted>(), p => p.Reason == "the cloud has moved on");
    }

    [Fact]
    public async Task A_stopped_release_is_still_modelled_so_the_cloud_drifts_away()
    {
        var plume = new FakePlumeService();
        var harness = Harness(plume);
        var hazardId = await ReleaseAsync(harness);
        Assert.True(await PredictedAsync(harness, hazardId));

        await harness.Truth(new HazardRateChanged(hazardId, 0, "Store empty: the release has stopped"));
        await harness.RunAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("the release has stopped", (await harness.EventsAsync()).Select(e => e.Payload).OfType<PlumePredicted>().Last().Reason);
        var stopped = plume.Requests[^1];

        // It keeps asking, with the duration frozen at how long it actually ran and the elapsed time still growing,
        // which is what tells the model the cloud has left the source behind.
        await harness.RunAsync(PlumeDispersionSystem.Interval + TimeSpan.FromSeconds(10));
        var later = plume.Requests[^1];
        Assert.Equal(stopped.DurationMinutes, later.DurationMinutes);
        Assert.True(later.ElapsedMinutes > later.DurationMinutes, $"elapsed {later.ElapsedMinutes}, duration {later.DurationMinutes}");
        Assert.Equal(0.4, later.ReleaseRateKgPerSecond); // the rate it ran at, not the zero it stopped at
    }

    [Fact]
    public async Task A_replay_rebuilds_the_same_cloud_without_calling_the_service_again()
    {
        var plume = new FakePlumeService();
        var harness = Harness(plume);
        var hazardId = await ReleaseAsync(harness);
        Assert.True(await PredictedAsync(harness, hazardId));
        var zones = ModelOf(harness, hazardId).Zones.Count;
        var area = ModelOf(harness, hazardId).AreaSquareMeters;

        // Replay the stream into a fresh world with no service anywhere near it.
        var replayed = new WorldState();
        foreach (var simEvent in await harness.EventsAsync())
            replayed.Apply(simEvent);

        var hazard = replayed.Hazards[hazardId];
        Assert.NotNull(hazard.Prediction);
        hazard.Model = new PlumeModel(hazard.Origin, hazard.Substance);
        hazard.Model.Step(new HazardStepInput(harness.Engine.SimTime, replayed.Weather, hazard.Rate, null, hazard.Prediction), TimeSpan.FromSeconds(5));

        var model = (PlumeModel)hazard.Model;
        Assert.True(model.UsingService);
        Assert.Equal(zones, model.Zones.Count);
        Assert.Equal(area, model.AreaSquareMeters);
    }

    // ---- Command's projection, and what it covers ----

    [Fact]
    public async Task Command_projects_a_plume_on_what_it_knows_and_is_told_what_is_inside_it()
    {
        // A school 500 m downwind (moderate zone), a hospital 1.5 km downwind (low zone), homes nearby.
        var downwind = 90.0; // westerly wind
        var gis = new FakeGisService(
            (GisLayerKeys.Schools, "St Mary's National School", GeoMath.Destination(Release, downwind, 500)),
            (GisLayerKeys.Hospitals, "St Vincent's", GeoMath.Destination(Release, downwind, 1500)),
            (GisLayerKeys.Buildings, "", GeoMath.Destination(Release, downwind, 400)),
            (GisLayerKeys.Schools, "Upwind College", GeoMath.Destination(Release, 270, 800))); // outside the plume
        var harness = Harness(new FakePlumeService(), gis);
        await harness.Perceived(new WeatherObserved("Met Éireann", Release, 270, 4, 14, 0.7));
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, Release, "Barrow Street")).EntityId!.Value;

        var result = await harness.C2.PredictPlumeAsync("chlorine", incident, rateKgPerSecond: 2.0);
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("Plume projected", result.Detail);

        var projection = Assert.Single(harness.Cop.PlumeProjections);
        Assert.True(projection.IsCurrent);
        Assert.False(projection.Automatic);
        Assert.Equal("chlorine", projection.Substance);
        Assert.Equal("chemical", projection.MaterialClass);
        Assert.Equal(270, projection.WindFromDegrees); // the reported wind, not the true one
        Assert.Equal(3, projection.Zones.Count);
        Assert.Equal(2400, projection.ReachMeters);
        Assert.Contains("collects in low ground", projection.Advisory);

        // The overlay: the school is in the moderate zone, the hospital in the low one, and the upwind school nowhere.
        var school = Assert.Single(projection.Impacts, i => i.LayerKey == GisLayerKeys.Schools);
        Assert.Equal(PlumeLevels.Moderate, school.Level);
        Assert.Equal(1, school.Count);
        Assert.Equal("St Mary's National School", Assert.Single(school.Names));
        Assert.Equal(PlumeLevels.Low, Assert.Single(projection.Impacts, i => i.LayerKey == GisLayerKeys.Hospitals).Level);
        Assert.DoesNotContain(projection.Impacts.SelectMany(i => i.Names), name => name == "Upwind College");

        // And the alert the deliverable asks for, worded for the people who have to act on it.
        var alert = Assert.Single(harness.Cop.Alerts, a => a.Title.Contains("School"));
        Assert.Equal("School within projected hazard area", alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(AlertCategory.Hazard, alert.Category);
        Assert.Contains("St Mary's National School", alert.Message);
        Assert.Contains(harness.Cop.Alerts, a => a.Title.Contains("home(s) within projected hazard area"));
        // A hospital only in the low zone is not worth an alert of its own.
        Assert.DoesNotContain(harness.Cop.Alerts, a => a.Title.Contains("Hospital"));
    }

    [Fact]
    public async Task A_projection_is_refused_when_it_cannot_be_made_and_without_a_service()
    {
        var harness = Harness(new FakePlumeService());
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, Release)).EntityId!.Value;

        Assert.False((await harness.C2.PredictPlumeAsync("  ", incident)).Succeeded);
        Assert.False((await harness.C2.PredictPlumeAsync("chlorine", Guid.NewGuid())).Succeeded);         // unknown incident
        Assert.False((await harness.C2.PredictPlumeAsync("chlorine")).Succeeded);                        // nowhere to put it
        Assert.False((await harness.C2.PredictPlumeAsync("chlorine", incident, durationMinutes: 0)).Succeeded);
        Assert.False((await harness.C2.PredictPlumeAsync("chlorine", incident, rateKgPerSecond: 0)).Succeeded);

        // A location on its own, with no incident, is enough.
        Assert.True((await harness.C2.PredictPlumeAsync("ammonia", location: Release)).Succeeded);

        // With no hazard-model service, the command says so rather than guessing.
        var offline = Harness(plume: null);
        var result = await offline.C2.PredictPlumeAsync("chlorine", location: Release);
        Assert.False(result.Succeeded);
        Assert.Contains("isn't running", result.Error);
    }

    [Fact]
    public async Task A_shift_in_the_reported_wind_re_runs_the_projection_and_supersedes_the_old_one()
    {
        var harness = Harness(new FakePlumeService());
        await harness.Perceived(new WeatherObserved("Met Éireann", Release, 270, 4, 14, 0.7));
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, Release)).EntityId!.Value;
        Assert.True((await harness.C2.PredictPlumeAsync("chlorine", incident, rateKgPerSecond: 2.0)).Succeeded);
        var first = Assert.Single(harness.Cop.PlumeProjections).Id;

        // The met service reports the wind has backed to the south-west.
        await harness.Perceived(new WeatherObserved("Met Éireann", Release, 200, 4, 14, 0.7));
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.PlumeProjections.Count > 1, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10)),
            "the projection was never re-run");

        var current = Assert.Single(harness.Cop.PlumeProjections, p => p.IsCurrent);
        Assert.NotEqual(first, current.Id);
        Assert.True(current.Automatic);
        Assert.Equal(200, current.WindFromDegrees);
        Assert.False(harness.Cop.PlumeProjections.Single(p => p.Id == first).IsCurrent);
        Assert.Equal(2.0, current.ReleaseRateKgPerSecond); // the rate command gave it, kept

        // A projection is not re-run again straight away, however much the wind wanders.
        await harness.Perceived(new WeatherObserved("Met Éireann", Release, 280, 4, 14, 0.7));
        await harness.RunAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, harness.Cop.PlumeProjections.Count);

        // Command's projection never learns the truth: a real release at a different rate, right there, changes nothing
        // about the rate its re-runs use.
        await harness.Truth(new HazardStarted(Guid.NewGuid(), null, HazardKind.Plume, Release, "Chlorine release", 9.0, "chlorine", 5000));
        await harness.RunAsync(PlumeDispersionSystem.ProjectionRefreshInterval + TimeSpan.FromSeconds(10));
        Assert.True(harness.Cop.PlumeProjections.Count > 2, "the 280° report should have brought a re-run by now");
        Assert.All(harness.Cop.PlumeProjections, p => Assert.Equal(2.0, p.ReleaseRateKgPerSecond));
    }

    // ---- What crews notice, by material ----

    [Fact]
    public void Each_material_class_resolves_with_the_same_thresholds_as_the_service()
    {
        Assert.Same(Substance.Chlorine, Substance.For("Liquid chlorine"));
        Assert.Same(Substance.HydrogenSulphide, Substance.For("H2S"));
        Assert.Equal("smoke", Substance.For("warehouse smoke").MaterialClass);
        Assert.Equal("biological", Substance.For("anthrax spores").MaterialClass);
        Assert.Equal("radiological", Substance.For("Caesium-137 source").MaterialClass);
        var unknown = Substance.For("yellow vapour");
        Assert.Equal(("yellow vapour", "toxic-gas"), (unknown.Name, unknown.MaterialClass));
        // Radiological thresholds sit at 0.1, 1 and 10 mSv/h, as the Python library has them.
        Assert.Equal(0.1, Substance.Radiological.DoseRate(Substance.Radiological.Low)!.Value, 6);
        Assert.Equal(10, Substance.Radiological.DoseRate(Substance.Radiological.High)!.Value, 6);
        Assert.Null(Substance.Chlorine.DoseRate(10));
    }

    [Fact]
    public void Crews_report_what_they_would_actually_notice()
    {
        var low = new HazardExposure(HazardLevel.Low, Substance.Radiological.Low);
        Assert.Contains("dosimeters", HazardReportingSystem.Sensed("Engine 4", Substance.Radiological, low, ""));
        Assert.Contains("Nothing to see or smell", HazardReportingSystem.Sensed("Engine 4", Substance.Radiological, low, ""));
        Assert.Contains("detector", HazardReportingSystem.Sensed("Hazmat 1", Substance.Biological, new(HazardLevel.Low, 1e-5), ""));
        Assert.Contains("smoke", HazardReportingSystem.Sensed("Engine 4", Substance.Smoke, new(HazardLevel.Moderate, 5), ""));
        Assert.Contains("bleach", HazardReportingSystem.Sensed("Engine 4", Substance.Chlorine, new(HazardLevel.Low, 1.5), ""));
        Assert.Contains("rotten-egg", HazardReportingSystem.Sensed("Engine 4", Substance.HydrogenSulphide, new(HazardLevel.Low, 0.7), ""));
    }

    [Fact]
    public async Task Nobody_notices_a_biological_release_without_a_detector()
    {
        var harness = new TestHarness(new HazardSystem(), new HazardReportingSystem());
        var engine = await harness.RegisterUnitAsync("Engine 4", UnitType.Engine);
        var hazmat = await harness.RegisterUnitAsync("Hazmat 1", UnitType.Hazmat);
        await harness.Truth(new HazardStarted(Guid.NewGuid(), null, HazardKind.Plume, TestHarness.Dublin, "Aerosol release", 0.05, "biological aerosol", 50));
        // Both crews stand 100 m downwind (east, for the default westerly).
        var downwind = GeoMath.Destination(TestHarness.Dublin, 90, 100);
        harness.World.Units[engine].Location = downwind;
        harness.World.Units[hazmat].Location = downwind;
        await harness.RunAsync(TimeSpan.FromMinutes(3));

        var reports = (await harness.EventsAsync()).Select(e => e.Payload).OfType<ReportReceived>().ToList();
        Assert.Contains(reports, r => r.SourceName == "Hazmat 1" && r.Claim.Contains("detector"));
        Assert.DoesNotContain(reports, r => r.SourceName == "Engine 4");
    }

    // ---- Injects ----

    [Fact]
    public async Task The_instructor_can_release_any_material_and_revise_the_rate()
    {
        var harness = Harness(new FakePlumeService());
        var release = Simulation.Scenarios.InjectTemplates.Create("release",
            new Dictionary<string, string> { ["material"] = "radioactive material", ["rate"] = "0.02", ["quantity"] = "10" })!;
        await harness.Engine.ApplyAsync(context =>
        {
            foreach (var (visibility, payload) in release.Produce(context)) context.Emit(visibility, payload, EventSources.Instructor);
            return true;
        });
        await harness.RunAsync(TimeSpan.FromSeconds(10));

        var hazard = Assert.Single(harness.World.Hazards.Values);
        Assert.Equal("radioactive material", hazard.Substance);
        Assert.Equal(0.02, hazard.Rate);
        Assert.Equal("radiological", ((PlumeModel)hazard.Model!).Substance.MaterialClass);
        // The incident it belongs to is a hazmat release, and HazardSystem doesn't start a second (chlorine) plume for it.
        Assert.Equal(IncidentType.HazmatRelease, Assert.Single(harness.World.Incidents.Values).Type);

        Assert.Throws<ArgumentException>(() => Simulation.Scenarios.InjectTemplates.Create("release", new Dictionary<string, string> { ["material"] = "custard" }));

        var revise = Simulation.Scenarios.InjectTemplates.Create("release-rate", new Dictionary<string, string> { ["rate"] = "0", ["reason"] = "source shielded" })!;
        Assert.Equal("Release stops: source shielded", revise.Description);
        await harness.Engine.ApplyAsync(context =>
        {
            foreach (var (visibility, payload) in revise.Produce(context)) context.Emit(visibility, payload, EventSources.Instructor);
            return true;
        });
        Assert.Equal(0, hazard.Rate);
    }

    // ---- The desk and the review ----

    [Fact]
    public async Task A_release_nobody_has_projected_waits_on_the_desk_of_whoever_does_hazard_prediction()
    {
        var harness = Harness(new FakePlumeService());
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, Release)).EntityId!.Value;
        // Every owner of hazard prediction present, so none of it falls to the IC.
        bool Filled(UserRole role) => role is UserRole.IncidentCommander or UserRole.Planning or UserRole.Safety or UserRole.Operations;

        Assert.Contains(RoleDesk.For(UserRole.Planning, harness.Cop, Filled), d => d.Function == "Hazard prediction" && d.IncidentId == incident);
        Assert.DoesNotContain(RoleDesk.For(UserRole.IncidentCommander, harness.Cop, Filled), d => d.Function == "Hazard prediction");
        // With nobody in Planning, Safety or Operations, it falls to the IC.
        Assert.Contains(RoleDesk.For(UserRole.IncidentCommander, harness.Cop, r => r == UserRole.IncidentCommander), d => d.Function == "Hazard prediction");

        Assert.True((await harness.C2.PredictPlumeAsync("chlorine", incident, rateKgPerSecond: 1)).Succeeded);
        Assert.DoesNotContain(RoleDesk.For(UserRole.Planning, harness.Cop, Filled), d => d.Function == "Hazard prediction");
    }

    [Fact]
    public async Task The_review_says_how_soon_a_release_was_projected_and_whether_what_it_found_was_protected()
    {
        var gis = new FakeGisService((GisLayerKeys.Schools, "St Mary's National School", GeoMath.Destination(Release, 90, 500)));
        var harness = Harness(new FakePlumeService(), gis);
        await harness.Perceived(new WeatherObserved("Met Éireann", Release, 270, 4, 14, 0.7));
        var projected = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, Release)).EntityId!.Value;
        var ignored = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, GeoMath.Destination(Release, 0, 3000))).EntityId!.Value;

        await harness.RunAsync(TimeSpan.FromMinutes(3));
        Assert.True((await harness.C2.PredictPlumeAsync("chlorine", projected, rateKgPerSecond: 2)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(6));

        // Evacuate the projected moderate zone, from the projection itself.
        var moderate = harness.Cop.PlumeProjections.Single().Zone(PlumeLevels.Moderate)!;
        Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation", moderate.Boundary, projected)).Succeeded);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test exercise");
        var number = harness.Cop.FindIncident(projected)!.Number;
        var never = harness.Cop.FindIncident(ignored)!.Number;
        Assert.Contains(report.Findings, f => f.Strength && f.Text == $"{number}: plume projected 3 min 00 s after the release was reported.");
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.StartsWith($"{never}: the release was never projected"));
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("1 school(s) in the moderate or high zone; a protective zone covered them 6 min"));
        Assert.Contains(report.Timeline, t => t.Category == "HAZARD" && t.Text.StartsWith("Plume projection: chlorine"));
        // The school alert is a consequence of the projection, not a decision of its own.
        Assert.DoesNotContain(report.Decisions, d => d.Decision.Contains("within projected hazard area"));
    }

    // ---- Stability ----

    [Fact]
    public void Stability_follows_the_wind_and_the_time_of_day()
    {
        var summerNoon = new DateTimeOffset(2026, 6, 21, 13, 0, 0, TimeSpan.Zero);
        var winterNight = new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero);

        // A still summer afternoon: the air boils, and a plume mixes out fast.
        Assert.Equal("A", AtmosphericStability.Classify(1, summerNoon, 0.5));
        // A breezy one is nearer neutral.
        Assert.Equal("C", AtmosphericStability.Classify(7, summerNoon, 0.5));
        // A clear, still night is the dangerous one: the cloud stays together for kilometres.
        Assert.Equal("F", AtmosphericStability.Classify(1.5, winterNight, 0.6));
        // Wind mixes even a night back to neutral.
        Assert.Equal("D", AtmosphericStability.Classify(8, winterNight, 0.6));
        // Overcast and damp: neither heating by day nor cooling by night.
        Assert.Equal("E", AtmosphericStability.Classify(1, winterNight, 0.95));
        Assert.Equal("B", AtmosphericStability.Classify(1, summerNoon, 0.95));
    }

    [Fact]
    public void The_bearing_difference_wraps_around_north()
    {
        Assert.Equal(20, GeoMath.BearingDifference(350, 10), 6);
        Assert.Equal(-20, GeoMath.BearingDifference(10, 350), 6);
        Assert.Equal(90, GeoMath.BearingDifference(270, 0), 6);
        Assert.Equal(0, GeoMath.BearingDifference(180, 180), 6);
    }
}
