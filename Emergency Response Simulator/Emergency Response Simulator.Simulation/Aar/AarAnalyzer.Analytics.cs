using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>Resource commitment, delays and heatmaps (Design Document §22: "where resources were committed, where delays occurred").</summary>
public sealed partial class AarAnalyzer
{
    /// <summary>Heatmap cells.</summary>
    public const double CellMeters = 150;

    /// <summary>A unit crawling slower than this while en route counts as held up.</summary>
    public const double CrawlKph = 10;

    /// <summary>Stops shorter than this are traffic lights and junctions, not delays.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, List<(DateTimeOffset At, UnitStatus Status)>> _statuses = [];
    private readonly Dictionary<Guid, List<(DateTimeOffset At, GeoPoint Location, double SpeedKph)>> _fixes = [];
    private readonly Dictionary<Guid, List<(DateTimeOffset At, string Reason, TimeSpan Eta)>> _routes = [];

    /// <summary>Unit status changes, AVL fixes and route changes as command received them.</summary>
    private void Collect(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        switch (simEvent.Payload)
        {
            case UnitRegistered e:
                Status(e.UnitId, UnitStatus.Available);
                Fix(e.UnitId, e.Location, 0);
                break;
            case UnitDispatched e:
                Status(e.UnitId, UnitStatus.Dispatched);
                break;
            case UnitTasked e:
                Status(e.UnitId, UnitStatus.Dispatched);
                break;
            case UnitDispatchCancelled e:
                Status(e.UnitId, UnitStatus.Available);
                break;
            case UnitStatusChanged e:
                Status(e.UnitId, e.Status);
                break;
            case UnitPositionReported e:
                Fix(e.UnitId, e.Location, e.SpeedKph);
                break;
            case RouteReported e:
                if (!_routes.TryGetValue(e.UnitId, out var routes)) _routes[e.UnitId] = routes = [];
                routes.Add((at, e.Reason ?? "Dispatched", e.Eta));
                break;
        }

        void Status(Guid unit, UnitStatus status)
        {
            if (!_statuses.TryGetValue(unit, out var list)) _statuses[unit] = list = [];
            if (list.Count == 0 || list[^1].Status != status) list.Add((at, status));
        }

        void Fix(Guid unit, GeoPoint location, double speed)
        {
            if (!_fixes.TryGetValue(unit, out var list)) _fixes[unit] = list = [];
            list.Add((at, location, speed));
        }
    }

    /// <summary>The unit's status at a moment, from what command was told.</summary>
    private UnitStatus StatusAt(Guid unitId, DateTimeOffset at) =>
        _statuses.TryGetValue(unitId, out var list) ? list.LastOrDefault(s => s.At <= at).Status : UnitStatus.Available;

    // ---- Commitment ----

    private List<AarUnitUsage> Usage()
    {
        var usage = new List<AarUnitUsage>();
        foreach (var (unitId, changes) in _statuses.Where(s => OwnUnit(s.Key)))
        {
            var spans = new List<AarStatusSpan>();
            for (var i = 0; i < changes.Count; i++)
            {
                var to = i + 1 < changes.Count ? changes[i + 1].At : End;
                if (to > changes[i].At) spans.Add(new AarStatusSpan(changes[i].At, to, changes[i].Status));
            }
            var total = (End - changes[0].At).TotalSeconds;
            var committed = spans.Where(s => UnitStatusRules.IsCommitted(s.Status)).Sum(s => (s.To - s.From).TotalSeconds);
            var delayed = UnitDelays(unitId).Aggregate(TimeSpan.Zero, (sum, d) => sum + d.Lost);
            usage.Add(new AarUnitUsage(unitId, Callsign(unitId), ResourceGroups.Label(_unitTypes.GetValueOrDefault(unitId)), spans,
                total <= 0 ? 0 : committed / total, delayed > TimeSpan.Zero ? delayed : null));
        }
        return usage.OrderByDescending(u => u.Committed).ThenBy(u => u.Callsign, StringComparer.Ordinal).ToList();
    }

    // ---- Delays ----

    private List<AarDelay> Delays() =>
        _fixes.Keys.Where(OwnUnit).SelectMany(UnitDelays).OrderByDescending(d => d.Lost).ToList();

