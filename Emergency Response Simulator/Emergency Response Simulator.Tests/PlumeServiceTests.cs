using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.Systems;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Skips unless the Python hazard-model service is running (hazard-models/, see the README). Its address is
/// <c>ERS_HAZARD_MODELS</c> if set, otherwise the default <c>http://127.0.0.1:8765/</c>.
/// </summary>
public sealed class HazardServiceFactAttribute : FactAttribute
{
    public HazardServiceFactAttribute()
    {
        if (!HazardService.Running)
            Skip = $"No hazard-model service at {HazardService.BaseUrl}; start it with uvicorn (README, Local setup).";
    }
}

public static class HazardService
{
    public static Uri BaseUrl { get; } =
        new(Environment.GetEnvironmentVariable("ERS_HAZARD_MODELS") is { Length: > 0 } url ? url : "http://127.0.0.1:8765/");

    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using var http = new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync("health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    });

    public static bool Running => Probe.Value;

    public static IPlumeService Client() => new HttpPlumeService(new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(10) });
}

/// <summary>
/// Phase 13 end to end: the engine against the real Python dispersion model, not a fake. The model's physics are tested
/// on the Python side; these check that what crosses the wire is usable and that the engine does the right thing with it.
/// </summary>
public class PlumeServiceTests
{
    private static readonly GeoPoint Release = new(53.3395, -6.2365);

    private static PlumeRequest Chlorine(string stability = "D", double rate = 2.0) =>
        new(Release, "chlorine", rate, 30, new WeatherInput(270, 4, 14, 0.7, stability));

    private static Polygon Outline(PlumeZone zone) =>
        Wgs84.Factory.CreatePolygon([.. zone.Boundary.Select(p => new Coordinate(p.Longitude, p.Latitude))]);

    [HazardServiceFact]
    public async Task The_real_model_answers_with_nested_zones_downwind_of_the_release()
    {
        var prediction = await HazardService.Client().PredictAsync(Chlorine());

        Assert.Equal("gaussian-plume-v1", prediction.Model);
        Assert.Equal(PlumeLevels.All, prediction.Zones.Select(z => z.Level));
        var (high, moderate, low) = (Outline(prediction.Zones[0]), Outline(prediction.Zones[1]), Outline(prediction.Zones[2]));
        Assert.True(high.IsValid && moderate.IsValid && low.IsValid);
        Assert.True(high.Area < moderate.Area && moderate.Area < low.Area);
        Assert.True(low.Centroid.X > Release.Longitude, "a westerly wind should carry it east");
        Assert.True(prediction.DenseGas);
        Assert.Equal("chemical", prediction.Substance!.MaterialClass);
    }

    [HazardServiceFact]
    public async Task Every_material_class_the_deliverable_names_is_known()
    {
        var classes = (await HazardService.Client().GetSubstancesAsync()).Select(s => s.MaterialClass).ToHashSet();
        Assert.Equal(new HashSet<string> { "chemical", "smoke", "toxic-gas", "biological", "radiological" }, classes);
    }

    [HazardServiceFact]
    public async Task A_still_night_carries_the_cloud_further_than_a_sunny_afternoon()
    {
        var client = HazardService.Client();
        var night = await client.PredictAsync(Chlorine("F"));
        var afternoon = await client.PredictAsync(Chlorine("A"));
        double Reach(PlumePrediction p) => p.Zones.Single(z => z.Level == PlumeLevels.Moderate).MaxDistanceMeters;
        Assert.True(Reach(night) > Reach(afternoon) * 2, $"night {Reach(night):F0} m, afternoon {Reach(afternoon):F0} m");
    }

    [HazardServiceFact]
    public async Task The_engine_adopts_the_real_models_cloud_for_ground_truth()
    {
        var harness = new TestHarness(cop => [new PlumeDispersionSystem(HazardService.Client(), cop), new HazardSystem()]);
        var hazardId = Guid.NewGuid();
        await harness.Truth(new HazardStarted(hazardId, null, HazardKind.Plume, Release, "Chlorine release", 2.0, "chlorine", 5000));

        // The service is called off the engine thread, so give it real time to answer between steps. The first answer
        // comes seconds into the release, when the cloud has barely left the source; wait for one that has it a few
        // hundred metres out.
        var adopted = false;
        for (var i = 0; i < 60 && !adopted; i++)
        {
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(50);
            adopted = harness.World.Hazards[hazardId].Model is PlumeModel { UsingService: true, Prediction.FrontMeters: > 400 };
        }
        Assert.True(adopted, "the engine never adopted a prediction with the cloud out past 400 m");

        var model = (PlumeModel)harness.World.Hazards[hazardId].Model!;
        Assert.Equal("gaussian-plume-v1", model.ModelName);
        Assert.NotEmpty(model.Footprint());
        var downwind = (harness.World.Weather.WindFromDegrees + 180) % 360;
        Assert.NotEqual(HazardLevel.None, model.ExposureAt(GeoMath.Destination(Release, downwind, 150)).Level);
        Assert.Equal(HazardLevel.None, model.ExposureAt(GeoMath.Destination(Release, harness.World.Weather.WindFromDegrees, 400)).Level);
    }

    [HazardServiceFact]
    public async Task Command_projects_with_the_real_model()
    {
        var harness = new TestHarness(cop => [], HazardService.Client());
        await harness.Perceived(new WeatherObserved("Met Éireann", Release, 270, 4, 14, 0.7));
        var result = await harness.C2.PredictPlumeAsync("ammonia", location: Release, rateKgPerSecond: 5);
        Assert.True(result.Succeeded, result.Error);
        var projection = Assert.Single(harness.Cop.PlumeProjections);
        Assert.Equal("gaussian-plume-v1", projection.Model);
        Assert.NotEmpty(projection.Zones);
        Assert.Contains("Lighter than air", projection.Advisory);
    }
}
