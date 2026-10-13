using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Simulation.Services;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Pins the JSON shape exchanged with hazard-models/plume_service, which has matching tests on the Python side.
/// </summary>
public class PlumeContractTests
{
    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task Request_and_response_use_the_camel_case_wire_format()
    {
        const string response = """
            {"model":"stub-ellipse-v0","generatedAt":"2026-10-08T14:00:00Z",
             "zones":[{"level":"high","boundary":[{"latitude":53.35,"longitude":-6.26},
                                                  {"latitude":53.36,"longitude":-6.25},
                                                  {"latitude":53.35,"longitude":-6.24}]}]}
            """;
        var handler = new RecordingHandler(response);
        var service = new HttpPlumeService(new HttpClient(handler) { BaseAddress = new Uri("http://hazard.test/") });

        var prediction = await service.PredictAsync(new PlumeRequest(
            new GeoPoint(53.35, -6.26), "chlorine", 2.0, 30, new WeatherInput(270, 4, 14, 0.7)));

        Assert.Equal("/v1/plume/predict", handler.RequestPath);
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal(53.35, (double)body["releaseLocation"]!["latitude"]!);
        Assert.Equal(2.0, (double)body["releaseRateKgPerSecond"]!);
        Assert.Equal(270, (double)body["weather"]!["windFromDegrees"]!);
        Assert.Equal("D", (string)body["weather"]!["stabilityClass"]!);

        Assert.Equal("stub-ellipse-v0", prediction.Model);
        var zone = Assert.Single(prediction.Zones);
        Assert.Equal("high", zone.Level);
        Assert.Equal(3, zone.Boundary.Count);
    }

    [Fact]
    public async Task The_phase_13_fields_travel_both_ways_under_the_names_the_service_uses()
    {
        // Field names as pydantic's camelCase generator writes them; note doseRateMsvPerHour, not ...MSv...
        const string response = """
            {"model":"gaussian-plume-v1","generatedAt":"2026-10-08T14:00:00Z",
             "zones":[{"level":"moderate","boundary":[{"latitude":53.35,"longitude":-6.26}],
                       "thresholdMgPerM3":0.5,"doseRateMsvPerHour":1.0,"areaSquareMeters":120000,
                       "maxDistanceMeters":1800,"arrivalSeconds":45}],
             "substance":{"name":"caesium-137","materialClass":"radiological","lowMgPerM3":0.05,"moderateMgPerM3":0.5,
                          "highMgPerM3":5,"molarMass":29,"halfLifeMinutes":null,"advisory":"Limit time in the zones."},
             "frontMeters":2400,"rearMeters":300,"effectiveHeightMeters":12.5,"plumeRiseMeters":12.5,
             "denseGas":false,"stabilityClass":"F","urban":true}
            """;
        var handler = new RecordingHandler(response);
        var service = new HttpPlumeService(new HttpClient(handler) { BaseAddress = new Uri("http://hazard.test/") });

        var prediction = await service.PredictAsync(new PlumeRequest(
            new GeoPoint(53.35, -6.26), "caesium-137", 0.01, 10, new WeatherInput(270, 2, 8, 0.6, "F"),
            QuantityKg: 6, ReleaseTemperatureC: 300, ReleasePressureKpa: 150, ReleaseHeightMeters: 4, ElapsedMinutes: 15,
            Terrain: new TerrainInput(1.5)));

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal(6, (double)body["quantityKg"]!);
        Assert.Equal(300, (double)body["releaseTemperatureC"]!);
        Assert.Equal(150, (double)body["releasePressureKpa"]!);
        Assert.Equal(4, (double)body["releaseHeightMeters"]!);
        Assert.Equal(15, (double)body["elapsedMinutes"]!);
        Assert.Equal(1.5, (double)body["terrain"]!["roughnessMeters"]!);
        Assert.Equal(101.3, (double)body["weather"]!["pressureKpa"]!);

        var zone = Assert.Single(prediction.Zones);
        Assert.Equal((0.5, 1.0, 120000.0, 1800.0, 45.0), (zone.ThresholdMgPerM3, zone.DoseRateMsvPerHour, zone.AreaSquareMeters, zone.MaxDistanceMeters, zone.ArrivalSeconds));
        Assert.Equal("radiological", prediction.Substance!.MaterialClass);
        Assert.Equal("Limit time in the zones.", prediction.Substance.Advisory);
        Assert.Equal((2400.0, 300.0, 12.5, false, "F", true),
            (prediction.FrontMeters, prediction.RearMeters, prediction.EffectiveHeightMeters, prediction.DenseGas, prediction.StabilityClass, prediction.Urban));
    }

    [Fact]
    public async Task Substances_are_listed_from_the_service()
    {
        const string response = """[{"name":"chlorine","materialClass":"chemical","lowMgPerM3":1.5,"moderateMgPerM3":5.8,"highMgPerM3":58,"molarMass":70.9,"advisory":"x"}]""";
        var handler = new RecordingHandler(response);
        var service = new HttpPlumeService(new HttpClient(handler) { BaseAddress = new Uri("http://hazard.test/") });

        var substances = await service.GetSubstancesAsync();
        Assert.Equal("/v1/substances", handler.RequestPath);
        Assert.Equal("chlorine", Assert.Single(substances).Name);
    }
}
