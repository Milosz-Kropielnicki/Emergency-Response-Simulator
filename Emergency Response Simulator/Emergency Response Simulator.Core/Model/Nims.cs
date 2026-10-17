using System.Text.RegularExpressions;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 20: NIMS/ICS compliance — terminology, organisation, forms and resource typing ----

public enum NimsArea
{
    /// <summary>Common terminology and plain language on the radio.</summary>
    Terminology,

    /// <summary>Command, modular organisation, span of control, unified command, facilities.</summary>
    Organisation,

    /// <summary>The ICS forms and the Incident Action Plan for each operational period.</summary>
    Forms,

    /// <summary>Every resource described by its NIMS kind and type.</summary>
    ResourceTyping,
}

public enum NimsSeverity
{
    /// <summary>A NIMS requirement not met.</summary>
    Gap,

    /// <summary>Recommended practice not followed; a judgement for the reviewer.</summary>
    Advisory,

    /// <summary>A requirement met: shown so the review says what went right too.</summary>
    Met,
}

/// <param name="Reference">Where NIMS or ICS asks for it, e.g. "NIMS 2017, ICS: span of control".</param>
public sealed record NimsFinding(NimsArea Area, NimsSeverity Severity, string Message, string Reference, Guid? IncidentId = null);

/// <summary>The response checked against NIMS/ICS at one moment, from the COP.</summary>
/// <param name="Score">100 less 10 for each gap and 4 for each advisory, never below 0.</param>
public sealed record NimsReport(DateTimeOffset At, IReadOnlyList<NimsFinding> Findings)
{
    public int Score => Math.Max(0, 100 - 10 * Count(NimsSeverity.Gap) - 4 * Count(NimsSeverity.Advisory));

    public int Count(NimsSeverity severity) => Findings.Count(f => f.Severity == severity);

    public IEnumerable<NimsFinding> In(NimsArea area) => Findings.Where(f => f.Area == area);

    public static NimsReport Empty { get; } = new(default, []);
}

/// <summary>A term the simulator (or Irish practice) uses, and the NIMS term for it.</summary>
public sealed record TermCrosswalk(string Local, string Nims, string Note);

/// <summary>A code or jargon found in a message, and what to say instead.</summary>
public sealed record PlainLanguageIssue(string Found, string Say);

/// <summary>NIMS common terminology: position titles, the crosswalk from local terms, and plain-language checks.</summary>
public static class NimsTerminology
{
    /// <summary>The NIMS title of each Command and General Staff position.</summary>
    public static string Title(IcsRole role) => role switch
    {
        IcsRole.IncidentCommander => "Incident Commander",
        IcsRole.Safety => "Safety Officer",
        IcsRole.Liaison => "Liaison Officer",
        IcsRole.PublicInformation => "Public Information Officer",
        IcsRole.Operations => "Operations Section Chief",
        IcsRole.Planning => "Planning Section Chief",
        IcsRole.Logistics => "Logistics Section Chief",
        _ => "Finance/Administration Section Chief",
    };

    /// <summary>Command Staff report to the IC; General Staff lead the sections.</summary>
    public static bool IsCommandStaff(IcsRole role) => role is IcsRole.Safety or IcsRole.Liaison or IcsRole.PublicInformation;

    /// <summary>
    /// The simulator is set in Ireland, where the Framework for Major Emergency Management and the services' own ranks are
    /// what people say. This is how they line up with NIMS, so an exchange or a review uses the NIMS term.
    /// </summary>
    public static IReadOnlyList<TermCrosswalk> Crosswalk { get; } =
    [
        new("On-Site Co-ordinator", "Unified Command lead / Incident Commander", "The lead agency's controller at the site co-ordinates the services: unified command."),
        new("Controller of Operations", "Incident Commander (single agency)", "Each service's senior officer on site commands its own resources."),
        new("Gold / Strategic", "Agency Administrator / EOC (policy group)", "Strategy and resources, away from the scene."),
        new("Silver / Tactical", "Incident Commander", "Commands the incident."),
        new("Bronze / Operational", "Division or Group Supervisor", "Leads the work in one area or function."),
        new("Local Co-ordination Centre", "Emergency Operations Centre (EOC)", "Multi-agency co-ordination off the site."),
        new("Major emergency declared", "Incident complexity Type 1–2 / EOC activation", "Brings in the multi-agency arrangements."),
        new("Rendezvous point", "Staging Area", "Where arriving resources wait to be assigned."),
        new("Site Control Point", "Incident Command Post (ICP)", "Where command is exercised."),
        new("Inner cordon", "Hot zone / Exclusion zone", "Only those working the hazard, in protective equipment."),
        new("Outer cordon", "Incident perimeter / Cold zone boundary", "Controls who gets near the scene."),
        new("Casualty Clearing Station", "Treatment Area (Medical Group)", "Casualties triaged and treated before transport."),
        new("Rest centre", "Shelter / Congregate care facility", "Where evacuated people go."),
        new("Garda", "Law enforcement", "An Garda Síochána, the Irish police service."),
        new("HSE National Ambulance Service", "EMS", "Emergency medical services."),
        new("Station Officer / Sub-Officer", "Strike Team or Task Force Leader / Company Officer", "Leads a crew or a group of appliances."),
    ];

