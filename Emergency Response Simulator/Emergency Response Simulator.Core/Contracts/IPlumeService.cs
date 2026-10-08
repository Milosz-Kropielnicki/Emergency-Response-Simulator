using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Hazard dispersion: "Where is the hazard going?" (Design Document §7.3).
/// Backed by the Python hazard-model service; the JSON shape of these records is the wire contract
/// (see hazard-models/plume_service/models.py).
/// Results are predictions, not facts.
/// </summary>
public interface IPlumeService
{
    Task<PlumePrediction> PredictAsync(PlumeRequest request, CancellationToken cancellationToken = default);
}

public sealed record PlumeRequest(
    GeoPoint ReleaseLocation,
    string Substance,
    double ReleaseRateKgPerSecond,
    double DurationMinutes,
    WeatherInput Weather);

/// <param name="WindFromDegrees">Direction the wind blows from, degrees clockwise from north.</param>
/// <param name="StabilityClass">Pasquill–Gifford class "A" (very unstable) to "F" (very stable).</param>
public sealed record WeatherInput(
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity,
    string StabilityClass = "D");

public sealed record PlumePrediction(
    string Model,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PlumeZone> Zones);

/// <param name="Level">"high", "moderate" or "low" concentration.</param>
public sealed record PlumeZone(string Level, IReadOnlyList<GeoPoint> Boundary);
