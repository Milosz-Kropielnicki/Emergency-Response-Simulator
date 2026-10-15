using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Crews;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// A small fictional roster around Dublin so the shell has resources to show, the receiving hospitals
/// (real names, illustrative capacities), neighbouring agencies run by the simulation, whose units
/// command can see but not dispatch, and the crew riding every unit. No incidents: those come from scenarios.
/// </summary>
public sealed class DemoRosterScenario : IScenario
{
    public string Name => "Demo roster";

    /// <summary>Agency names scenarios refer to (Phase 10).</summary>
    public static class Agencies
    {
        public const string Investigators = "Garda National Bureau of Criminal Investigation";
        public const string SafetyRegulator = "Health and Safety Authority";
        public const string PublicHealth = "HSE Public Health";
        public const string Waterways = "Waterways Ireland";
        public const string Council = "Dublin City Council";
        public const string RoadsPolicing = "Garda Roads Policing";
    }

    public async Task SeedAsync(IEventPublisher publisher, CancellationToken cancellationToken = default)
    {
        var fire = Guid.NewGuid();
        var ems = Guid.NewGuid();
        var police = Guid.NewGuid();

        await Register(new AgencyRegistered(fire, "City Fire Brigade", "Fire", AgencyType.Fire));
        await Register(new AgencyRegistered(ems, "Ambulance Service", "EMS", AgencyType.Ems));
        await Register(new AgencyRegistered(police, "Metropolitan Police", "Police", AgencyType.Police));

        var station3 = new GeoPoint(53.3498, -6.2603);
        var station8 = new GeoPoint(53.3331, -6.2489);
        var hospital = new GeoPoint(53.3389, -6.2333);

        await AddUnit("Engine 4", UnitType.Engine, fire, station3, "Station 3", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 7", UnitType.Engine, fire, station8, "Station 8", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 12", UnitType.Engine, fire, station3, "Station 3", 5, "Structural", "BreathingApparatus", Qualifications.Swiftwater);
        await AddUnit("Engine 14", UnitType.Engine, fire, station8, "Station 8", 4, "Structural", "BreathingApparatus");
        await AddUnit("Ladder 3", UnitType.Ladder, fire, station8, "Station 8", 4, "AerialLadder", "Rescue");
        await AddUnit("Hazmat 2", UnitType.Hazmat, fire, station3, "Station 3", 6, "Hazmat", "Decontamination");
        await AddUnit("Ambulance 14", UnitType.AmbulanceAls, ems, hospital, "St. Mary's", 2, "ALS");
        await AddUnit("Ambulance 21", UnitType.AmbulanceAls, ems, hospital, "St. Mary's", 2, "ALS");
        await AddUnit("Ambulance 30", UnitType.AmbulanceBls, ems, station8, "Station 8", 2, "BLS");
        await AddUnit("Police 14", UnitType.Patrol, police, new GeoPoint(53.3440, -6.2672), "Central", 2);
        await AddUnit("Police 21", UnitType.Patrol, police, new GeoPoint(53.3520, -6.2440), "North", 2);
        await AddUnit("Traffic 5", UnitType.Traffic, police, new GeoPoint(53.3405, -6.2550), "Central", 1);

        // Neighbouring services with their own control rooms (AI-controlled, Design Document §10.3).
        var northFire = Guid.NewGuid();
        var southEms = Guid.NewGuid();
        var trafficCorps = Guid.NewGuid();
        // Each works its own talkgroup: command hears their crews only through their control rooms (or a patch).
        // Each has its own priorities at a joint incident (Phase 10): the words are what an IAP objective must say to cover them.
        await Register(new AgencyRegistered(northFire, "City Fire Brigade, North District", "Fire N", AgencyType.Fire, AiControlled: true,
            RadioChannel: "FIRE NORTH", Jurisdiction: "Dublin City", Priority: "Keep fire cover for the north city", PriorityTerms: ["cover", "standby"]));
        await Register(new AgencyRegistered(southEms, "Ambulance Service, Dublin South", "EMS S", AgencyType.Ems, AiControlled: true,
            RadioChannel: "AMB SOUTH", Jurisdiction: "Dublin Region", Priority: "Get every casualty to the right hospital",
            PriorityTerms: ["casualt", "patient", "hospital", "triage"]));
        await Register(new AgencyRegistered(trafficCorps, "Garda Roads Policing", "Garda RP", AgencyType.Police, AiControlled: true,
            RadioChannel: "GARDA RP", Jurisdiction: "Dublin Region", Priority: "Keep traffic moving on the city's main routes",
            PriorityTerms: ["traffic", "diversion", "road"]));

        // Agencies with no units on the roster, whose agreement, knowledge or authority command may need (Phase 10).
        var council = Guid.NewGuid();
        var nbci = Guid.NewGuid();
        var hsa = Guid.NewGuid();
        var publicHealth = Guid.NewGuid();
        var waterways = Guid.NewGuid();
        var port = Guid.NewGuid();
        await Register(new AgencyRegistered(council, "Dublin City Council", "DCC", AgencyType.EmergencyManagement, AiControlled: true,
            Jurisdiction: "Dublin City", Priority: "Look after displaced residents and get the area back to normal",
            PriorityTerms: ["resident", "welfare", "rest centre", "evacuat", "shelter"]));
        await Register(new AgencyRegistered(nbci, Agencies.Investigators, "GNBCI", AgencyType.Investigation, AiControlled: true,
            Jurisdiction: "Ireland", Priority: "Preserve the scene and the evidence for the investigation", PriorityTerms: ["scene", "evidence", "investigat"]));
        await Register(new AgencyRegistered(hsa, Agencies.SafetyRegulator, "HSA", AgencyType.Regulator, AiControlled: true,
            Jurisdiction: "Ireland", Priority: "Establish what was stored on the site and keep workers safe",
            PriorityTerms: ["chemical", "hazmat", "decontamin", "site safety"]));
        await Register(new AgencyRegistered(publicHealth, Agencies.PublicHealth, "HSE PH", AgencyType.PublicHealth, AiControlled: true,
            Jurisdiction: "Dublin Region", Priority: "Protect the public from smoke and chemical exposure",
            PriorityTerms: ["public health", "exposure", "smoke", "air quality", "shelter"]));
        await Register(new AgencyRegistered(waterways, Agencies.Waterways, "WI", AgencyType.AreaAuthority, AiControlled: true,
            Jurisdiction: "Grand Canal Dock basin", Priority: "Keep the people on the basin and its moorings safe",
            PriorityTerms: ["basin", "canal", "mooring", "waterway"]));
        await Register(new AgencyRegistered(port, "Dublin Port Company", "DPC", AgencyType.AreaAuthority, AiControlled: true,
            Jurisdiction: "Dublin Port", Priority: "Keep the port working", PriorityTerms: ["port", "shipping", "vessel"]));

        // Jurisdictions: city, county, regional (state) and national (federal) tiers, and two estates whose authorities must
        // agree measures inside them. Boundaries are schematic, not surveyed.
        await Register(new JurisdictionDeclared("ireland", "Ireland", JurisdictionLevel.National,
            "Government (National Emergency Coordination Group)", null,
            [new(55.45, -10.70), new(55.45, -5.95), new(51.35, -5.95), new(51.35, -10.70)],
            Powers: "national coordination, the Defence Forces and national specialist teams"));
        await Register(new JurisdictionDeclared("dublin-region", "Dublin Region", JurisdictionLevel.Regional,
            "Regional Coordination Group (Major Emergency region)", null,
            [new(53.63, -6.55), new(53.63, -6.00), new(53.18, -6.00), new(53.18, -6.55)],
            Powers: "coordinating the principal response agencies across the region's councils"));
        await Register(new JurisdictionDeclared("dublin-city", "Dublin City", JurisdictionLevel.City, "Dublin City Council", council,
            [new(53.405, -6.335), new(53.405, -6.175), new(53.330, -6.175), new(53.305, -6.215), new(53.305, -6.335)],
            Powers: "the city's fire brigade, roads, housing and rest centres"));
        await Register(new JurisdictionDeclared("dlr", "Dún Laoghaire–Rathdown", JurisdictionLevel.County, "Dún Laoghaire–Rathdown County Council", null,
            [new(53.330, -6.175), new(53.305, -6.215), new(53.235, -6.215), new(53.215, -6.100), new(53.300, -6.100)],
            Powers: "the county's roads, housing and rest centres"));
        await Register(new JurisdictionDeclared("kildare", "County Kildare", JurisdictionLevel.County, "Kildare County Council", null,
            [new(53.45, -6.95), new(53.45, -6.46), new(53.27, -6.46), new(53.10, -6.62), new(53.10, -6.95)],
            Powers: "Kildare Fire Service and the county's roads"));
        await Register(new JurisdictionDeclared("grand-canal-dock", "Grand Canal Dock basin", JurisdictionLevel.National, Agencies.Waterways, waterways,
            [new(53.34245, -6.23820), new(53.34378, -6.23820), new(53.34378, -6.23360), new(53.34245, -6.23360)],
            RequiresConsent: true, Powers: "the Grand Canal Dock basin, its quays, moorings and boats"));
        await Register(new JurisdictionDeclared("dublin-port", "Dublin Port", JurisdictionLevel.National, "Dublin Port Company (Harbour Master)", port,
            [new(53.3470, -6.2290), new(53.3580, -6.2290), new(53.3580, -6.1750), new(53.3470, -6.1750)],
            RequiresConsent: true, Powers: "the port estate, berths and vessel movements"));

        // Mutual-aid agreements with the neighbouring services (Phase 10): what each will send, who approves, and the terms.
        await Register(new MutualAidAgreementRegistered(Guid.NewGuid(), "Kildare Fire Service", AgencyType.Fire, "County Kildare",
            [UnitType.Engine, UnitType.Ladder, UnitType.WaterTender, UnitType.Tanker], MaxUnits: 4, "Kildare Chief Fire Officer",
            TimeSpan.FromMinutes(2.5), TimeSpan.FromMinutes(15), NeedsMajorEmergency: false,
            "Firefighting and rescue for High or Critical incidents, under the host incident commander but with crews kept under their own " +
            "officer. Not hazardous-materials entry or decontamination.",
            ["hazmat", "chemical", "decontamination", "decon", "hot zone"], "KILDARE FIRE"));
        await Register(new MutualAidAgreementRegistered(Guid.NewGuid(), "National Ambulance Service (Midlands)", AgencyType.Ems, "Midlands",
            [UnitType.AmbulanceAls, UnitType.AmbulanceBls], MaxUnits: 4, "National Ambulance Service Control",
            TimeSpan.FromMinutes(1.5), TimeSpan.FromMinutes(20), NeedsMajorEmergency: true,
            "Ambulances once a major emergency has been declared; they return to the Midlands as soon as the casualties are cleared.",
            [], "NAS MIDLANDS"));
        await Register(new MutualAidAgreementRegistered(Guid.NewGuid(), "Garda Kildare Division", AgencyType.Police, "County Kildare",
            [UnitType.Patrol, UnitType.Traffic, UnitType.Motorcycle], MaxUnits: 4, "Garda Chief Superintendent, Kildare",
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(12), NeedsMajorEmergency: false,
            "Cordons, traffic control and evacuation assistance for High or Critical incidents.", []));
        await AddUnit("Engine 31", UnitType.Engine, northFire, new GeoPoint(53.3585, -6.2560), "Station 11", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 33", UnitType.Engine, northFire, new GeoPoint(53.3585, -6.2560), "Station 11", 4, "Structural");
        await AddUnit("Ambulance 52", UnitType.AmbulanceAls, southEms, new GeoPoint(53.3245, -6.2385), "Donnybrook", 2, "ALS");
        await AddUnit("Ambulance 55", UnitType.AmbulanceBls, southEms, new GeoPoint(53.3245, -6.2385), "Donnybrook", 2, "BLS");
        await AddUnit("Garda RP 41", UnitType.Traffic, trafficCorps, new GeoPoint(53.3462, -6.2515), "Pearse Street", 2);
        await AddUnit("Garda RP 43", UnitType.Motorcycle, trafficCorps, new GeoPoint(53.3372, -6.2460), "Pearse Street", 1);

        // Receiving hospitals: emergency department places and how many are already in use.
        var jamess = Guid.NewGuid();
        var mater = Guid.NewGuid();
        var vincents = Guid.NewGuid();
        var beaumont = Guid.NewGuid();
        var templeStreet = Guid.NewGuid();
        await Register(new HospitalRegistered(jamess, "St. James's Hospital", new GeoPoint(53.3415, -6.2949), EdCapacity: 40, Occupied: 35));
        await Register(new HospitalRegistered(mater, "Mater Misericordiae University Hospital", new GeoPoint(53.3597, -6.2659), EdCapacity: 45, Occupied: 33));
        await Register(new HospitalRegistered(vincents, "St. Vincent's University Hospital", new GeoPoint(53.3168, -6.2128), EdCapacity: 35, Occupied: 24));
        await Register(new HospitalRegistered(beaumont, "Beaumont Hospital", new GeoPoint(53.3905, -6.2232), EdCapacity: 40, Occupied: 31));
        await Register(new HospitalRegistered(templeStreet, "Children's Health Ireland at Temple Street", new GeoPoint(53.3571, -6.2617), EdCapacity: 20, Occupied: 13));

        // Phase 16: specialist services, blood and mortuary spaces (real services, illustrative numbers). The Mater is the
        // region's major trauma centre, St. James's has the National Burns Unit, children go to Temple Street.
        await Register(new HospitalServicesRegistered(jamess, [new(Specialty.Burns, 8, 5), new(Specialty.Trauma, 4, 3)], BloodUnits: 60, MorgueSpaces: 12));
        await Register(new HospitalServicesRegistered(mater, [new(Specialty.Trauma, 10, 6), new(Specialty.Psychiatric, 6, 4)], BloodUnits: 70, MorgueSpaces: 12));
        await Register(new HospitalServicesRegistered(vincents, [new(Specialty.Psychiatric, 8, 6)], BloodUnits: 40, MorgueSpaces: 10));
        await Register(new HospitalServicesRegistered(beaumont, [new(Specialty.Trauma, 4, 2)], BloodUnits: 50, MorgueSpaces: 10));
        await Register(new HospitalServicesRegistered(templeStreet, [new(Specialty.Paediatric, 15, 9)], BloodUnits: 20, MorgueSpaces: 4, ChildrenOnly: true));

        await publisher.PublishAsync(
            new WeatherChanged(WindFromDegrees: 270, WindSpeedMps: 4.5, TemperatureC: 14, RelativeHumidity: 0.72),
            EventVisibility.Truth, EventSources.Scenario, cancellationToken);

        // What command is told: a met-service observation that happens to match the truth for now.
        await Register(new WeatherObserved("Met service", new GeoPoint(53.3498, -6.2603),
            WindFromDegrees: 270, WindSpeedMps: 4.5, TemperatureC: 14, RelativeHumidity: 0.72));

        Task Register(DomainEvent payload) =>
            publisher.PublishAsync(payload, EventVisibility.Perceived, EventSources.Scenario, cancellationToken);

        // Every unit gets its crew (Phase 8): named people, their qualifications and how far into their shift they are.
        async Task AddUnit(string callsign, UnitType type, Guid agency, GeoPoint at, string station, int crew, params string[] capabilities)
        {
            var id = Guid.NewGuid();
            await Register(new UnitRegistered(id, callsign, type, agency, at, station, crew, capabilities));
            var (onShift, lapsed) = Shifts.TryGetValue(callsign, out var set)
                ? (TimeSpan.FromHours(set.OnShiftHours), set.Lapsed)
                : (CrewRoster.DefaultOnShift(callsign, type), []);
            await Register(new CrewRostered(id, CrewRoster.Generate(callsign, type, crew, capabilities, lapsed), onShift, CrewRoster.ShiftLength(type)));
        }
    }

    /// <summary>
    /// Crews whose place in their shift matters to the exercise, and certificates that have lapsed: Engine 4's day watch
    /// is nearly over, Engine 7 is close to the end of a long shift, Ambulance 14 is in the last hour of its twelve,
    /// Ambulance 21's paramedic registration has lapsed (so it works at basic life support), and one of Hazmat 2's
    /// technicians is out of date. Everyone else is somewhere in the middle of their shift.
    /// </summary>
    private static readonly Dictionary<string, (double OnShiftHours, string[] Lapsed)> Shifts = new()
    {
        ["Engine 4"] = (9.4, []),
        ["Engine 7"] = (9.75, []),
        ["Engine 12"] = (2, []),
        ["Ambulance 14"] = (11.25, []),
        ["Ambulance 21"] = (4, [Qualifications.Als]),
        ["Hazmat 2"] = (5, [Qualifications.Hazmat]),
    };
}