    private static readonly (Regex Pattern, string Say)[] Codes =
    [
        (new(@"\b10-4\b", RegexOptions.Compiled), "\"understood\""),
        (new(@"\b10-9\b", RegexOptions.Compiled), "\"say again\""),
        (new(@"\b10-20\b", RegexOptions.Compiled), "\"location\" or \"where are you\""),
        (new(@"\b10-(?:33|3)\b", RegexOptions.Compiled), "\"emergency traffic\" or \"stop transmitting\""),
        (new(@"\b10-\d{1,3}\b", RegexOptions.Compiled), "what the code means, in plain words"),
        (new(@"\bcode\s?[1-4]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "\"lights and sirens\" or \"no lights\" in plain words"),
        (new(@"\bsignal\s?\d{1,3}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "what the signal means, in plain words"),
        (new(@"\bQS[LYO]\b", RegexOptions.Compiled), "\"received\" or \"move to\" in plain words"),
        (new(@"\bphase\s?(?:one|two|1|2)\s+response\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "the resources and the task"),
    ];

    /// <summary>
    /// Ten-codes, signal codes and other service-specific codes in a message. NIMS asks for plain language between
    /// agencies, because codes mean different things to different services.
    /// </summary>
    public static IReadOnlyList<PlainLanguageIssue> CheckPlainLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var issues = new List<PlainLanguageIssue>();
        var claimed = new List<(int Start, int End)>();
        foreach (var (pattern, say) in Codes)
            foreach (Match match in pattern.Matches(text))
            {
                if (claimed.Any(c => match.Index < c.End && match.Index + match.Length > c.Start)) continue; // a more specific code matched it
                claimed.Add((match.Index, match.Index + match.Length));
                issues.Add(new(match.Value, say));
            }
        return issues;
    }
}

/// <summary>A resource as NIMS types it (the Resource Typing Library): its discipline, kind and type.</summary>
/// <param name="Type">"Type 1" is the most capable; null where NIMS doesn't grade the kind.</param>
public sealed record NimsResourceType(string Discipline, string Kind, string? Type)
{
    public string Label => Type is null ? Kind : $"{Kind}, {Type}";
}

public static class NimsResourceTypes
{
    /// <summary>The NIMS kind and type of each unit the simulator has.</summary>
    public static NimsResourceType Of(UnitType type) => type switch
    {
        UnitType.Engine => new("Fire/Hazardous Materials", "Engine, Fire", "Type 1"),
        UnitType.Ladder => new("Fire/Hazardous Materials", "Aerial Apparatus, Fire", "Type 1"),
        UnitType.Tanker => new("Fire/Hazardous Materials", "Water Tender, Firefighting (Tanker)", "Type 1"),
        UnitType.WaterTender => new("Fire/Hazardous Materials", "Water Tender, Firefighting (Tanker)", "Type 2"),
        UnitType.Rescue => new("Search and Rescue", "Heavy Rescue Unit", "Type 1"),
        UnitType.Hazmat => new("Fire/Hazardous Materials", "Hazardous Materials Entry Team", "Type 1"),
        UnitType.Command => new("Incident Management", "Mobile Communications Center (Command Unit)", "Type 2"),
        UnitType.Wildland => new("Fire/Hazardous Materials", "Engine, Fire (Wildland)", "Type 3"),
        UnitType.AmbulanceAls => new("Emergency Medical Services", "Ambulance Ground Team (ALS)", "Type 1"),
        UnitType.AmbulanceBls => new("Emergency Medical Services", "Ambulance Ground Team (BLS)", "Type 3"),
        UnitType.MedicalSupervisor => new("Emergency Medical Services", "EMS Task Force Leader", null),
        UnitType.MassCasualtyUnit => new("Emergency Medical Services", "Mass Casualty Support Vehicle", "Type 1"),
        UnitType.MedevacHelicopter => new("Emergency Medical Services", "Air Ambulance (Rotary-Wing)", "Type 1"),
        UnitType.Patrol => new("Law Enforcement", "Law Enforcement Patrol Team", "Type 4"),
        UnitType.Traffic => new("Law Enforcement", "Law Enforcement Patrol Team (Traffic)", "Type 4"),
        UnitType.Swat => new("Law Enforcement", "SWAT/Tactical Team", "Type 1"),
        UnitType.Motorcycle => new("Law Enforcement", "Law Enforcement Patrol Team (Motorcycle)", "Type 4"),
        UnitType.Supervisor => new("Law Enforcement", "Law Enforcement Supervisor", null),
        UnitType.SearchTeam => new("Search and Rescue", "Land Search and Rescue Team", "Type 3"),
        UnitType.PrisonerTransport => new("Law Enforcement", "Prisoner Transport", null),
        UnitType.SearchAndRescue => new("Search and Rescue", "Urban Search and Rescue Task Force", "Type 1"),
        UnitType.Engineering => new("Public Works", "Engineering Services Team", null),
        UnitType.UtilityCrew => new("Public Works", "Utility Restoration Crew", null),
        UnitType.BombDisposal => new("Law Enforcement", "Bomb Squad/Explosives Team", "Type 1"),
        UnitType.Drone => new("Law Enforcement", "Small Unmanned Aircraft System (sUAS) Team", "Type 3"),
        UnitType.Helicopter => new("Law Enforcement", "Helicopter, Aerial Surveillance", "Type 3"),
        UnitType.HeavyMachinery => new("Public Works", "Heavy Equipment (Excavator/Loader)", null),
        UnitType.Bus => new("Mass Care", "Evacuation Transport (Bus)", null),
        _ => new("Other", "Not typed", null),
    };

    public static bool IsTyped(UnitType type) => type != UnitType.Other;
}
