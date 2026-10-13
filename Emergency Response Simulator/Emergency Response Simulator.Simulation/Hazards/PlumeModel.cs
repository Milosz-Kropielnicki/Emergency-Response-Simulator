using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// Concentration thresholds for a released substance, mg/m³: Low ≈ AEGL-1 (discomfort), Moderate ≈ AEGL-2
/// (serious, lasting effects), High ≈ AEGL-3 (life-threatening), all for 60-minute exposure.
/// <para>
/// The same library as the Python service's (<c>hazard-models/plume_service/substances.py</c>), so the in-engine
/// fallback draws its zones at the same thresholds and a crew notices the same thing whichever model is in play.
/// Keep the two in step.
/// </para>
/// </summary>
/// <param name="MaterialClass">"chemical", "smoke", "toxic-gas", "biological" or "radiological" (Phase 13).</param>
/// <param name="DoseRatePerMgM3">mSv/h per mg/m³ in the cloud, for radiological material: what a dosimeter shows.</param>
public sealed record Substance(string Name, double Low, double Moderate, double High, string MaterialClass = "chemical",
    double? DoseRatePerMgM3 = null)
{
    // 1 ppm chlorine ≈ 2.9 mg/m³; ammonia ≈ 0.7; sulphur dioxide ≈ 2.6; hydrogen sulphide ≈ 1.4.
    public static readonly Substance Chlorine = new("chlorine", 1.5, 5.8, 58);
    public static readonly Substance Ammonia = new("ammonia", 21, 112, 770);
    public static readonly Substance SulphurDioxide = new("sulphur dioxide", 0.5, 2, 80);
    public static readonly Substance HydrogenSulphide = new("hydrogen sulphide", 0.7, 23, 70);
    public static readonly Substance Smoke = new("smoke", 0.5, 5, 50, "smoke");
    public static readonly Substance ToxicGas = new("toxic gas", 1.5, 5.8, 58, "toxic-gas");
    public static readonly Substance Biological = new("biological aerosol", 1e-5, 1e-4, 1e-3, "biological");

    /// <summary>Caesium-137-like particulate: 0.1, 1 and 10 mSv/h at the three thresholds.</summary>
    public static readonly Substance Radiological = new("radioactive material", 0.05, 0.5, 5, "radiological", DoseRatePerMgM3: 2);

    /// <summary>Words that identify a substance, longest first so "hydrogen sulphide" wins over "hydrogen".</summary>
    private static readonly (string Keyword, Substance Substance)[] Keywords =
    [
        ("hydrogen sulphide", HydrogenSulphide), ("hydrogen sulfide", HydrogenSulphide),
        ("sulphur dioxide", SulphurDioxide), ("sulfur dioxide", SulphurDioxide),
        ("biological", Biological), ("radiological", Radiological), ("radioactive", Radiological), ("anthrax", Biological),
        ("chlorine", Chlorine), ("ammonia", Ammonia), ("caesium", Radiological), ("cesium", Radiological), ("cobalt", Radiological),
        ("iodine-131", Radiological), ("spore", Biological), ("aerosol", Biological), ("smoke", Smoke), ("soot", Smoke),
        ("so2", SulphurDioxide), ("h2s", HydrogenSulphide), ("nh3", Ammonia), ("cl2", Chlorine),
    ];

    /// <summary>
    /// A known substance by name ("anhydrous ammonia" is <see cref="Ammonia"/>), or, unknown, a toxic gas with
    /// chlorine-like thresholds that keeps the name it was given.
    /// </summary>
    public static Substance For(string? name)
    {
        var key = name?.Trim().ToLowerInvariant() ?? "";
        foreach (var (keyword, substance) in Keywords)
            if (key.Contains(keyword))
                return substance;
        return ToxicGas with { Name = string.IsNullOrWhiteSpace(name) ? ToxicGas.Name : name.Trim() };
    }

    /// <summary>Radiological: harms by dose, so crews notice it on dosimeters, not with their noses.</summary>
    public bool IsRadiological => MaterialClass == "radiological";

    /// <summary>Biological: odourless and invisible, so only a detector notices it.</summary>
    public bool IsBiological => MaterialClass == "biological";

    public bool IsSmoke => MaterialClass == "smoke";

    /// <summary>What a dosimeter reads in a cloud at this concentration, mSv/h; null for anything not radiological.</summary>
    public double? DoseRate(double concentrationMgPerM3) => DoseRatePerMgM3 is { } factor ? concentrationMgPerM3 * factor : null;
}

