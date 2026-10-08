namespace Emergency_Response_Simulator.ViewModels;

/// <summary>Keys of the operational (COP-driven) map layer toggles.</summary>
public static class MapLayerKeys
{
    public const string Incidents = "op:incidents";
    public const string FireUnits = "op:units-fire";
    public const string EmsUnits = "op:units-ems";
    public const string PoliceUnits = "op:units-police";
    public const string OtherUnits = "op:units-other";
    public const string Weather = "op:weather";

    public const string HazardZones = "zones:hazard";
    public const string EvacuationZones = "zones:evacuation";
    public const string CommandZones = "zones:command";
    public const string TrafficZones = "zones:traffic";
    public const string SearchZones = "zones:search";
    public const string PerimeterZones = "zones:perimeter";
}
