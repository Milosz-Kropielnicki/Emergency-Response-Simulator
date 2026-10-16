using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using static Emergency_Response_Simulator.Simulation.Scenarios.Inject;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// Training scenario: a fire and explosion in a warehouse unit near Barrow Street, with a suspected
/// chemical store next door. Fictional, set on real streets.
/// Information arrives late, imprecise and partly wrong; the trainee must open the incident, decide
/// what to believe, commit resources and react to the wind shift and a unit falling silent.
/// With the world models running (Phase 6) the fire really spreads with the wind: left unchecked it reaches the
/// chlorine store next door (a toxic release that worsens when the drums rupture) and then the substation that
/// feeds the area (power, traffic signals and hospital capacity follow).
/// Communications (Phase 7): a radio black spot under the railway bridge, a mobile mast that runs on batteries
/// once the substation fails, a caller with little English, and a fire crew whose handheld batteries run low.
/// Crews (Phase 8, from the crew model rather than scripted): the warehouse comes down some 20–28 minutes in unless the
/// fire is out. The crew nearest it warns twice first, the test being whether the trainee sounds the evacuation signal.
/// Anyone still inside is caught and calls a Mayday. Engine 4's watch ends about 35 minutes in.
/// </summary>
public static class BarrowStreetScenario
{
    public const string Key = "barrow-street";
    public const string Name = "Barrow Street warehouse fire";

    /// <summary>Where the fire really is. Callers place it slightly wrong.</summary>
    public static readonly GeoPoint FireLocation = new(53.34045, -6.23710);

    /// <summary>
    /// Where a lorry has shed its load on MacMahon Bridge (truth, unreported), drawn across both
    /// carriageways. Crews approaching along Pearse Street run into it and must detour via Grand Canal Street.
    /// </summary>
    public static readonly IReadOnlyList<GeoPoint> ObstructionLine = [new(53.34180, -6.23780), new(53.34280, -6.23780)];

    /// <summary>The unit next door that really does store chlorine-based chemicals (the neighbour is right).</summary>
    public static readonly GeoPoint ChemicalStore = GeoMath.Destination(FireLocation, 45, 70);

    /// <summary>The substation feeding Grand Canal Dock, downwind once the wind backs south-west.</summary>
    public static readonly GeoPoint Substation = GeoMath.Destination(FireLocation, 50, 160);

    /// <summary>Radio black spot under the railway bridge on the approach from the north-west.</summary>
    public static readonly GeoPoint RailwayBridge = GeoMath.Destination(FireLocation, 315, 130);

    /// <summary>The mobile mast serving Grand Canal Dock; it is fed from the substation.</summary>
    public static readonly GeoPoint MobileMast = GeoMath.Destination(FireLocation, 30, 350);

    /// <summary>Phase 17: the gas pressure regulator on the warehouse's street frontage, close enough for the fire to reach.</summary>
    public static readonly GeoPoint GasRegulator = GeoMath.Destination(FireLocation, 200, 40);

    /// <summary>Phase 17: the pumping station that keeps up the docklands' mains pressure, inside the substation's area.</summary>
    public static readonly GeoPoint PumpingStation = GeoMath.Destination(Substation, 90, 300);

