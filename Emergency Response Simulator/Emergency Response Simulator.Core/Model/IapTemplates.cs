namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// Starting points for the IAP builder: common objectives, tactics, channels and safety measures.
/// Everything is a suggestion; the planner can type anything.
/// </summary>
public static class IapTemplates
{
    /// <summary>Strategic objectives in the usual priority order (§8.2): life, incident stabilisation, property.</summary>
    public static IReadOnlyList<string> StrategicObjectives { get; } =
    [
        "Protect life",
        "Maintain responder safety",
        "Establish a safe exclusion zone",
        "Evacuate exposed civilians",
        "Establish medical triage capability",
        "Prevent fire spread to adjacent structures",
        "Identify chemical hazards",
        "Protect critical infrastructure",
        "Protect the environment",
        "Keep the public informed",
        "Restore normal traffic and services",
    ];

    private static readonly Dictionary<string, string[]> OperationalByStrategic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Protect life"] =
        [
            "Complete primary search of the affected building",
            "Rescue trapped persons",
            "Evacuate all civilians within the projected hazard area",
            "Triage, treat and transport all casualties",
        ],
        ["Maintain responder safety"] =
        [
            "Account for all personnel inside the inner cordon",
            "Brief all crews on hazards before deployment",
            "Rotate crews before fatigue limits",
        ],
        ["Establish a safe exclusion zone"] =
        [
            "Establish inner and outer cordons",
            "Close roads into the hazard area",
            "Control access at the cordon points",
        ],
        ["Evacuate exposed civilians"] =
        [
            "Evacuate all civilians within the projected hazard area",
            "Open a rest centre for evacuees",
            "Advise shelter-in-place beyond the evacuation zone",
        ],
        ["Establish medical triage capability"] =
        [
            "Set up a casualty clearing station",
            "Triage, treat and transport all casualties",
            "Alert receiving hospitals",
        ],
        ["Prevent fire spread to adjacent structures"] =
        [
            "Protect exposures on all sides",
            "Contain the fire to the building of origin",
            "Establish a sustainable water supply",
        ],
        ["Identify chemical hazards"] =
        [
            "Identify the substances involved",
            "Monitor air quality downwind",
            "Contain run-off",
        ],
        ["Protect critical infrastructure"] =
        [
            "Isolate gas and electricity to the affected area",
            "Protect the rail line and bridges",
        ],
        ["Keep the public informed"] =
        [
            "Issue public warnings for the affected area",
            "Brief the media at set times",
        ],
        ["Restore normal traffic and services"] =
        [
            "Re-open roads once the area is safe",
            "Hand the scene over to the owner",
        ],
    };

    /// <summary>Operational objectives that usually serve a strategic objective.</summary>
    public static IReadOnlyList<string> OperationalFor(string? strategic) =>
        strategic is not null && OperationalByStrategic.TryGetValue(strategic.Trim(), out var list)
            ? list
            : OperationalByStrategic.Values.SelectMany(v => v).Distinct().ToArray();

    /// <summary>Common tactics, used as suggestions for tactical tasks and unit assignments.</summary>
    public static IReadOnlyList<string> Tactics { get; } =
    [
        "Offensive interior attack",
        "Defensive exterior attack",
        "Exposure protection",
        "Primary search",
        "Secondary search",
        "Ventilation",
        "Water supply from hydrants",
        "Aerial monitor",
        "Rescue from height",
        "Establish evacuation perimeter",
        "Door-to-door evacuation",
        "Traffic-control point",
        "Inner cordon control",
        "Outer cordon control",
        "Casualty clearing station",
        "Triage and treatment",
        "Patient transport",
        "Gas monitoring",
        "Decontamination",
        "Run-off containment",
        "Search area sweep",
        "Rapid intervention crew",
        "Staging / standby",
    ];

    /// <summary>The usual radio plan for the agencies on scene (ICS 205), plus a tactical channel per group.</summary>
    public static List<RadioChannel> DefaultChannels(IEnumerable<AgencyType> agencies, IEnumerable<string> groups)
    {
        var present = agencies.ToHashSet();
        var channels = new List<RadioChannel>
        {
            new() { Function = "Command", Channel = "FIRE CMD 1", AssignedTo = "IC, section chiefs, group supervisors" },
        };
        if (present.Contains(AgencyType.Fire))
            channels.Add(new() { Function = "Fireground tactical", Channel = "FIRE TAC 2", AssignedTo = "Fire crews" });
        if (present.Contains(AgencyType.Ems))
            channels.Add(new() { Function = "Medical", Channel = "AMB OPS 1", AssignedTo = "Ambulance crews, receiving hospitals" });
        if (present.Contains(AgencyType.Police))
            channels.Add(new() { Function = "Police", Channel = "GARDA OPS", AssignedTo = "Cordon and traffic units" });
        if (present.Count > 1)
            channels.Add(new() { Function = "Inter-agency command", Channel = "INTER-AGENCY 1", AssignedTo = "Agency commanders",
                Remarks = "Major emergency inter-agency talkgroup" });

        var tactical = 3;
        foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)))
            channels.Add(new() { Function = "Tactical", Channel = $"FIRE TAC {tactical++}", AssignedTo = group });
        return channels;
    }

    public static IReadOnlyList<string> Ppe { get; } =
    [
        "Full structural fire kit and BA in the hot zone",
        "Chemical protective suits for hazmat entry",
        "Hi-vis on all roads",
        "Gas monitors with entry teams",
        "Helmets and gloves inside the inner cordon",
    ];

    /// <summary>A sensible mitigation for a reported threat, if one is known.</summary>
    public static string? MitigationFor(string hazard)
    {
        var text = hazard.ToLowerInvariant();
        if (text.Contains("chemical") || text.Contains("hazmat") || text.Contains("gas") || text.Contains("toxic"))
            return "Approach from upwind; chemical protection and gas monitoring; decontamination at the exit of the hot zone";
        if (text.Contains("smoke"))
            return "BA inside the hot zone; approach from upwind";
        if (text.Contains("collapse") || text.Contains("structural"))
            return "Collapse zone of 1.5 × building height; no entry without an engineer's survey";
        if (text.Contains("fire") || text.Contains("flame"))
            return "BA and full fire kit; maintain a rapid intervention crew";
        if (text.Contains("explos"))
            return "Withdraw to a safe distance; no radios within 15 m of suspect items";
        if (text.Contains("traffic") || text.Contains("road"))
            return "Hi-vis; road closed behind a fend-off vehicle";
        if (text.Contains("water") || text.Contains("flood"))
            return "Lifejackets within 3 m of water; tethered entry only";
        if (text.Contains("electric") || text.Contains("power"))
            return "Treat cables as live until ESB Networks confirm isolation";
        return null;
    }

    /// <summary>Known emergency services at Dublin hospitals, for the medical plan; null when not known.</summary>
    public static string? HospitalCapabilities(string name)
    {
        var text = name.ToLowerInvariant();
        if (!IsReceivingHospital(name)) return null;
        if (text.Contains("james")) return "Emergency department; National Burns Unit";
        if (text.Contains("mater misericordiae") || text.StartsWith("mater ")) return "Emergency department; major trauma centre";
        if (text.Contains("beaumont")) return "Emergency department; neurosurgery";
        if (text.Contains("vincent")) return "Emergency department";
        if (text.Contains("tallaght")) return "Emergency department";
        if (text.Contains("connolly")) return "Emergency department";
        if (text.Contains("temple street") || text.Contains("children") || text.Contains("crumlin")) return "Paediatric emergency department";
        return null;
    }

    /// <summary>
    /// False for hospitals that do not take emergency casualties (maternity, dental, eye and ear, rehabilitation,
    /// hospices), so the medical plan doesn't send ambulances there.
    /// </summary>
    public static bool IsReceivingHospital(string name)
    {
        var text = name.ToLowerInvariant();
        string[] excluded = ["maternity", "rotunda", "holles", "coombe", "dental", "eye and ear", "donnybrook", "hospice",
            "psychiatric", "mental", "clinic", "rehabilitation", "orthodontic"];
        return !excluded.Any(text.Contains);
    }
}
