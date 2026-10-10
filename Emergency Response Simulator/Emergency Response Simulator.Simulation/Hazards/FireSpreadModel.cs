using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// Fire spread as a cellular automaton on 15 m cells (Design Document §10.4). Each burning cell may ignite its
/// eight neighbours with a probability that rises with the neighbour's fuel (buildings burn, water doesn't) and
/// with the wind behind it, then burns out after 15–30 minutes. Crews' water puts out burning cells within reach
/// and cools the cells around them. Seeded, so the same fire spreads the same way every run.
/// </summary>
public sealed class FireSpreadModel : IHazardModel
{
    public const double CellMeters = 15;
    public const int HalfCells = 50;

    /// <summary>
    /// Ignition rate into a fully fuelled neighbour with no wind, per second. A cell on the fire's edge has about three
    /// burning neighbours, so the front crosses a 15 m cell in roughly 1 / (2.4 × rate) ≈ 7 minutes in still air
    /// (~2 m/min, building to building) and about three times faster downwind in a 7 m/s wind.
    /// </summary>
    public const double BaseIgnitionRate = 1.0 / 1080;

    /// <summary>How strongly wind pushes the fire: the rate is multiplied by e^(k·speed·cos θ).</summary>
    public const double WindCoefficient = 0.15;

    /// <summary>The model advances in steps no longer than this, so a 30 s tick behaves like six 5 s ones.</summary>
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(5);

    /// <summary>A cell within this distance of a burning cell is uncomfortably hot.</summary>
    private const double HeatMeters = 25;

    /// <summary>Smoke is obvious within this distance.</summary>
    private const double SmokeMeters = 150;

    private enum CellState : byte
    {
        Unburned,
        Burning,
        BurnedOut,
    }

