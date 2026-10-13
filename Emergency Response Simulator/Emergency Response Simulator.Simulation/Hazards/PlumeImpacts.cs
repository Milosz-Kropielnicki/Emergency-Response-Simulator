using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// What a projected plume covers on the ground, from the static GIS layers (Phase 13), and the alerts that follow.
/// <para>
/// A set of concentration zones is only half an answer: what command needs is "a school and 200 homes are in the
/// moderate zone, and the hospital is 400 m outside it". So each zone is intersected with the layers that matter —
/// schools, hospitals, shelters, critical infrastructure and buildings — and anything sensitive inside the moderate or
/// high zone raises an alert of its own, because those are the places that cannot simply be told to close the windows.
/// </para>
/// Each place is counted once, against the worst zone it falls in.
/// </summary>
public static class PlumeImpacts
{
    /// <summary>Layers worth checking a plume against, and what to call what is in them.</summary>
    private static readonly (string Layer, string Kind, bool Named)[] Checked =
    [
        (GisLayerKeys.Schools, "School", true),
        (GisLayerKeys.Hospitals, "Hospital", true),
        (GisLayerKeys.Shelters, "Shelter", true),
        (GisLayerKeys.CriticalInfrastructure, "Critical site", true),
        (GisLayerKeys.Buildings, "Home", false), // a street of houses is a number, not a list
    ];

    /// <summary>At most this many names per impact: enough for the alert to be useful, short enough to read.</summary>
    public const int MaxNames = 5;

    /// <summary>Places that cannot just close their windows: an alert of their own inside the worse zones.</summary>
    private static readonly string[] AlertFor = [GisLayerKeys.Schools, GisLayerKeys.Hospitals, GisLayerKeys.Shelters];

    /// <summary>
    /// What each zone covers. Empty when there is no GIS database (the simulator runs without one), which leaves the
    /// projection itself perfectly usable — it just cannot say what is underneath it.
    /// </summary>
    public static async Task<IReadOnlyList<PlumeImpact>> FindAsync(
        IGisService? gis, IReadOnlyList<PlumeZone> zones, CancellationToken cancellationToken = default)
    {
        if (gis is null || zones.Count == 0) return [];

        // Worst zone first, so a school in the high zone is not also counted in the low one around it.
        var ordered = zones
            .Select(z => (z.Level, Area: Outline(z.Boundary)))
            .Where(z => z.Area is not null)
            .OrderByDescending(z => PlumeLevels.Severity(z.Level))
            .ToList();
        if (ordered.Count == 0) return [];

        var impacts = new List<PlumeImpact>();
        var counted = new HashSet<Guid>();
        foreach (var (level, area) in ordered)
        {
            foreach (var (layer, kind, named) in Checked)
            {
                IReadOnlyList<GisFeature> found;
                try
                {
                    found = await gis.FindFeaturesWithinAsync(area!, [layer], cancellationToken);
                }
                catch (Exception)
                {
                    continue; // a layer that has never been imported, or a database that is away
                }

                var fresh = found.Where(f => counted.Add(f.Id)).ToList();
                if (layer == GisLayerKeys.Buildings) fresh = [.. fresh.Where(IsHome)];
                if (fresh.Count == 0) continue;

                var names = named
                    ? fresh.Select(f => f.Name).OfType<string>().Where(n => n.Length > 0).Distinct().OrderBy(n => n).Take(MaxNames).ToList()
                    : [];
                impacts.Add(new PlumeImpact(level, layer, kind, fresh.Count, names));
            }
        }
        return impacts;
    }

    /// <summary>
    /// A building people live in. OpenStreetMap tags most of them, and leaves plenty as a bare "yes"; those are counted
    /// too, because in a residential district that is what they are.
    /// </summary>
    private static bool IsHome(GisFeature building)
    {
        var kind = building.Properties.GetValueOrDefault("building", "").ToLowerInvariant();
        return kind is "" or "yes" or "residential" or "house" or "apartments" or "detached" or "semidetached_house"
            or "terrace" or "dormitory" or "bungalow" or "flats";
    }

    /// <summary>
    /// The alerts a projection raises: one for each sensitive place inside the moderate or high zone, and one for the
    /// homes in them. Worded as the deliverable asks — "School within projected hazard area".
    /// </summary>
    public static IEnumerable<AlertRaised> Alerts(PlumeProjected projection)
    {
        foreach (var impact in projection.Impacts.Where(i => i.Level != PlumeLevels.Low && AlertFor.Contains(i.LayerKey)))
        {
            var what = impact.Count == 1 ? impact.Kind : $"{impact.Count} {impact.Kind.ToLowerInvariant()}s";
            var named = impact.Names.Count > 0 ? $" ({string.Join(", ", impact.Names)})" : "";
            yield return new AlertRaised(
                Guid.NewGuid(),
                AlertCategory.Hazard,
                impact.Level == PlumeLevels.High ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"{what} within projected hazard area",
                $"{what}{named} {(impact.Count == 1 ? "is" : "are")} inside the {impact.Level} concentration zone for the " +
                $"{projection.Substance} release. They cannot shelter in place without help: decide on evacuation, who warns them, and how.",
                projection.IncidentId,
                null);
        }

        // Homes get one alert for the whole projection: a number, and what to tell them.
        var homes = projection.Impacts.Where(i => i.LayerKey == GisLayerKeys.Buildings && i.Level != PlumeLevels.Low).Sum(i => i.Count);
        if (homes > 0)
            yield return new AlertRaised(
                Guid.NewGuid(),
                AlertCategory.Hazard,
                AlertSeverity.Warning,
                $"About {homes} home(s) within projected hazard area",
                $"The projected {projection.Substance} plume covers about {homes} home(s) above the moderate threshold. " +
                "Shelter in place or evacuate, and get the message out before the cloud does.",
                projection.IncidentId,
                null);
    }

    private static Polygon? Outline(IReadOnlyList<GeoPoint> boundary)
    {
        if (boundary.Count < 4) return null;
        var ring = boundary.Select(p => new Coordinate(p.Longitude, p.Latitude)).ToList();
        if (!ring[0].Equals2D(ring[^1])) ring.Add(ring[0]);
        var polygon = Wgs84.Factory.CreatePolygon([.. ring]);
        return polygon.IsValid ? polygon : null;
    }
}
