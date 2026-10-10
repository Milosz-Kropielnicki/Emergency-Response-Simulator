using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// Concentration thresholds for a released substance, mg/m³: Low ≈ AEGL-1 (discomfort), Moderate ≈ AEGL-2
/// (serious, lasting effects), High ≈ AEGL-3 (life-threatening), all for 60-minute exposure.
/// </summary>
public sealed record Substance(string Name, double Low, double Moderate, double High)
{
    // 1 ppm chlorine ≈ 2.9 mg/m³; ammonia ≈ 0.7 mg/m³.
    public static readonly Substance Chlorine = new("chlorine", 1.5, 5.8, 58);
    public static readonly Substance Ammonia = new("ammonia", 21, 112, 770);
    public static readonly Substance Smoke = new("smoke", 0.5, 5, 50);

    /// <summary>A known substance by name, or an irritant gas with chlorine-like thresholds.</summary>
    public static Substance For(string? name)
    {
        var key = name?.ToLowerInvariant() ?? "";
        if (key.Contains("chlorine")) return Chlorine;
        if (key.Contains("ammonia")) return Ammonia;
        if (key.Contains("smoke")) return Smoke;
        return Chlorine with { Name = name ?? "toxic gas" };
    }
}

/// <summary>
/// A simple ground-level Gaussian plume for a continuous release (Design Document §10.4; Phase 12 brings the full
/// models). Concentration downwind at (x, y): C = Q / (π·u·σy·σz) · exp(−y² / 2σy²), with Briggs urban dispersion
/// for neutral conditions (class D). The cloud only reaches as far downwind as the wind has carried it since the
/// release began, and it swings with the wind. When the release stops, the cloud clears after a few minutes.
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

    public PlumeModel(GeoPoint origin, string? substance)
    {
        _origin = origin;
        _plane = new LocalGrid(origin, 1, 0);
        Substance = Substance.For(substance);
    }

    public HazardKind Kind => HazardKind.Plume;
    public Substance Substance { get; }

    public bool IsActive => _stoppedAt is { } stopped ? _now - stopped < ClearanceTime : _rate > 0;

    /// <summary>Area above the Low threshold.</summary>
    public double AreaSquareMeters { get; private set; }

    public double Intensity => Concentration(100, 0);

    /// <summary>How far downwind the cloud has travelled.</summary>
    public double FrontMeters => _frontMeters;

    /// <summary>Areas above each threshold, lowest (largest) first.</summary>
    public IReadOnlyList<(HazardLevel Level, Polygon Area)> Zones => _zones;

    public void Step(HazardStepInput input, TimeSpan delta)
    {
        _now = input.SimTime;
        _windFrom = input.Weather.WindFromDegrees;
        _windSpeed = Math.Max(1, input.Weather.WindSpeedMps); // calm air still drifts; avoids dividing by zero

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
            AreaSquareMeters = 0;
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
        AreaSquareMeters = _zones.Count > 0 ? AreaOf(_zones[0].Area) : 0;
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

    public IReadOnlyList<Polygon> Footprint() => _zones.Count > 0 ? [_zones[0].Area] : [];

    public HazardExposure ExposureAt(GeoPoint point)
    {
        if (!IsActive) return HazardExposure.None;
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
