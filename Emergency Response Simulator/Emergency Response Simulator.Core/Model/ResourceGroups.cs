namespace Emergency_Response_Simulator.Core.Model;

/// <summary>How units are grouped on the resource board and for shortage alerts.</summary>
public static class ResourceGroups
{
    public static string For(UnitType type) => type switch
    {
        UnitType.Engine => "Fire engines",
        UnitType.Ladder => "Ladder trucks",
        UnitType.Hazmat => "Hazmat teams",
        UnitType.AmbulanceAls => "ALS ambulances",
        UnitType.AmbulanceBls => "BLS ambulances",
        UnitType.Patrol or UnitType.Traffic or UnitType.Motorcycle or UnitType.Supervisor => "Police units",
        UnitType.Swat => "Tactical units",
        _ => type.ToString(),
    };

    /// <summary>Which kind of agency runs a unit type.</summary>
    public static AgencyType AgencyFor(UnitType type) => type switch
    {
        UnitType.Engine or UnitType.Ladder or UnitType.Tanker or UnitType.Rescue or UnitType.Hazmat or UnitType.Command
            or UnitType.Wildland or UnitType.WaterTender => AgencyType.Fire,
        UnitType.AmbulanceAls or UnitType.AmbulanceBls or UnitType.MedicalSupervisor or UnitType.MassCasualtyUnit
            or UnitType.MedevacHelicopter => AgencyType.Ems,
        UnitType.Patrol or UnitType.Traffic or UnitType.Swat or UnitType.Motorcycle or UnitType.Supervisor
            or UnitType.SearchTeam or UnitType.PrisonerTransport => AgencyType.Police,
        _ => AgencyType.EmergencyManagement,
    };

    /// <summary>Display name for a unit type, e.g. AmbulanceAls → "ALS ambulance".</summary>
    public static string Label(UnitType type) => type switch
    {
        UnitType.AmbulanceAls => "ALS ambulance",
        UnitType.AmbulanceBls => "BLS ambulance",
        UnitType.Swat => "SWAT",
        UnitType.MassCasualtyUnit => "MCI unit",
        _ => Events.EventDescriber.Humanize(type),
    };
}
