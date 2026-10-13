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

    /// <summary>
    /// The substances the model knows, for the instructor's injects and command's projection dialog (Phase 13).
    /// Empty when the service cannot be reached.
    /// </summary>
    Task<IReadOnlyList<SubstanceInfo>> GetSubstancesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One run of the dispersion model. Everything after <paramref name="Weather"/> was added in Phase 13 and has a
/// default, so a caller that knows only the release and the weather still gets a prediction.
/// </summary>
/// <param name="QuantityKg">Inventory; it caps the duration when the store empties sooner.</param>
/// <param name="ReleaseTemperatureC">Null: the material leaves at ambient temperature.</param>
/// <param name="ElapsedMinutes">
/// How long the release has been running. Null predicts the steady state, ignoring how far the cloud has actually got.
/// </param>
public sealed record PlumeRequest(
    GeoPoint ReleaseLocation,
    string Substance,
    double ReleaseRateKgPerSecond,
    double DurationMinutes,
    WeatherInput Weather,
    double? QuantityKg = null,
    double? ReleaseTemperatureC = null,
    double? ReleasePressureKpa = null,
    double ReleaseHeightMeters = 0,
    double ReleaseDiameterMeters = 0.5,
    double? ElapsedMinutes = null,
    TerrainInput? Terrain = null);

/// <param name="RoughnessMeters">
/// Aerodynamic roughness length: about 0.03 over open grass, 0.5 in a suburb, more than 1 in a city centre.
/// From 0.4 up, the model uses urban dispersion curves.
/// </param>
public sealed record TerrainInput(double RoughnessMeters = 1.0);

/// <param name="WindFromDegrees">Direction the wind blows from, degrees clockwise from north.</param>
/// <param name="StabilityClass">Pasquill–Gifford class "A" (very unstable) to "F" (very stable).</param>
public sealed record WeatherInput(
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity,
    string StabilityClass = "D",
    double PressureKpa = 101.3);

/// <param name="Zones">Low, moderate and high concentration, each as a polygon on the ground.</param>
/// <param name="RearMeters">Back of the cloud: non-zero once a release that has finished drifts away from its source.</param>
/// <param name="DenseGas">Heavier than air: it stays on the ground and collects in low places.</param>
public sealed record PlumePrediction(
    string Model,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PlumeZone> Zones,
    SubstanceInfo? Substance = null,
    double FrontMeters = 0,
    double RearMeters = 0,
    double EffectiveHeightMeters = 0,
    double PlumeRiseMeters = 0,
    bool DenseGas = false,
    string StabilityClass = "D",
    bool Urban = false);

/// <param name="Level">"high", "moderate" or "low" concentration.</param>
/// <param name="DoseRateMsvPerHour">Radiological material only, where dose matters more than concentration.</param>
/// <param name="ArrivalSeconds">Time from the release for the cloud to reach this zone's near edge.</param>
public sealed record PlumeZone(
    string Level,
    IReadOnlyList<GeoPoint> Boundary,
    double ThresholdMgPerM3 = 0,
    double? DoseRateMsvPerHour = null,
    double AreaSquareMeters = 0,
    double MaxDistanceMeters = 0,
    double? ArrivalSeconds = null);

/// <summary>What the model took the substance to be, and what command should know about it.</summary>
/// <param name="MaterialClass">"chemical", "smoke", "toxic-gas", "biological" or "radiological".</param>
public sealed record SubstanceInfo(
    string Name,
    string MaterialClass,
    double LowMgPerM3,
    double ModerateMgPerM3,
    double HighMgPerM3,
    double MolarMass,
    double? HalfLifeMinutes = null,
    string Advisory = "");

/// <summary>Zone levels as the model names them, strongest first.</summary>
public static class PlumeLevels
{
    public const string High = "high";
    public const string Moderate = "moderate";
    public const string Low = "low";

    public static IReadOnlyList<string> All { get; } = [High, Moderate, Low];

    /// <summary>Strongest first, so a point is tested against the worst zone it is in.</summary>
    public static int Severity(string level) => level switch { High => 3, Moderate => 2, Low => 1, _ => 0 };

    public static string Name(string level) => level switch
    {
        High => "High",
        Moderate => "Moderate",
        Low => "Low",
        _ => level,
    };
}
