using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.GisImport;

/// <summary>
/// What to fetch from OpenStreetMap for each static layer, and which tags to keep as feature properties.
/// </summary>
public sealed record OsmLayerQuery(string LayerKey, string Statements, IReadOnlyList<string> KeepTags)
{
    /// <summary>Builds the full Overpass QL request for a bounding box ("south,west,north,east").</summary>
    /// <remarks>
    /// Ways and nodes use the compact "tags" verbosity, but relations need the default verbosity:
    /// "tags" omits their member list, and multipolygons are assembled from those members.
    /// </remarks>
    public string ToOverpassQl(string bbox) =>
        $"[out:json][timeout:300][maxsize:536870912][bbox:{bbox}];\n({Statements})->.all;\n" +
        "(node.all;way.all;);out tags geom;\nrel.all;out geom;";
}

public static class OsmLayerQueries
{
    private static readonly string[] Common = ["name", "operator"];

    public static IReadOnlyList<OsmLayerQuery> All { get; } =
    [
        new(GisLayerKeys.Roads,
            """way["highway"~"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|pedestrian|motorway_link|trunk_link|primary_link|secondary_link|tertiary_link)$"];""",
            [.. Common, "highway", "ref", "oneway", "maxspeed", "lanes", "bridge", "tunnel", "junction", "access", "surface"]),

        new(GisLayerKeys.Buildings,
            """way["building"];relation["building"]["type"="multipolygon"];""",
            [.. Common, "building", "building:levels", "height", "addr:street", "addr:housenumber", "amenity"]),

        new(GisLayerKeys.Water,
            """way["waterway"~"^(river|canal|stream|drain|riverbank|dock)$"];way["natural"="water"];relation["natural"="water"];""",
            [.. Common, "waterway", "natural", "water", "tunnel"]),

        new(GisLayerKeys.Railways,
            """way["railway"~"^(rail|light_rail|tram|subway|narrow_gauge)$"];""",
            [.. Common, "railway", "usage", "electrified", "bridge", "tunnel"]),

        new(GisLayerKeys.AreaBoundaries,
            """relation["boundary"="administrative"]["admin_level"~"^(9|10)$"];""",
            [.. Common, "admin_level", "ref", "official_name", "name:ga"]),

        new(GisLayerKeys.Hospitals,
            """nwr["amenity"="hospital"];nwr["healthcare"="hospital"];""",
            [.. Common, "amenity", "healthcare", "emergency", "beds", "addr:street"]),

        new(GisLayerKeys.FireStations,
            """nwr["amenity"="fire_station"];""",
            [.. Common, "amenity", "addr:street"]),

        new(GisLayerKeys.PoliceStations,
            """nwr["amenity"="police"];""",
            [.. Common, "amenity", "police", "addr:street"]),

        new(GisLayerKeys.AmbulanceStations,
            """nwr["emergency"="ambulance_station"];""",
            [.. Common, "emergency", "addr:street"]),

        new(GisLayerKeys.Shelters,
            """nwr["social_facility"="shelter"];nwr["emergency"="assembly_point"];nwr["amenity"="community_centre"];""",
            [.. Common, "amenity", "social_facility", "emergency", "capacity"]),

        new(GisLayerKeys.Hydrants,
            """node["emergency"="fire_hydrant"];""",
            ["ref", "fire_hydrant:type", "fire_hydrant:diameter", "couplings"]),

        new(GisLayerKeys.Schools,
            """nwr["amenity"~"^(school|kindergarten|college|university|childcare)$"];""",
            [.. Common, "amenity", "capacity", "isced:level"]),

        new(GisLayerKeys.CriticalInfrastructure,
            """nwr["power"~"^(plant|substation)$"];nwr["man_made"~"^(water_works|wastewater_plant|water_tower|pumping_station|communications_tower)$"];nwr["telecom"="exchange"];nwr["amenity"="fuel"];nwr["railway"="station"];nwr["aeroway"="aerodrome"];nwr["industrial"~"^(chemical|oil|gas|refinery)$"];""",
            [.. Common, "power", "man_made", "telecom", "amenity", "railway", "aeroway", "industrial", "voltage", "substance"]),
    ];

    public static OsmLayerQuery? Find(string layerKey) => All.FirstOrDefault(q => q.LayerKey == layerKey);

    /// <summary>
    /// Adds a "category" property to critical infrastructure so the map and alerts can group it
    /// (power, water, fuel, transport, telecoms, chemical).
    /// </summary>
    public static string? InfrastructureCategory(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.ContainsKey("power")) return "Power";
        if (tags.TryGetValue("man_made", out var manMade))
            return manMade == "communications_tower" ? "Telecoms" : "Water";
        if (tags.ContainsKey("telecom")) return "Telecoms";
        if (tags.TryGetValue("amenity", out var amenity) && amenity == "fuel") return "Fuel";
        if (tags.ContainsKey("railway") || tags.ContainsKey("aeroway")) return "Transport";
        if (tags.ContainsKey("industrial")) return "Chemical";
        return null;
    }
}
