using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// Surface flooding on 40 m cells (Design Document §10.4). Water enters at the source at the inflow rate and runs
/// downhill: each step, every wet cell passes part of the difference in water-surface height to lower neighbours.
/// Drains slowly, and water reaching the edge of the modelled area runs off it. Uses the elevation grid when there
/// is one; on flat ground the water spreads evenly.
/// </summary>
public sealed class FloodModel : IHazardModel
{
    public const double CellMeters = 40;
    public const int HalfCells = 30;

    /// <summary>Water deeper than this closes a road to ordinary and emergency vehicles.</summary>
    public const double ImpassableDepth = 0.25;

    /// <summary>Gullies and soakage: metres per second.</summary>
    private const double DrainRate = 0.5 / 1000 / 60;

    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(10);
    private static readonly (int Dc, int Dr)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private readonly LocalGrid _grid;
    private readonly double[] _ground;
    private readonly double[] _depth;
    private readonly int _source;
    private double _rate;

    public FloodModel(GeoPoint source, IHazardTerrain terrain)
    {
        _grid = new LocalGrid(source, CellMeters, HalfCells);
        _ground = new double[_grid.CellCount];
        _depth = new double[_grid.CellCount];
        for (var i = 0; i < _grid.CellCount; i++)
            _ground[i] = terrain.Elevation(_grid.CentreOf(i));
        _source = _grid.Index(HalfCells, HalfCells);
    }

    public HazardKind Kind => HazardKind.Flood;
    public LocalGrid Grid => _grid;
    public bool IsActive => _rate > 0 || MaxDepth >= 0.05;
    public double AreaSquareMeters => _depth.Count(d => d >= 0.1) * _grid.CellArea;
    public double Intensity => MaxDepth;
    public double MaxDepth => _depth.Max();

    public double DepthAt(GeoPoint point) => _grid.TryIndex(point, out var index) ? _depth[index] : 0;

    public void Step(HazardStepInput input, TimeSpan delta)
    {
        _rate = input.Rate;
        var remaining = delta;
        while (remaining > TimeSpan.Zero)
        {
            var step = remaining < MaxStep ? remaining : MaxStep;
            Advance(step.TotalSeconds);
            remaining -= step;
        }
    }

    private void Advance(double seconds)
    {
        _depth[_source] += _rate * seconds / _grid.CellArea;

        // Share of a surface-height difference that flows in one step; stays stable (≤ 1/4 per neighbour).
        var fraction = Math.Min(0.25, seconds / 40);
        var change = new double[_depth.Length];
        var wants = new double[Neighbours.Length];

        for (var i = 0; i < _depth.Length; i++)
        {
            if (_depth[i] <= 1e-4) continue;
            var surface = _ground[i] + _depth[i];
            var (col, row) = _grid.Cell(i);

            double total = 0;
            for (var n = 0; n < Neighbours.Length; n++)
            {
                wants[n] = 0;
                var (dc, dr) = Neighbours[n];
                if (!_grid.Contains(col + dc, row + dr)) continue;
                var j = _grid.Index(col + dc, row + dr);
                var drop = surface - (_ground[j] + _depth[j]);
                if (drop <= 0) continue;
                wants[n] = drop / 2 * fraction;
                total += wants[n];
            }
            if (total <= 0) continue;

            // Can't send more water than the cell holds.
            var scale = Math.Min(1, _depth[i] / total);
            for (var n = 0; n < Neighbours.Length; n++)
            {
                if (wants[n] <= 0) continue;
                var (dc, dr) = Neighbours[n];
                var amount = wants[n] * scale;
                change[i] -= amount;
                change[_grid.Index(col + dc, row + dr)] += amount;
            }
        }

        var drained = DrainRate * seconds;
        for (var i = 0; i < _depth.Length; i++)
        {
            var (col, row) = _grid.Cell(i);
            var edge = col == 0 || row == 0 || col == _grid.Size - 1 || row == _grid.Size - 1;
            _depth[i] = edge ? 0 : Math.Max(0, _depth[i] + change[i] - (_depth[i] > 0 ? drained : 0));
        }
    }

    /// <summary>Cells deeper than <paramref name="depth"/>, with their outline and depth: used to close roads.</summary>
    public IEnumerable<(int Index, double Depth)> CellsDeeperThan(double depth)
    {
        for (var i = 0; i < _depth.Length; i++)
        {
            if (_depth[i] >= depth) yield return (i, _depth[i]);
        }
    }

    public double DepthOf(int index) => _depth[index];

    public IReadOnlyList<Polygon> Footprint() =>
        _grid.Outline(Enumerable.Range(0, _depth.Length).Where(i => _depth[i] >= 0.1));

    public HazardExposure ExposureAt(GeoPoint point)
    {
        var depth = DepthAt(point);
        return depth switch
        {
            >= 1.0 => new HazardExposure(HazardLevel.High, depth),
            >= 0.5 => new HazardExposure(HazardLevel.Moderate, depth),
            >= 0.15 => new HazardExposure(HazardLevel.Low, depth),
            _ => HazardExposure.None,
        };
    }
}
