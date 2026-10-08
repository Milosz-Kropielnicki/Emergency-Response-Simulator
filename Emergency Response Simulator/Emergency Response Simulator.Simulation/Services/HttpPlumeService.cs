using System.Net.Http.Json;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>Calls the Python hazard-model service (hazard-models/plume_service).</summary>
public sealed class HttpPlumeService(HttpClient http) : IPlumeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PlumePrediction> PredictAsync(PlumeRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("v1/plume/predict", request, Json, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PlumePrediction>(Json, cancellationToken)
            ?? throw new InvalidOperationException("The hazard-model service returned an empty prediction.");
    }
}

/// <summary>Bound from the "HazardModels" configuration section.</summary>
public sealed class HazardModelOptions
{
    public const string SectionName = "HazardModels";

    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:8765/");

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