/// <summary>
/// A simple ground-level Gaussian plume for a continuous release (Design Document §10.4): concentration downwind at
/// (x, y) is C = Q / (π·u·σy·σz) · exp(−y² / 2σy²), with Briggs urban dispersion for neutral conditions (class D). The
/// cloud only reaches as far downwind as the wind has carried it since the release began, and it swings with the wind.
/// When the release stops, the cloud clears after a few minutes.
/// <para>
/// Since Phase 13 this is the <b>fallback</b>. When the Python hazard-model service is running, its fuller model
/// (stability classes, plume rise, dense-gas slumping, deposition and decay) is authoritative: its zones arrive as
/// <see cref="PlumePredicted"/> events and are adopted here through <see cref="Adopt"/>, and everything downstream —
/// exposure, footprints, the instructor's board — reads those instead. With no service, this keeps a session running
/// on its own maths, and <see cref="ModelName"/> says which is in play.
/// </para>
/// </summary>
public sealed class PlumeModel : IHazardModel
{
    /// <summary>Downwind distances are sampled out to here at most.</summary>
    private const double MaxRangeMeters = 15_000;

    /// <summary>How long the cloud lingers once the release stops.</summary>
    private static readonly TimeSpan ClearanceTime = TimeSpan.FromMinutes(8);

    private readonly GeoPoint _origin;
    private readonly LocalGrid _plane;
    private double _rate;
    private double _windFrom;
    private double _windSpeed = 1;
    private double _frontMeters;
    private DateTimeOffset? _stoppedAt;
    private DateTimeOffset _now;
    private IReadOnlyList<(HazardLevel Level, Polygon Area)> _zones = [];

    /// <summary>The service's prediction, once one has arrived (Phase 13); null means this model's own maths.</summary>
    private PlumePredicted? _predicted;
    private IReadOnlyList<(HazardLevel Level, Polygon Area, double Threshold)> _predictedZones = [];

    public PlumeModel(GeoPoint origin, string? substance)
    {
        _origin = origin;
        _plane = new LocalGrid(origin, 1, 0);
        Substance = Substance.For(substance);
    }

    public HazardKind Kind => HazardKind.Plume;
    public Substance Substance { get; }

    /// <summary>Which model the zones come from: the service's (Phase 13) or this one's.</summary>
    public string ModelName => _predicted?.Model ?? InEngineModel;

    public const string InEngineModel = "in-engine-gaussian";

    /// <summary>True once the service's model is driving the cloud rather than this one.</summary>
    public bool UsingService => _predicted is not null;

    /// <summary>What the service said about the release: dense gas, how high the plume sits, what to advise.</summary>
    public PlumePredicted? Prediction => _predicted;

    /// <summary>
    /// Takes the service's prediction as the cloud's true shape (Phase 13). Applied from the event stream, so a replay
    /// rebuilds exactly the same cloud; a prediction with no zones means nothing is above the lowest threshold now.
    /// </summary>
    public void Adopt(PlumePredicted prediction)
    {
        _predicted = prediction;
        _predictedZones =
        [
            .. prediction.Zones
                .Select(z => (Level: LevelOf(z.Level), Area: Outline(z.Boundary), z.ThresholdMgPerM3))
                .Where(z => z.Area is not null)
                .Select(z => (z.Level, z.Area!, z.ThresholdMgPerM3))
                .OrderBy(z => z.Level), // lowest (largest) first, as this model's own zones are
        ];
        _frontMeters = Math.Max(_frontMeters, prediction.FrontMeters);
    }