    /// <param name="demoAutoResponse">
    /// Presentation mode: at minute 2 the scenario itself opens the incident and dispatches a first response,
    /// so the AVL picture comes alive without a trainee. Off for training, where those decisions are the point.
    /// </param>
    public static ScriptedInjectSystem Create(bool demoAutoResponse = false)
    {
        var fire = Guid.NewGuid();
        var lorry = Guid.NewGuid();
        var secondCall = Guid.NewGuid();
        var demoIncident = Guid.NewGuid();

        List<Inject> demo = !demoAutoResponse ? [] :
        [
            new(TimeSpan.FromMinutes(2), "DEMO: instructor opens the incident and sends a first response",
                context => [
                    Perceived(new IncidentCreated(demoIncident, "INC-00001", IncidentType.Explosion, IncidentPriority.High,
                        FireLocation, "Barrow Street", "Barrow Street warehouse fire")),
                    Perceived(new ReportLinked(secondCall, demoIncident)),
                    .. new[] { "Engine 4", "Engine 12", "Ambulance 14", "Police 21" }
                        .Select(callsign => UnitIdByCallsign(context, callsign))
                        .OfType<Guid>()
                        .Select(unit => Perceived(new UnitDispatched(unit, demoIncident, null))),
                ]),
        ];

        return new ScriptedInjectSystem(Name,
        [
            .. demo,
            new(TimeSpan.FromSeconds(5), "Lorry sheds its load on MacMahon Bridge (truth, unreported)",
                _ => [Truth(new RoadObstructed(lorry, ObstructionLine, "lorry has shed its load across both lanes"))]),

            new(TimeSpan.FromSeconds(5), "Chlorine store and substation near the warehouse (truth: set off only if the fire reaches them)",
                _ => [
                    Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.ChemicalStore, "unit 4 (cleaning chemicals store)",
                        ChemicalStore, "chlorine", Quantity: 1000)),
                    Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.Substation, "Grand Canal Street substation",
                        Substation, ServiceRadiusMeters: 600)),
                ]),

            new(TimeSpan.FromSeconds(5), "Radio black spot and mobile mast (truth)",
                _ => [
                    Truth(new RadioDeadZonePlaced(Guid.NewGuid(), RailwayBridge, 45, "under the railway bridge at Barrow Street")),
                    Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.CellTower, "Grand Canal Dock mobile mast",
                        MobileMast, ServiceRadiusMeters: 900)),
                ]),

            new(TimeSpan.FromSeconds(5), "Gas regulator, docklands pumping station and the DART line over Barrow Street (truth)",
                _ => [
                    Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.GasMain, "Barrow Street gas regulator", GasRegulator, ServiceRadiusMeters: 500)),
                    // One hour of fuel in the standby generator, then the pumps stop.
                    Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.WaterPumping, "Grand Canal Dock pumping station", PumpingStation,
                        Quantity: 1, ServiceRadiusMeters: 900)),
                    Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.Rail, "DART line at Barrow Street", RailwayBridge, ServiceRadiusMeters: 300)),
                ]),

            new(TimeSpan.FromMinutes(3.5), "Third call: a resident with very little English (an interpreter is needed)",
                context => Call(context, new PendingCall
                {
                    Caller = "999 caller (mobile)", Language = "Polish", Mobile = true,
                    Summary = "Fire next to my building, smoke is coming in, my mother cannot walk, third floor, Barrow Street apartments",
                    Location = GeoMath.Destination(FireLocation, 40, 120), AccuracyMeters = 60, Patience = TimeSpan.FromMinutes(3),
                })),

            new(TimeSpan.FromMinutes(9), "A fire crew's handheld batteries run low (truth)",
                context => WorkingFireCrew(context) is { } crew ? [Truth(new RadioBatteryChanged(crew, 0.12))] : [],
                Condition: context => WorkingFireCrew(context) is not null,
                Until: TimeSpan.FromMinutes(30)),

            new(TimeSpan.FromMinutes(1), "Drums rupture in the heat: release rate rises (truth, only once a release has run 4 min)",
                context => ChlorineRelease(context) is { } release
                    ? [Truth(new HazardRateChanged(release.Id, release.Rate * 2.5, "drums rupturing in the heat"))]
                    : [],
                Condition: context => ChlorineRelease(context) is { } release && context.SimTime - release.StartedAt >= TimeSpan.FromMinutes(4),
                Until: TimeSpan.FromMinutes(60)),

            new(TimeSpan.FromSeconds(20), "Fire and explosion start in the warehouse (truth)",
                _ => [Truth(new WorldIncidentStarted(fire, IncidentType.Explosion, FireLocation, Severity: 0.4, ActualCasualties: 6))]),

            new(TimeSpan.FromSeconds(60), "First 999 call: vague location",
                context => Call(context, new PendingCall
                {
                    Caller = "999 caller (mobile)", Summary = "Loud bang and smoke from a warehouse somewhere near Barrow Street",
                    Location = GeoMath.Destination(FireLocation, 300, 120), AccuracyMeters = 150, Patience = TimeSpan.FromMinutes(3),
                })),

            new(TimeSpan.FromSeconds(105), "Second call: better location, people affected",
                context => Call(context, new PendingCall
                {
                    Id = secondCall, Caller = "999 caller (landline)", Mobile = false,
                    Summary = "Fire in the warehouse unit on Barrow Street, people coming out coughing",
                    Location = GeoMath.Destination(FireLocation, 90, 25), AccuracyMeters = 40, Patience = TimeSpan.FromMinutes(3),
                })),

            new(TimeSpan.FromSeconds(150), "Social media rumour (wrong)",
                _ => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Media, "Social media post",
                    "Huge explosion at the tech offices on Grand Canal Dock, dozens hurt!!", Confidence.Low,
                    VerificationStatus.Suspected, new GeoPoint(53.3395, -6.2365), 500))]),

            new(TimeSpan.FromMinutes(4), "Fire grows (truth)",
                _ => [Truth(new WorldIncidentChanged(fire, Severity: 0.6, ActualCasualties: 9, Extinguished: false))]),

            new(TimeSpan.FromMinutes(5), "Passing police patrol confirms a large fire",
                context => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, "Police 14",
                    "Large fire visible from Grand Canal Street, black smoke drifting east", Confidence.High,
                    VerificationStatus.Reported, FireLocation, 30, UnitIdByCallsign(context, "Police 14")))]),

            new(TimeSpan.FromMinutes(5.5), "Wind really backs to the south-west (truth)",
                _ => [Truth(new WeatherChanged(WindFromDegrees: 210, WindSpeedMps: 7, TemperatureC: 13, RelativeHumidity: 0.68))]),

            new(TimeSpan.FromMinutes(6.5), "Met service reports the wind shift",
                _ => [Perceived(new WeatherObserved("Met service", new GeoPoint(53.3498, -6.2603),
                    WindFromDegrees: 210, WindSpeedMps: 7, TemperatureC: 13, RelativeHumidity: 0.68))]),

            new(TimeSpan.FromMinutes(7), "Neighbour warns of chemical storage",
                _ => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Public, "Facility manager, unit 4",
                    "The unit next door stores chlorine-based cleaning chemicals, maybe 40 drums", Confidence.Medium,
                    VerificationStatus.Suspected, GeoMath.Destination(FireLocation, 45, 40), 20))]),

            new(TimeSpan.FromMinutes(8), "Engine 7's radio fails (truth: command only notices the silence)",
                context => UnitIdByCallsign(context, "Engine 7") is { } engine7
                    ? [Truth(new UnitRadioFailed(engine7, Failed: true))]
                    : []),

            new(TimeSpan.FromMinutes(8.5), "Garda inspector asks for authority to evacuate (decision for the trainee)",
                _ => [Perceived(new ApprovalRequested(Guid.NewGuid(), null,
                    "Authority to evacuate Barrow Street apartments",
                    "Approx. 200 residents in the apartment block north-east of the fire; smoke is now drifting over it. " +
                    "Request authority to evacuate to the community centre on Pearse Street.",
                    "Garda Inspector, Pearse Street"))]),

            new(TimeSpan.FromMinutes(10), "St. James's loses two resus bays to a burst pipe (truth: hospital capacity drops)",
                context => context.World.Hospitals.Values.FirstOrDefault(h => h.Name.StartsWith("St. James's")) is { } james
                    ? [Truth(new HospitalCapacityChanged(james.Id, james.Capacity - 3, "burst pipe: two resus bays out of use"))]
                    : []),

            new(TimeSpan.FromMinutes(10), "Hospital reports pressure on the emergency department",
                _ => [Perceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Critical, AlertSeverity.Critical,
                    "St. James's Hospital: ED near capacity",
                    "Emergency department at 95% capacity; can accept 3 more P1 casualties.", null, null))]),

            new(TimeSpan.FromMinutes(6), "First fire engine still driving breaks down (truth)",
                context => TravellingEngine(context) is { } engine
                    ? [Truth(new UnitBrokeDown(engine, "engine overheating, lost power"))]
                    : [],
                Condition: context => TravellingEngine(context) is not null,
                Until: TimeSpan.FromMinutes(15)),

            new(TimeSpan.FromMinutes(25), "Lorry load cleared from MacMahon Bridge (truth)",
                _ => [Truth(new RoadObstructionCleared(lorry))]),

            new(TimeSpan.FromMinutes(18), "Engine 7's radio recovers (truth)",
                context => UnitIdByCallsign(context, "Engine 7") is { } engine7
                    ? [Truth(new UnitRadioFailed(engine7, Failed: false))]
                    : []),

            // ---- Multi-agency (Phase 10): what the other agencies know, which reaches command only if they share it ----

            new(TimeSpan.FromMinutes(6.5), "Garda Roads Policing see people trapped on a balcony (held: information silo)",
                context => Held(context, DemoRosterScenario.Agencies.RoadsPolicing,
                    "Our patrol on Grand Canal Street saw two people on a third-floor balcony of the Barrow Street apartments waving for help. " +
                    "Smoke is getting into the building.", GeoMath.Destination(FireLocation, 40, 120), urgent: true)),

            new(TimeSpan.FromMinutes(7.5), "The Health and Safety Authority's register shows what unit 4 stores (held)",
                context => Held(context, DemoRosterScenario.Agencies.SafetyRegulator,
                    "Our register shows unit 4 next to the warehouse holds up to 1,000 kg of hypochlorite cleaning products. Heat, or water " +
                    "on the drums, gives off chlorine gas.", ChemicalStore, urgent: true)),

            new(TimeSpan.FromMinutes(8), "Waterways Ireland: people living on boats in the basin, downwind (held)",
                context => Held(context, DemoRosterScenario.Agencies.Waterways,
                    "About 15 people live on boats moored in Grand Canal Dock, and the water-sports centre on Hanover Quay has a youth " +
                    "group out on the water.", BasinCentre, urgent: true)),

            new(TimeSpan.FromMinutes(10), "Dublin City Council: the rest centre is smaller than the Garda think (held)",
                context => Held(context, DemoRosterScenario.Agencies.Council,
                    "Pearse Street community centre can take 120 people, not 200. We can open St. Andrew's Resource Centre on Pearse " +
                    "Street as an overflow if asked.", null, urgent: false)),

            new(TimeSpan.FromMinutes(11), "Garda intelligence on the warehouse tenant (held)",
                context => Held(context, DemoRosterScenario.Agencies.Investigators,
                    "Intelligence: the warehouse tenant is under investigation for storing unlicensed chemicals. Expect more drums than " +
                    "declared, and treat the seat of the explosion as a crime scene.", FireLocation, urgent: false)),

            new(TimeSpan.FromMinutes(1), "HSE Public Health hears of chlorine symptoms downwind (held, once a release has run 3 min)",
                context => Held(context, DemoRosterScenario.Agencies.PublicHealth,
                    "GPs in Ringsend are ringing us about people with stinging eyes and coughs. It looks like chlorine is reaching there.",
                    GeoMath.Destination(FireLocation, 30, 700), urgent: true),
                Condition: context => ChlorineRelease(context) is { } release && context.SimTime - release.StartedAt >= TimeSpan.FromMinutes(3),
                Until: TimeSpan.FromMinutes(60)),

            new(TimeSpan.FromMinutes(12), "The Garda investigators claim the lead: a possible crime scene (authority dispute)",
                context => LeadClaim(context),
                Condition: context => OpenIncidentHere(context) is not null && FireBurning(context),
                Until: TimeSpan.FromMinutes(40)),
        ]);
    }

    /// <summary>The middle of Grand Canal Dock basin, north of the fire and downwind once the wind backs.</summary>
    public static readonly GeoPoint BasinCentre = new(53.34310, -6.23590);

    private static IEnumerable<(EventVisibility, DomainEvent)> Held(SimulationContext context, string agency, string claim, GeoPoint? location, bool urgent) =>
        AgencyIdByName(context, agency) is { } id
            ? [Truth(new InformationHeld(Guid.NewGuid(), id, claim, location, urgent))]
            : [];

    private static IEnumerable<(EventVisibility, DomainEvent)> LeadClaim(SimulationContext context)
    {
        if (OpenIncidentHere(context) is not { } incident || AgencyIdByName(context, DemoRosterScenario.Agencies.Investigators) is not { } investigators)
            return [];
        const string agency = DemoRosterScenario.Agencies.Investigators;
        const string position = "The explosion may be criminal. The Garda should take the lead now, and the warehouse be preserved as a crime " +
                                "scene: no overhaul, and no more water on the seat of the explosion than is needed.";
        CommsNet.Chat(context, agency, $"Detective Superintendent, {agency}: {position}", null, TimeSpan.Zero);
        return [Perceived(new DisputeRaised(Guid.NewGuid(), incident, DisputeKind.LeadAgency, investigators, agency,
            "Who leads at the warehouse", position,
            "The Garda Síochána investigate suspected crime; under the major emergency framework the lead can pass to them once the scene is a crime scene."))];
    }

    private static Guid? OpenIncidentHere(SimulationContext context) =>
        context.World.Interagency.Incidents
            .Where(i => !i.Value.Closed && GeoMath.DistanceMeters(i.Value.Location, FireLocation) < 500)
            .OrderBy(i => i.Value.CreatedAt)
            .Select(i => (Guid?)i.Key)
            .FirstOrDefault();

    private static bool FireBurning(SimulationContext context) =>
        context.World.Hazards.Values.Any(h => h.Kind == HazardKind.Fire && !h.Ended);

    private static Guid? AgencyIdByName(SimulationContext context, string name) =>
        context.World.Agencies.Where(a => a.Value.Name == name).Select(a => (Guid?)a.Key).FirstOrDefault();

    /// <summary>Scripted 999 calls ring the line like anyone else's: they can queue for a call-taker.</summary>
    private static IEnumerable<(EventVisibility, DomainEvent)> Call(SimulationContext context, PendingCall call)
    {
        CommsNet.EmergencyCall(context, call);
        return [];
    }

    private static Guid? WorkingFireCrew(SimulationContext context) =>
        context.World.Units.Values
            .Where(u => u.Type == UnitType.Engine && u.Phase == State.ResponsePhase.Operating && u.Battery > 0.2)
            .OrderBy(u => u.Callsign, StringComparer.Ordinal)
            .FirstOrDefault()?.Id;

    private static State.WorldHazard? ChlorineRelease(SimulationContext context) =>
        context.World.Hazards.Values.FirstOrDefault(h => h.Kind == HazardKind.Plume && !h.Ended && h.Rate > 0);

    private static Guid? TravellingEngine(SimulationContext context) =>
        context.World.Units.Values
            .Where(u => u.Type == UnitType.Engine && u.Phase == State.ResponsePhase.Travelling && !u.BrokenDown
                        && !(u.AgencyId is { } agency && context.World.Agencies.TryGetValue(agency, out var a) && a.AiControlled))
            .OrderBy(u => u.Callsign, StringComparer.Ordinal)
            .FirstOrDefault()?.Id;

    private static Guid? UnitIdByCallsign(SimulationContext context, string callsign) =>
        context.World.Units.Values.FirstOrDefault(u => u.Callsign == callsign)?.Id;
}