    // Neighbour offsets with their distance factor (diagonals are further away).
    private static readonly (int Dc, int Dr, double Factor)[] Neighbours =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 0.7), (1, -1, 0.7), (-1, 1, 0.7), (-1, -1, 0.7),
    ];

    private readonly LocalGrid _grid;
    private readonly CellState[] _state;
    private readonly float[] _fuel;
    private readonly float[] _burnLeft;
    private readonly float[] _wet;
    private readonly Random _random;
    private readonly List<int> _burning = [];
    private int _burnedOut;

    public FireSpreadModel(GeoPoint origin, double severity, IHazardTerrain terrain, int seed)
    {
        _grid = new LocalGrid(origin, CellMeters, HalfCells);
        _state = new CellState[_grid.CellCount];
        _fuel = new float[_grid.CellCount];
        _burnLeft = new float[_grid.CellCount];
        _wet = new float[_grid.CellCount];
        _random = new Random(seed);

        for (var i = 0; i < _grid.CellCount; i++)
            _fuel[i] = (float)Math.Clamp(terrain.Fuel(_grid.CentreOf(i)), 0, 1);

        // A bigger starting fire involves more of the building straight away.
        var radius = severity switch { < 0.3 => 0, < 0.6 => 1, _ => 2 };
        var centre = _grid.Index(HalfCells, HalfCells);
        _fuel[centre] = Math.Max(_fuel[centre], 0.9f); // the fire started somewhere that burns
        for (var dr = -radius; dr <= radius; dr++)
        {
            for (var dc = -radius; dc <= radius; dc++)
            {
                var index = _grid.Index(HalfCells + dc, HalfCells + dr);
                if (_fuel[index] > 0) Ignite(index);
            }
        }
    }

    public HazardKind Kind => HazardKind.Fire;
    public LocalGrid Grid => _grid;
    public bool IsActive => _burning.Count > 0;

    /// <summary>Burning and burnt-out area together: the fire's extent so far.</summary>
    public double AreaSquareMeters => (_burning.Count + _burnedOut) * _grid.CellArea;

    public double Intensity => BurningAreaSquareMeters;

    public double BurningAreaSquareMeters => _burning.Count * _grid.CellArea;

    public int BurningCells => _burning.Count;

    public void Step(HazardStepInput input, TimeSpan delta)
    {
        var remaining = delta;
        while (remaining > TimeSpan.Zero && _burning.Count > 0)
        {
            var step = remaining < MaxStep ? remaining : MaxStep;
            Advance(input, step.TotalSeconds);
            remaining -= step;
        }
    }

    private void Advance(HazardStepInput input, double seconds)
    {
        foreach (var crew in input.Suppression ?? [])
            Suppress(crew, seconds);

        // Wind pushes fire towards where it is blowing (opposite to where it comes from).
        var windTo = (input.Weather.WindFromDegrees + 180) * Math.PI / 180;
        var windSpeed = input.Weather.WindSpeedMps;

        var ignitions = new List<int>();
        foreach (var index in _burning.ToList())
        {
            _burnLeft[index] -= (float)seconds;
            if (_burnLeft[index] <= 0)
            {
                _state[index] = CellState.BurnedOut;
                _burning.Remove(index);
                _burnedOut++;
                continue;
            }

            var (col, row) = _grid.Cell(index);
            foreach (var (dc, dr, factor) in Neighbours)
            {
                if (!_grid.Contains(col + dc, row + dr)) continue;
                var neighbour = _grid.Index(col + dc, row + dr);
                if (_state[neighbour] != CellState.Unburned || _fuel[neighbour] <= 0) continue;

                // Angle between the direction of spread (east = +col, north = +row) and the wind.
                var spread = Math.Atan2(dc, dr); // bearing, clockwise from north
                var alignment = Math.Cos(spread - windTo);
                var rate = BaseIgnitionRate * _fuel[neighbour] * factor * (1 - _wet[neighbour])
                           * Math.Exp(WindCoefficient * windSpeed * alignment);
                if (_random.NextDouble() < 1 - Math.Exp(-rate * seconds))
                    ignitions.Add(neighbour);
            }
        }

        foreach (var index in ignitions)
        {
            if (_state[index] == CellState.Unburned) Ignite(index);
        }

        // Water soaked into unburnt cells dries out over ten minutes or so.
        var drying = (float)Math.Exp(-seconds / 600);
        for (var i = 0; i < _wet.Length; i++)
        {
            if (_wet[i] > 0) _wet[i] *= drying;
        }
    }

    /// <summary>A crew puts out the burning cells nearest to it and soaks the unburnt cells around them.</summary>
    private void Suppress(Suppression crew, double seconds)
    {
        if (!_grid.TryIndex(crew.Location, out _) && GeoMath.DistanceMeters(crew.Location, _grid.Centre) > HalfCells * CellMeters + crew.ReachMeters)
            return;

        var (ex, ey) = _grid.ToLocal(crew.Location);
        double DistanceTo(int index)
        {
            var (cx, cy) = _grid.LocalCentre(index);
            return Math.Sqrt((cx - ex) * (cx - ex) + (cy - ey) * (cy - ey));
        }

        var inReach = _burning.Select(i => (Index: i, Distance: DistanceTo(i)))
            .Where(x => x.Distance <= crew.ReachMeters)
            .OrderBy(x => x.Distance)
            .ToList();
        if (inReach.Count == 0) return;

        var budget = crew.CellsPerMinute * seconds / 60;
        var whole = (int)Math.Floor(budget);
        if (_random.NextDouble() < budget - whole) whole++;

        foreach (var (index, _) in inReach.Take(whole))
        {
            _state[index] = CellState.BurnedOut;
            _burning.Remove(index);
            _burnedOut++;
        }

        // Cooling: crews also wet the unburnt cells around the fire edge they are working on.
        foreach (var (index, _) in inReach)
        {
            var (col, row) = _grid.Cell(index);
            foreach (var (dc, dr, _) in Neighbours)
            {
                if (!_grid.Contains(col + dc, row + dr)) continue;
                var neighbour = _grid.Index(col + dc, row + dr);
                if (_state[neighbour] == CellState.Unburned)
                    _wet[neighbour] = Math.Max(_wet[neighbour], 0.75f);
            }
        }
    }

    private void Ignite(int index)
    {
        _state[index] = CellState.Burning;
        _burnLeft[index] = 900 + 900 * _fuel[index]; // 15–30 minutes
        _burning.Add(index);
    }

    public bool IsBurningAt(GeoPoint point) =>
        _grid.TryIndex(point, out var index) && _state[index] == CellState.Burning;

    /// <summary>Distance from a point to the nearest burning cell's edge, or infinity when nothing burns.</summary>
    public double DistanceToFire(GeoPoint point)
    {
        if (_burning.Count == 0) return double.PositiveInfinity;
        var (px, py) = _grid.ToLocal(point);
        var best = double.PositiveInfinity;
        foreach (var index in _burning)
        {
            var (cx, cy) = _grid.LocalCentre(index);
            var d = Math.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
            if (d < best) best = d;
        }
        return Math.Max(0, best - CellMeters / 2);
    }

    /// <summary>The compass direction the fire has grown furthest in from its origin, e.g. "NE".</summary>
    public string? SpreadDirection()
    {
        if (_burning.Count == 0) return null;
        var (east, north) = _burning.Select(_grid.LocalCentre).Aggregate((0.0, 0.0), (a, c) => (a.Item1 + c.East, a.Item2 + c.North));
        if (Math.Abs(east) + Math.Abs(north) < CellMeters * _burning.Count * 0.3) return null; // roughly even all round
        return GeoMath.CompassPoint(Math.Atan2(east, north) * 180 / Math.PI);
    }

    public IReadOnlyList<Polygon> Footprint() =>
        _grid.Outline(Enumerable.Range(0, _state.Length).Where(i => _state[i] != CellState.Unburned));

    public IReadOnlyList<Polygon> BurningFootprint() => _grid.Outline(_burning);

    public HazardExposure ExposureAt(GeoPoint point)
    {
        var distance = DistanceToFire(point);
        return distance switch
        {
            <= 0 => new HazardExposure(HazardLevel.High, 0),
            <= HeatMeters => new HazardExposure(HazardLevel.Moderate, distance),
            <= SmokeMeters => new HazardExposure(HazardLevel.Low, distance),
            _ => HazardExposure.None,
        };
    }
}