    private static HazardLevel LevelOf(string level) => level switch
    {
        PlumeLevels.High => HazardLevel.High,
        PlumeLevels.Moderate => HazardLevel.Moderate,
        _ => HazardLevel.Low,
    };

    /// <summary>A zone boundary from the service as a polygon, or null when it is not usable.</summary>
    private static Polygon? Outline(IReadOnlyList<GeoPoint> boundary)
    {
        if (boundary.Count < 4) return null;
        var ring = boundary.Select(p => new Coordinate(p.Longitude, p.Latitude)).ToList();
        if (!ring[0].Equals2D(ring[^1])) ring.Add(ring[0]);
        var polygon = Wgs84.Factory.CreatePolygon([.. ring]);
        return polygon.IsValid ? polygon : null;
    }

    public bool IsActive => _stoppedAt is { } stopped ? _now - stopped < ClearanceTime : _rate > 0;

    /// <summary>Area above the Low threshold.</summary>
    public double AreaSquareMeters => UsingService
        ? _predicted!.Zones.Select(z => z.AreaSquareMeters).DefaultIfEmpty(0).Max()
        : _ownArea;

    private double _ownArea;

    public double Intensity => Concentration(100, 0);

    /// <summary>How far downwind the cloud has travelled.</summary>
    public double FrontMeters => _frontMeters;

    /// <summary>Areas above each threshold, lowest (largest) first.</summary>
    public IReadOnlyList<(HazardLevel Level, Polygon Area)> Zones => UsingService
        ? [.. _predictedZones.Select(z => (z.Level, z.Area))]
        : _zones;

    public void Step(HazardStepInput input, TimeSpan delta)
    {
        _now = input.SimTime;
        _windFrom = input.Weather.WindFromDegrees;
        _windSpeed = Math.Max(1, input.Weather.WindSpeedMps); // calm air still drifts; avoids dividing by zero

        // The service's model, when there is one, takes over the shape of the cloud (Phase 13).
        if (input.Prediction is { } prediction && !ReferenceEquals(prediction, _predicted))
            Adopt(prediction);

        if (input.Rate > 0)
        {
            _rate = input.Rate;
            _stoppedAt = null;
            _frontMeters = Math.Min(MaxRangeMeters, _frontMeters + _windSpeed * delta.TotalSeconds);
        }
        else if (_rate > 0)
        {
            // The release has stopped: keep the last cloud while it clears.
            _stoppedAt ??= input.SimTime;
        }

        if (!IsActive)
        {
            _rate = 0;
            _zones = [];
            _ownArea = 0;
            _predicted = null;
            _predictedZones = [];
            return;
        }

        // A clearing cloud thins out as it goes.
        var fading = Fading;
        _zones =
        [
            .. new[] { (HazardLevel.Low, Substance.Low), (HazardLevel.Moderate, Substance.Moderate), (HazardLevel.High, Substance.High) }
                .Select(t => (t.Item1, Ring(t.Item2 / Math.Max(fading, 0.01))))
                .Where(z => z.Item2 is not null)
                .Select(z => (z.Item1, z.Item2!)),
        ];
        _ownArea = _zones.Count > 0 ? AreaOf(_zones[0].Area) : 0;
    }

    /// <summary>The effective release rate (kg/s), counting a stopped release as zero.</summary>
    public double Rate => _stoppedAt is null ? _rate : 0;

    /// <summary>Ground-level concentration (mg/m³) at <paramref name="downwind"/> metres along and <paramref name="crosswind"/> across the plume.</summary>
    public double Concentration(double downwind, double crosswind)
    {
        if (_rate <= 0 || downwind < 1 || downwind > _frontMeters) return 0;
        var (sy, sz) = Sigmas(downwind);
        var q = _rate * 1_000_000; // kg/s → mg/s
        return q / (Math.PI * _windSpeed * sy * sz) * Math.Exp(-crosswind * crosswind / (2 * sy * sy));
    }