    /// <summary>
    /// Where a unit was held up on the way: stopped or crawling for a minute or more while en route, and re-routes that
    /// pushed its arrival back.
    /// </summary>
    private IEnumerable<AarDelay> UnitDelays(Guid unitId)
    {
        var callsign = Callsign(unitId);
        if (_fixes.TryGetValue(unitId, out var fixes))
        {
            DateTimeOffset? since = null;
            GeoPoint where = default;
            for (var i = 0; i < fixes.Count; i++)
            {
                var (at, location, speed) = fixes[i];
                var slow = StatusAt(unitId, at) == UnitStatus.EnRoute && speed < CrawlKph;
                if (slow && since is null)
                {
                    since = at;
                    where = location;
                }
                var last = i == fixes.Count - 1;
                if (since is { } start && (!slow || last))
                {
                    var lost = at - start;
                    if (lost >= MinimumDelay)
                        yield return new AarDelay(callsign, start, lost, Place(where), Cause(unitId, start, at));
                    since = null;
                }
            }
        }

        if (_routes.TryGetValue(unitId, out var routes))
        {
            for (var i = 1; i < routes.Count; i++)
            {
                if (!routes[i].Reason.StartsWith("Re-routed", StringComparison.OrdinalIgnoreCase)) continue;
                // Arrival expected before and after the re-route.
                var before = routes[i - 1].At + routes[i - 1].Eta;
                var after = routes[i].At + routes[i].Eta;
                if (after - before >= MinimumDelay)
                    yield return new AarDelay(callsign, routes[i].At, after - before, Position(unitId, routes[i].At), routes[i].Reason);
            }
        }
    }

    private string Position(Guid unitId, DateTimeOffset at) =>
        _fixes.TryGetValue(unitId, out var fixes) && fixes.Any(f => f.At <= at) ? Place(fixes.Last(f => f.At <= at).Location) : "unknown";

    /// <summary>What the record says held the unit up: a re-route reason near the time, or slow traffic.</summary>
    private string Cause(Guid unitId, DateTimeOffset from, DateTimeOffset to)
    {
        if (_routes.TryGetValue(unitId, out var routes)
            && routes.FirstOrDefault(r => r.At >= from - TimeSpan.FromSeconds(30) && r.At <= to + TimeSpan.FromSeconds(30)
                                          && r.Reason.StartsWith("Re-routed", StringComparison.OrdinalIgnoreCase)) is { Reason: { } reason })
            return reason;
        return _fixes[unitId].Where(f => f.At >= from && f.At <= to).All(f => f.SpeedKph < 1) ? "Stopped" : "Slow traffic";
    }

    // ---- Heatmaps ----

    private List<AarHeatmap> Heatmaps(IReadOnlyList<AarUnitUsage> usage)
    {
        var focus = _incidents.Values.OrderBy(i => i.At).Select(i => (GeoPoint?)i.Location).FirstOrDefault()
                    ?? _truth.Incidents.Values.OrderBy(i => i.StartedAt).Select(i => (GeoPoint?)i.Location).FirstOrDefault();
        if (focus is not { } centre) return [];

        const int size = 24; // 3.6 km across
        var southWest = GeoMath.Destination(GeoMath.Destination(centre, 180, size / 2.0 * CellMeters), 270, size / 2.0 * CellMeters);
        var committed = new double[size * size];
        var delayed = new double[size * size];
        // A flat approximation is plenty over a few kilometres.
        var metersPerDegreeLon = 111_320 * Math.Cos(southWest.Latitude * Math.PI / 180);
        const double metersPerDegreeLat = 110_540;

        foreach (var (unitId, fixes) in _fixes.Where(f => OwnUnit(f.Key)))
        {
            for (var i = 0; i + 1 < fixes.Count; i++)
            {
                var (at, location, speed) = fixes[i];
                var minutes = Math.Min(5, (fixes[i + 1].At - at).TotalMinutes); // gaps longer than that are lost signal, not presence
                var status = StatusAt(unitId, at);
                if (!UnitStatusRules.IsCommitted(status) || minutes <= 0) continue;
                if (Cell(location) is not { } cell) continue;
                committed[cell] += minutes;
                if (status == UnitStatus.EnRoute && speed < CrawlKph) delayed[cell] += minutes;
            }
        }

        return
        [
            Map("Resource commitment", "unit-min", committed),
            Map("Delays en route", "min held up", delayed),
        ];

        int? Cell(GeoPoint point)
        {
            var column = (int)Math.Floor((point.Longitude - southWest.Longitude) * metersPerDegreeLon / CellMeters);
            var row = (int)Math.Floor((point.Latitude - southWest.Latitude) * metersPerDegreeLat / CellMeters);
            return column is >= 0 and < size && row is >= 0 and < size ? row * size + column : null;
        }

        AarHeatmap Map(string title, string unit, double[] values)
        {
            var map = new AarHeatmap
            {
                Title = title, Unit = unit, SouthWest = southWest, CellMeters = CellMeters, Columns = size, Rows = size, Values = values, Focus = centre,
            };
            var hotspots = values.Select((v, i) => (Value: v, Index: i)).Where(c => c.Value > 0.5).OrderByDescending(c => c.Value).Take(3)
                .Select(c => $"{Place(map.CellCentre(c.Index % size, c.Index / size))}: {c.Value:F0} {unit}").ToList();
            return new AarHeatmap
            {
                Title = title, Unit = unit, SouthWest = southWest, CellMeters = CellMeters, Columns = size, Rows = size, Values = values, Focus = centre,
                Hotspots = hotspots,
            };
        }
    }
}
