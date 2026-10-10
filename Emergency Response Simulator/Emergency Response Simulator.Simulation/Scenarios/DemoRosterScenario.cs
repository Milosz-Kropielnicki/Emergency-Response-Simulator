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
        await Register(new AgencyRegistered(northFire, "City Fire Brigade, North District", "Fire N", AgencyType.Fire, AiControlled: true,
            RadioChannel: "FIRE NORTH"));
        await Register(new AgencyRegistered(southEms, "Ambulance Service, Dublin South", "EMS S", AgencyType.Ems, AiControlled: true,
            RadioChannel: "AMB SOUTH"));
        await Register(new AgencyRegistered(trafficCorps, "Garda Roads Policing", "Garda RP", AgencyType.Police, AiControlled: true,
            RadioChannel: "GARDA RP"));
        await AddUnit("Engine 31", UnitType.Engine, northFire, new GeoPoint(53.3585, -6.2560), "Station 11", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 33", UnitType.Engine, northFire, new GeoPoint(53.3585, -6.2560), "Station 11", 4, "Structural");
        await AddUnit("Ambulance 52", UnitType.AmbulanceAls, southEms, new GeoPoint(53.3245, -6.2385), "Donnybrook", 2, "ALS");
        await AddUnit("Ambulance 55", UnitType.AmbulanceBls, southEms, new GeoPoint(53.3245, -6.2385), "Donnybrook", 2, "BLS");
        await AddUnit("Garda RP 41", UnitType.Traffic, trafficCorps, new GeoPoint(53.3462, -6.2515), "Pearse Street", 2);
        await AddUnit("Garda RP 43", UnitType.Motorcycle, trafficCorps, new GeoPoint(53.3372, -6.2460), "Pearse Street", 1);

        // Receiving hospitals: emergency department places and how many are already in use.
        await Register(new HospitalRegistered(Guid.NewGuid(), "St. James's Hospital", new GeoPoint(53.3415, -6.2949), EdCapacity: 40, Occupied: 35));
        await Register(new HospitalRegistered(Guid.NewGuid(), "Mater Misericordiae University Hospital", new GeoPoint(53.3597, -6.2659), EdCapacity: 45, Occupied: 33));
        await Register(new HospitalRegistered(Guid.NewGuid(), "St. Vincent's University Hospital", new GeoPoint(53.3168, -6.2128), EdCapacity: 35, Occupied: 24));
        await Register(new HospitalRegistered(Guid.NewGuid(), "Beaumont Hospital", new GeoPoint(53.3905, -6.2232), EdCapacity: 40, Occupied: 31));

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