    /// <summary>Briggs urban dispersion coefficients, neutral stability (class D).</summary>
    private static (double SigmaY, double SigmaZ) Sigmas(double x) =>
        (0.16 * x / Math.Sqrt(1 + 0.0004 * x), 0.14 * x / Math.Sqrt(1 + 0.0003 * x));

    private (double Downwind, double Crosswind) ToPlume(GeoPoint point)
    {
        var (east, north) = _plane.ToLocal(point);
        var bearing = (_windFrom + 180) * Math.PI / 180;
        var (sin, cos) = Math.SinCos(bearing);
        return (east * sin + north * cos, east * cos - north * sin);
    }

    private GeoPoint FromPlume(double downwind, double crosswind)
    {
        var bearing = (_windFrom + 180) * Math.PI / 180;
        var (sin, cos) = Math.SinCos(bearing);
        return _plane.FromLocal(downwind * sin + crosswind * cos, downwind * cos - crosswind * sin);
    }

    /// <summary>The outline of the area above a threshold, or null when nowhere reaches it.</summary>
    private Polygon? Ring(double threshold)
    {
        // Find how far downwind the centreline stays above the threshold.
        var reach = 0.0;
        for (var x = 2.0; x <= _frontMeters; x *= 1.08)
        {
            if (Concentration(x, 0) >= threshold) reach = x;
            else if (reach > 0) break;
        }
        if (reach <= 2) return null;

        const int samples = 28;
        var left = new List<Coordinate>();
        var right = new List<Coordinate>();
        for (var k = 1; k <= samples; k++)
        {
            var x = reach * k / samples;
            var centre = Concentration(x, 0);
            var (sy, _) = Sigmas(x);
            var half = centre > threshold ? sy * Math.Sqrt(2 * Math.Log(centre / threshold)) : 0;
            left.Add(Coord(FromPlume(x, -half)));
            right.Add(Coord(FromPlume(x, half)));
        }

        var origin = Coord(_origin);
        right.Reverse();
        return Wgs84.Factory.CreatePolygon([origin, .. left, .. right, origin]);

        static Coordinate Coord(GeoPoint p) => new(p.Longitude, p.Latitude);
    }

    public IReadOnlyList<Polygon> Footprint()
    {
        var zones = Zones;
        return zones.Count > 0 ? [zones[0].Area] : [];
    }

    public HazardExposure ExposureAt(GeoPoint point)
    {
        if (!IsActive) return HazardExposure.None;

        // The service's model (Phase 13) hands back zones rather than a formula, so the worst zone the point is in
        // gives the level, and that zone's threshold is the least it can be breathing.
        if (UsingService)
        {
            var at = point.ToPoint();
            var worst = _predictedZones
                .Where(z => z.Area.Intersects(at))
                .OrderByDescending(z => z.Level)
                .Select(z => (z.Level, z.Threshold))
                .FirstOrDefault();
            return worst.Level == HazardLevel.None ? HazardExposure.None : new HazardExposure(worst.Level, worst.Threshold);
        }

        var (x, y) = ToPlume(point);
        var c = Concentration(x, y) * Fading;
        var level = c >= Substance.High ? HazardLevel.High
            : c >= Substance.Moderate ? HazardLevel.Moderate
            : c >= Substance.Low ? HazardLevel.Low
            : HazardLevel.None;
        return new HazardExposure(level, c);
    }

    /// <summary>1 while releasing, falling to 0 as a stopped release's cloud clears.</summary>
    private double Fading => _stoppedAt is { } since ? Math.Max(0, 1 - (_now - since) / ClearanceTime) : 1;

    /// <summary>Area of a WGS84 polygon on the local plane (shoelace formula), without a full projection.</summary>
    private double AreaOf(Polygon polygon)
    {
        var ring = polygon.ExteriorRing.Coordinates;
        double sum = 0;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var (x1, y1) = _plane.ToLocal(new GeoPoint(ring[i].Y, ring[i].X));
            var (x2, y2) = _plane.ToLocal(new GeoPoint(ring[i + 1].Y, ring[i + 1].X));
            sum += x1 * y2 - x2 * y1;
        }
        return Math.Abs(sum) / 2;
    }
}
