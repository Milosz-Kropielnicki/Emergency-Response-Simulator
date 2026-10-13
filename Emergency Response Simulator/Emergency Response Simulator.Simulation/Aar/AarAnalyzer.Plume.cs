using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Plume projections in the review (Phase 13): how soon after a release was reported command projected where it was
/// going, and whether the schools, hospitals and homes the projection found were then covered by an evacuation or
/// shelter zone — and how long that took. A projection nobody acts on is a missed warning.
/// </summary>
public sealed partial class AarAnalyzer
{
    /// <summary>Projected within this long of the release being reported: prompt.</summary>
    public static readonly TimeSpan ProjectPromptly = TimeSpan.FromMinutes(10);

    /// <summary>A protective zone over the places a projection found, within this long: in time.</summary>
    public static readonly TimeSpan ProtectPromptly = TimeSpan.FromMinutes(15);

    private readonly Dictionary<Guid, (string Number, DateTimeOffset At)> _releases = [];
    private readonly List<(DateTimeOffset At, PlumeProjected Projection)> _projections = [];
    private readonly List<(DateTimeOffset At, ZoneDeclared Zone)> _protective = [];
    private readonly Dictionary<string, int> _dispersionModels = [];

    private void TrackPlume(SimEvent simEvent)
    {
        switch (simEvent.Payload)
        {
            case IncidentCreated { Type: IncidentType.HazmatRelease } e when simEvent.Visibility == EventVisibility.Perceived:
                _releases[e.IncidentId] = (e.Number, simEvent.SimTime);
                break;
            case PlumeProjected e:
                _projections.Add((simEvent.SimTime, e));
                break;
            case ZoneDeclared { Type: ZoneType.EvacuationZone or ZoneType.ShelterInPlace } e:
                _protective.Add((simEvent.SimTime, e));
                break;
            case PlumePredicted e:
                _dispersionModels[e.Model] = _dispersionModels.GetValueOrDefault(e.Model) + 1;
                break;
        }
    }

    private IEnumerable<AarFinding> PlumeFindings()
    {
        // Projecting the release.
        foreach (var (incidentId, release) in _releases)
        {
            var first = _projections.Where(p => p.Projection.IncidentId == incidentId).Select(p => (DateTimeOffset?)p.At).FirstOrDefault();
            if (first is not { } at)
                yield return new AarFinding(false, $"{release.Number}: the release was never projected, so command never saw who was downwind.");
            else
                yield return new AarFinding(at - release.At <= ProjectPromptly,
                    $"{release.Number}: plume projected {Duration(at - release.At)} after the release was reported.");
        }

        // Acting on what the projection found: once per incident, from the first projection that found anything pressing.
        foreach (var group in _projections.Where(p => p.Projection.Impacts.Any(Pressing)).GroupBy(p => p.Projection.IncidentId))
        {
            var (at, projection) = group.First();
            var area = Area(projection, PlumeLevels.Moderate) ?? Area(projection, PlumeLevels.High);
            if (area is null) continue;
            var found = string.Join(", ", projection.Impacts.Where(Pressing).Select(i => $"{i.Count} {i.Kind.ToLowerInvariant()}(s)"));
            var label = projection.IncidentId is { } id && _incidents.TryGetValue(id, out var incident) ? incident.Number : "A release";

            var covered = _protective
                .Where(z => Polygon(z.Zone.Boundary) is { } zone && zone.Intersects(area))
                .Select(z => (DateTimeOffset?)z.At)
                .Order()
                .FirstOrDefault();
            if (covered is not { } protectedAt)
                yield return new AarFinding(false, $"{label}: the projection at {Clock(at)} put {found} in the moderate or high zone, and no evacuation or shelter zone ever covered them.");
            else if (protectedAt <= at)
                yield return new AarFinding(true, $"{label}: {found} in the projected moderate or high zone were already inside a protective zone.");
            else
                yield return new AarFinding(protectedAt - at <= ProtectPromptly,
                    $"{label}: the projection at {Clock(at)} put {found} in the moderate or high zone; a protective zone covered them {Duration(protectedAt - at)} later.");
        }
    }

    /// <summary>Which dispersion model drove the real cloud, for the instructor's notes: the service's, or the fallback.</summary>
    public IReadOnlyDictionary<string, int> DispersionModels => _dispersionModels;

    private static bool Pressing(PlumeImpact impact) => impact.Level != PlumeLevels.Low;

    private static Polygon? Area(PlumeProjected projection, string level) =>
        projection.Zones.FirstOrDefault(z => z.Level == level) is { } zone ? Polygon(zone.Boundary) : null;

    private static Polygon? Polygon(IReadOnlyList<GeoPoint> boundary)
    {
        if (boundary.Count < 4) return null;
        var ring = boundary.Select(p => new Coordinate(p.Longitude, p.Latitude)).ToList();
        if (!ring[0].Equals2D(ring[^1])) ring.Add(ring[0]);
        var polygon = Wgs84.Factory.CreatePolygon([.. ring]);
        return polygon.IsValid ? polygon : null;
    }
}
