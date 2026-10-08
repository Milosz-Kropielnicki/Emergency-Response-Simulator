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
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
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
}
