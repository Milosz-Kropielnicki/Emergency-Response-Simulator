namespace Emergency_Response_Simulator.Core.Geo;

/// <summary>
/// Stable keys of the static GIS layers (Design Document §7.1). The importer writes them, the map
/// styles them, and services query them ("nearest hospital", "schools inside the plume").
/// </summary>
public static class GisLayerKeys
{
    // Base geography
    public const string Roads = "roads";
    public const string Buildings = "buildings";
    public const string Water = "water";
    public const string Railways = "railways";
    public const string Elevation = "elevation";
    public const string AreaBoundaries = "area-boundaries";

    // Emergency facilities
    public const string Hospitals = "hospitals";
    public const string FireStations = "fire-stations";
    public const string PoliceStations = "police-stations";
    public const string AmbulanceStations = "ambulance-stations";
    public const string Shelters = "shelters";
    public const string Hydrants = "hydrants";

    // Population and critical infrastructure
    public const string Schools = "schools";
    public const string CriticalInfrastructure = "critical-infrastructure";

    public static IReadOnlyList<GisLayerDefinition> All { get; } =
    [
        new(Roads, "Roads", GisLayerGroups.BaseGeography, 10, VisibleByDefault: true, MinZoom: 13),
        new(Buildings, "Buildings", GisLayerGroups.BaseGeography, 20, VisibleByDefault: true, MinZoom: 16),
        new(Water, "Rivers & water", GisLayerGroups.BaseGeography, 30, VisibleByDefault: false, MinZoom: 11),
        new(Railways, "Railways", GisLayerGroups.BaseGeography, 40, VisibleByDefault: false, MinZoom: 12),
        new(Elevation, "Terrain (elevation)", GisLayerGroups.BaseGeography, 50, VisibleByDefault: false, MinZoom: 11),
        new(AreaBoundaries, "Area boundaries", GisLayerGroups.BaseGeography, 60, VisibleByDefault: false, MinZoom: 10),
        new(Hospitals, "Hospitals", GisLayerGroups.Facilities, 100, VisibleByDefault: true, MinZoom: 10),
        new(FireStations, "Fire stations", GisLayerGroups.Facilities, 110, VisibleByDefault: true, MinZoom: 10),
        new(PoliceStations, "Police stations", GisLayerGroups.Facilities, 120, VisibleByDefault: true, MinZoom: 10),
        new(AmbulanceStations, "Ambulance stations", GisLayerGroups.Facilities, 130, VisibleByDefault: true, MinZoom: 10),
        new(Shelters, "Shelters & assembly points", GisLayerGroups.Facilities, 140, VisibleByDefault: false, MinZoom: 12),
        new(Hydrants, "Hydrants", GisLayerGroups.Facilities, 150, VisibleByDefault: false, MinZoom: 16),
        new(Schools, "Schools", GisLayerGroups.Infrastructure, 200, VisibleByDefault: false, MinZoom: 12),
        new(CriticalInfrastructure, "Critical infrastructure", GisLayerGroups.Infrastructure, 210, VisibleByDefault: false, MinZoom: 11),
    ];

    public static GisLayerDefinition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}

public static class GisLayerGroups
{
    public const string BaseGeography = "Base geography";
    public const string Facilities = "Emergency facilities";
    public const string Infrastructure = "Population & infrastructure";
}

/// <param name="MinZoom">Web map zoom level below which the layer is not drawn (keeps dense layers readable).</param>
public sealed record GisLayerDefinition(
    string Key,
    string Name,
    string Group,
    int DisplayOrder,
    bool VisibleByDefault,
    int MinZoom);
