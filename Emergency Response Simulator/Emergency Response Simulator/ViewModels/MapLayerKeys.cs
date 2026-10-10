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

    // Instructor only: ground truth the trainee never sees (Design Document §10.1, §21).
    public const string TruthHazards = "truth:hazards";
    public const string TruthPeople = "truth:people";
    public const string TruthUnits = "truth:units";
    public const string TruthTraffic = "truth:traffic";
}
