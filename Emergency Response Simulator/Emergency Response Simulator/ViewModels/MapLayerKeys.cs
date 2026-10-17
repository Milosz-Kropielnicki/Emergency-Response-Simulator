namespace Emergency_Response_Simulator.ViewModels;

/// <summary>Keys of the operational (COP-driven) map layer toggles.</summary>
public static class MapLayerKeys
{
    public const string Incidents = "op:incidents";
    public const string Reports = "op:reports";
    public const string FireUnits = "op:units-fire";
    public const string EmsUnits = "op:units-ems";
    public const string PoliceUnits = "op:units-police";
    public const string OtherUnits = "op:units-other";
    public const string Weather = "op:weather";
    public const string Routes = "op:routes";
    public const string Trails = "op:trails";
    public const string Hospitals = "op:hospitals";

    public const string HazardZones = "zones:hazard";
    public const string EvacuationZones = "zones:evacuation";
    public const string CommandZones = "zones:command";
    public const string TrafficZones = "zones:traffic";
    public const string SearchZones = "zones:search";
    public const string PerimeterZones = "zones:perimeter";

    // Multi-agency (Phase 10): city, county, regional and national areas, and estates needing their authority's consent.
    public const string Jurisdictions = "zones:jurisdictions";

    // Plume & hazard models (Phase 13): command's projections, predictions rather than declared zones.
    public const string PlumeProjections = "zones:plume";

    // Population protection and critical systems (Phase 17), as command knows them.
    public const string Shelters = "protect:shelters";
    public const string EvacuationRoutes = "protect:routes";
    public const string Infrastructure = "protect:infrastructure";
    public const string Gnss = "protect:gnss";
    public const string Environment = "protect:environment";

    // Instructor only: ground truth the trainee never sees (Design Document §10.1, §21).
    public const string TruthHazards = "truth:hazards";
    public const string TruthPeople = "truth:people";
    public const string TruthUnits = "truth:units";
    public const string TruthTraffic = "truth:traffic";

    /// <summary>Every key, for checking workspace definitions (Phase 21).</summary>
    public static IReadOnlySet<string> All { get; } = typeof(MapLayerKeys)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
}
