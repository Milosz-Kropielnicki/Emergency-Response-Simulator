using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// The AVL feed (Design Document §7.4): vehicle ID, position, timestamp, speed, heading and status for
/// every fix received, plus a short trail of recent positions for the map. Built from perceived
/// <see cref="UnitPositionReported"/> events, so it only knows what the vehicles managed to transmit.
/// </summary>
public sealed class AvlService : IAvlService
{
    /// <summary>Fixes kept per unit for the breadcrumb trail.</summary>
    public const int TrailLength = 40;

    private readonly ICopService _cop;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, AvlFix> _latest = [];
    private readonly Dictionary<Guid, Queue<AvlFix>> _trails = [];

    public AvlService(ICopService cop, IEventStore store, Guid sessionId)
    {
        _cop = cop;
        store.Appended += (_, simEvent) =>
        {
            if (simEvent.SessionId == sessionId && simEvent.Visibility == EventVisibility.Perceived
                && simEvent.Payload is UnitPositionReported fix)
            {
                Receive(fix, simEvent.SimTime);
            }
        };
    }

    public event EventHandler<AvlFix>? FixReceived;

    public IReadOnlyList<AvlFix> LatestFixes
    {
        get { lock (_lock) return _latest.Values.ToList(); }
    }

    public AvlFix? GetLatest(Guid unitId)
    {
        lock (_lock) return _latest.GetValueOrDefault(unitId);
    }

    /// <summary>Recent fixes for a unit, oldest first.</summary>
    public IReadOnlyList<AvlFix> GetTrail(Guid unitId)
    {
        lock (_lock) return _trails.TryGetValue(unitId, out var trail) ? trail.ToList() : [];
    }

    public void Receive(UnitPositionReported report, DateTimeOffset at)
    {
        var status = _cop.FindUnit(report.UnitId)?.Status ?? UnitStatus.Available;
        var fix = new AvlFix(report.UnitId, report.Location, at, report.SpeedKph, report.Heading, status);

        lock (_lock)
        {
            _latest[report.UnitId] = fix;
            if (!_trails.TryGetValue(report.UnitId, out var trail))
                _trails[report.UnitId] = trail = new Queue<AvlFix>();

            // Skip stationary heartbeats so the trail shows movement, not time.
            if (trail.Count == 0 || GeoMath.DistanceMeters(trail.Last().Location, fix.Location) > 5)
                trail.Enqueue(fix);
            while (trail.Count > TrailLength)
                trail.Dequeue();
        }

        FixReceived?.Invoke(this, fix);
    }
}
