using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.DependencyInjection;
using static Emergency_Response_Simulator.Tests.AvlRoutingTests;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 6: the simulated world. Hazards, civilians, casualties and hospitals, traffic, cascades, dynamic updates
/// and AI-run agencies, and the separation between what really happens and what command knows.
/// </summary>
public class WorldSimulationTests
{
    private static readonly GeoPoint Dublin = TestHarness.Dublin;

    private static RoutingService Routing(RoadNetwork network)
    {
        var routing = new RoutingService(new TrafficFeed(new FreeFlowTraffic()), new ServiceCollection().BuildServiceProvider());
        routing.Use(network);
        return routing;
    }

    /// <summary>The test grid with every street a two-way main road, so every crossing is a signalised junction.</summary>
    private static RoadNetwork MainRoadGrid() => Grid(features =>
    {
        foreach (var feature in features)
        {
            feature.Properties["highway"] = "primary";
            feature.Properties.Remove("oneway");
        }
    });

    private static async Task<Guid> IncidentAtAsync(TestHarness harness, GeoPoint at)
    {
        var id = Guid.NewGuid();
        await harness.Perceived(new IncidentCreated(id, $"INC-{harness.Cop.Incidents.Count + 1:D5}", IncidentType.StructureFire,
            IncidentPriority.High, at, null, null));
        return id;
    }

    private static async Task<Guid> UnitAtAsync(TestHarness harness, string callsign, UnitType type, GeoPoint at, Guid? agency = null)
    {
        var id = Guid.NewGuid();
        await harness.Perceived(new UnitRegistered(id, callsign, type, agency, at, "Station", 2, []));
        return id;
    }

    // ---- Hazards and what they set off ----

    [Fact]
    public async Task A_real_fire_gets_a_spreading_hazard_whose_footprint_command_never_sees()
    {
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(1.0)));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.4, 0));

        await harness.RunAsync(TimeSpan.FromMinutes(6));

        var hazard = Assert.Single(harness.World.Hazards.Values);
        Assert.Equal(HazardKind.Fire, hazard.Kind);
        Assert.True(hazard.Model!.AreaSquareMeters >= 9 * FireSpreadModel.CellMeters * FireSpreadModel.CellMeters);
        Assert.NotNull(hazard.Footprint);

        var footprints = (await harness.EventsAsync()).Where(e => e.Payload is HazardFootprintChanged).ToList();
        Assert.True(footprints.Count >= 3);
        Assert.All(footprints, e => Assert.Equal(EventVisibility.Truth, e.Visibility));
        Assert.Empty(harness.Cop.Zones);
        Assert.Empty(harness.Cop.Reports);
    }

    [Fact]
    public async Task A_fire_reaching_a_chemical_store_starts_a_toxic_release()
    {
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(1.0)));
        await harness.Truth(new WeatherChanged(270, 6, 14, 0.7));
        await harness.Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.ChemicalStore, "drum store", GeoMath.Destination(Dublin, 90, 30),
            "chlorine", Quantity: 600));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.4, 0));

        Assert.True(await harness.RunUntilAsync(() => harness.World.Hazards.Values.Any(h => h.Kind == HazardKind.Plume),
            TimeSpan.FromMinutes(15)));

        var release = harness.World.Hazards.Values.Single(h => h.Kind == HazardKind.Plume);
        Assert.Equal("chlorine", release.Substance);
        Assert.Equal(600.0 / 2400, release.Rate, 3);
        Assert.Contains(harness.World.Cascades, c => c.Cause == "Fire reached drum store" && c.Effect.StartsWith("Chlorine release"));
        Assert.All((await harness.EventsAsync()).Where(e => e.Payload is CascadeOccurred or HazardStarted),
            e => Assert.Equal(EventVisibility.Truth, e.Visibility));
    }

    [Fact]
    public async Task A_fire_reaching_a_substation_cuts_the_power_and_the_public_and_utility_report_it()
    {
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(1.0)), new InfrastructureSystem());
        await harness.Truth(new WeatherChanged(270, 6, 14, 0.7));
        await harness.Truth(new HazardSitePlaced(Guid.NewGuid(), HazardSiteKind.Substation, "Canal substation", GeoMath.Destination(Dublin, 90, 30),
            ServiceRadiusMeters: 500));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.4, 0));

        Assert.True(await harness.RunUntilAsync(() => harness.World.Outages.Count > 0, TimeSpan.FromMinutes(15)));
        Assert.Contains(harness.World.Cascades, c => c.Effect == "Power out within 500 m");
        Assert.Empty(harness.Cop.Reports); // nobody has said anything yet

        await harness.RunAsync(TimeSpan.FromMinutes(7));
        Assert.Contains(harness.Cop.Reports, r => r.Source == ReportSource.EmergencyCall && r.Claim.Contains("power"));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == InfrastructureSystem.Utility && r.Claim.Contains("Canal substation"));
    }

    [Fact]
    public async Task Power_comes_back_much_sooner_when_command_notifies_the_utility()
    {
        var harness = new TestHarness(new InfrastructureSystem());
        var outage = Guid.NewGuid();
        await harness.Truth(new PowerOutageStarted(outage, Dublin, 400, "cable fault"));
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.True((await harness.C2.NotifyAsync("ESB Networks", "Power out around Barrow Street, please attend")).Succeeded);

        Assert.True(await harness.RunUntilAsync(() => harness.World.Outages.Count == 0, TimeSpan.FromMinutes(40)));
        Assert.True(harness.Engine.SimTime - TestHarness.Start < PowerOutage.DefaultRepairTime);
        Assert.Contains(harness.Cop.Reports, r => r.Claim.StartsWith("Supply restored"));
    }

    [Fact]
    public async Task Floodwater_closes_streets_and_crews_only_find_out_by_driving_into_it()
    {
        var routing = Routing(Grid());
        var source = At(0, 3);
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(elevation: p => GeoMath.DistanceMeters(p, source) * 0.02)),
            new UnitResponseSystem(routing));
        await harness.Truth(new HazardStarted(Guid.NewGuid(), null, HazardKind.Flood, source, "Burst water main", Rate: 6));
        await harness.RunAsync(TimeSpan.FromMinutes(15));
        Assert.NotEmpty(harness.World.Obstructions);
        Assert.Empty(harness.Cop.Reports);

        var engine = await UnitAtAsync(harness, "Engine 12", UnitType.Engine, At(0, 0));
        var incident = await IncidentAtAsync(harness, At(0, 5));
        await harness.C2.DispatchAsync(engine, incident);
        await harness.RunAsync(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(1));

        Assert.Contains(harness.Cop.Reports, r => r.Source == ReportSource.FieldUnit && r.Claim.Contains("flooded"));
        Assert.Contains(await harness.EventsAsync(), e => e.Payload is RouteReported { Reason: { } reason } && reason.StartsWith("Re-routed around obstruction"));
    }

    [Fact]
    public async Task A_rising_release_rate_spreads_the_true_cloud()
    {
        var harness = new TestHarness(new HazardSystem());
        var release = Guid.NewGuid();
        await harness.Truth(new WeatherChanged(270, 3, 14, 0.7));
        await harness.Truth(new HazardStarted(release, null, HazardKind.Plume, Dublin, "Leaking tanker", Rate: 0.3, Substance: "ammonia", Inventory: 10_000));
        await harness.RunAsync(TimeSpan.FromMinutes(10));
        var before = harness.World.Hazards[release].Model!.AreaSquareMeters;

        await harness.Truth(new HazardRateChanged(release, 1.5, "valve fails completely"));
        await harness.RunAsync(TimeSpan.FromMinutes(1));

        Assert.True(harness.World.Hazards[release].Model!.AreaSquareMeters > 2 * before);
    }

    // ---- Civilians ----

    [Fact]
    public async Task People_notice_a_fire_and_flee_or_come_to_look_and_some_ring_999_with_vague_locations()
    {
        var options = new CivilianOptions { PopulationPerIncident = 200, CallProbability = 0.5 };
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(1.0)), new CivilianSystem(new UniformTerrain(1.0), options));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.6, 0));

        await harness.RunAsync(TimeSpan.FromMinutes(12));

        var people = harness.World.Civilians;
        Assert.Equal(200, people.Count);
        Assert.True(people.Count(c => c.State is CivilianState.Evacuating or CivilianState.Safe) >= 20);
        Assert.Contains(people, c => c.State == CivilianState.Converging);

        var calls = harness.Cop.Reports.Where(r => r.Source == ReportSource.EmergencyCall).ToList();
        Assert.InRange(calls.Count, 1, options.MaxCallsPerHazard);
        Assert.All(calls, c => Assert.True(c.LocationAccuracyMeters >= 30));
        Assert.Empty(harness.Cop.Incidents); // calls arrive; deciding there is an incident is still the trainee's job
    }

    [Fact]
    public async Task People_inside_a_declared_evacuation_zone_are_warned_and_most_leave()
    {
        var options = new CivilianOptions { PopulationPerIncident = 150, CarOwnership = 0, LimitedEnglish = 0 }; // language is tested separately
        var harness = new TestHarness(new CivilianSystem(new UniformTerrain(0.2), options));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Other, Dublin, 0.2, 0)); // no hazard: only the order moves people
        await harness.RunAsync(TimeSpan.FromSeconds(5));

        var zone = Wgs84.Circle(Dublin, 250);
        var area = Wgs84.CreatePolygon(zone);
        var inside = harness.World.Civilians.Where(c => area.Contains(c.Location.ToPoint())).Select(c => c.Id).ToHashSet();
        Assert.True(inside.Count > 30);

        await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", zone);
        await harness.RunAsync(TimeSpan.FromMinutes(25));

        var people = harness.World.Civilians;
        var stillInside = people.Count(c => inside.Contains(c.Id) && area.Contains(c.Location.ToPoint()));
        Assert.True(stillInside <= inside.Count * 0.3, $"{stillInside} of {inside.Count} still inside");
        Assert.True(people.Count(c => inside.Contains(c.Id) && c.State == CivilianState.Safe) >= inside.Count * 0.6);
        Assert.All(people.Where(c => !inside.Contains(c.Id)), c => Assert.Equal(CivilianState.Normal, c.State));
    }

    [Fact]
    public async Task Evacuees_cars_are_agents_on_the_roads_and_congest_them()
    {
        var routing = Routing(Grid());
        var live = new LiveTraffic(new FreeFlowTraffic());
        var options = new CivilianOptions { PopulationPerIncident = 300, CarOwnership = 1.0, PopulationRadiusMeters = 300 };
        var harness = new TestHarness(new CivilianSystem(new UniformTerrain(0.2), options, routing), new TrafficSystem(routing, live));
        var centre = At(2, 2);
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Other, centre, 0.2, 0));
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Wgs84.Circle(centre, 350));

        var congested = false;
        for (var i = 0; i < 12 * 60 / 5 && !congested; i++)
        {
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(5));
            congested = live.Snapshot.Factors.Values.Any(f => f < 0.95);
        }
        Assert.True(congested, "evacuation traffic slowed some streets");

        await harness.RunAsync(TimeSpan.FromMinutes(40));
        Assert.Empty(harness.World.Vehicles); // everyone has driven out
        Assert.Contains(harness.World.Civilians, c => c.State == CivilianState.Safe);
    }

    // ---- Casualties, ambulances and hospitals ----

    [Fact]
    public async Task Untreated_casualties_deteriorate_and_die_without_command_being_told()
    {
        var harness = new TestHarness(new MedicalSystem());
        var casualty = Guid.NewGuid();
        await harness.Truth(new CasualtyInjured(casualty, null, Dublin, Triage.Immediate, "trapped"));

        await harness.RunAsync(TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(30));

        Assert.Equal(Triage.Deceased, harness.World.Casualties[casualty].Triage);
        Assert.Contains(await harness.EventsAsync(), e => e.Payload is CasualtyChanged { Triage: Triage.Deceased } && e.Visibility == EventVisibility.Truth);
        Assert.Empty(harness.Cop.Reports);
    }

    [Fact]
    public async Task Ambulance_crews_treat_transport_and_hand_over_and_the_hospital_fills()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MedicalSystem());
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "Mater Hospital", GeoMath.Destination(Dublin, 0, 2000), 30, 10));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.RoadAccident, Dublin, 0.3, ActualCasualties: 2));
        var ambulance = await UnitAtAsync(harness, "Ambulance 14", UnitType.AmbulanceAls, Dublin);
        await harness.C2.DispatchAsync(ambulance, await IncidentAtAsync(harness, Dublin));

        await harness.RunAsync(TimeSpan.FromMinutes(25));

        Assert.Contains(harness.World.Casualties.Values, c => c.State == CasualtyState.AtHospital && c.HospitalId == hospital);
        Assert.True(harness.World.Hospitals[hospital].Delivered >= 1);
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Ambulance 14" && r.Claim.StartsWith("Leaving scene with"));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Ambulance 14" && r.Claim.Contains("casualties at scene"));
        Assert.Contains(await harness.EventsAsync(), e => e.Payload is UnitStatusChanged { Status: UnitStatus.Transporting });
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(ambulance)!.Status);
    }

    [Fact]
    public async Task A_full_hospital_goes_on_diversion_says_so_and_ambulances_go_elsewhere()
    {
        var harness = new TestHarness(cop => [new UnitResponseSystem(), new MedicalSystem(), new AttentionMonitor(cop, new AttentionOptions())]);
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(near, "Near Hospital", GeoMath.Destination(Dublin, 0, 500), 10, 10));
        await harness.Perceived(new HospitalRegistered(far, "Far Hospital", GeoMath.Destination(Dublin, 180, 3000), 20, 5));
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.RoadAccident, Dublin, 0.3, ActualCasualties: 1));
        var ambulance = await UnitAtAsync(harness, "Ambulance 21", UnitType.AmbulanceAls, Dublin);
        await harness.C2.DispatchAsync(ambulance, await IncidentAtAsync(harness, Dublin));

        Assert.True(await harness.RunUntilAsync(() => harness.World.Casualties.Values.Any(c => c.State == CasualtyState.Transporting),
            TimeSpan.FromMinutes(15)));

        Assert.Equal(far, harness.World.Casualties.Values.Single().HospitalId);
        Assert.True(harness.Cop.Hospitals.Single(h => h.Id == near).OnDiversion);
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Near Hospital on diversion");
    }

    [Fact]
    public async Task A_notified_hospital_activates_its_surge_plan()
    {
        var harness = new TestHarness(new MedicalSystem());
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "St. James's Hospital", Dublin, 40, 20));
        await harness.C2.NotifyAsync("St James's ED", "Major incident declared, expect casualties");

        await harness.RunAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(52, harness.World.Hospitals[hospital].Capacity);
        Assert.Contains(await harness.EventsAsync(), e => e.Payload is HospitalCapacityChanged { Capacity: 52 } && e.Visibility == EventVisibility.Truth);
    }

    // ---- Traffic and cascades ----

    [Fact]
    public async Task Cascade_power_failure_darkens_signals_and_delays_ambulances()
    {
        async Task<(TimeSpan Drive, TestHarness Harness)> DriveAsync(bool outage)
        {
            var routing = Routing(MainRoadGrid());
            var live = new LiveTraffic(new FreeFlowTraffic());
            var feed = new TrafficFeed(new FreeFlowTraffic());
            var harness = new TestHarness(new UnitResponseSystem(routing, live), new TrafficSystem(routing, live, feed));
            if (outage)
                await harness.Truth(new PowerOutageStarted(Guid.NewGuid(), GeoMath.Destination(At(2, 2), 45, 140), 800, "substation fault"));
            await harness.RunAsync(TimeSpan.FromSeconds(20));

            var ambulance = await UnitAtAsync(harness, "Ambulance 14", UnitType.AmbulanceAls, At(0, 0));
            await harness.C2.DispatchAsync(ambulance, await IncidentAtAsync(harness, At(5, 5)));
            var start = harness.Engine.SimTime;
            Assert.True(await harness.RunUntilAsync(() => harness.World.Units[ambulance].Phase == ResponsePhase.OnScene,
                TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1)));
            return (harness.Engine.SimTime - start, harness);
        }

        var (normal, _) = await DriveAsync(outage: false);
        var (dark, harness) = await DriveAsync(outage: true);

        Assert.True(dark - normal >= TimeSpan.FromSeconds(60), $"{normal} normally, {dark} with the signals out");
        Assert.Contains(harness.World.Cascades, c => c.Cause.StartsWith("Power outage") && c.Effect.StartsWith("Traffic signals dark at"));
        Assert.Contains(harness.World.Cascades, c => c.Cause.StartsWith("Traffic signals dark") && c.Effect.StartsWith("Ambulance 14 delayed"));
    }

    [Fact]
    public async Task Police_directing_traffic_restore_most_of_the_flow_through_a_dark_junction()
    {
        var network = MainRoadGrid();
        var routing = Routing(network);
        var live = new LiveTraffic(new FreeFlowTraffic());
        var harness = new TestHarness(new UnitResponseSystem(routing), new TrafficSystem(routing, live));
        await harness.Truth(new PowerOutageStarted(Guid.NewGuid(), At(2, 2), 1500, "grid fault"));
        var police = await UnitAtAsync(harness, "Traffic 5", UnitType.Traffic, At(2, 2));
        await harness.C2.DispatchAsync(police, await IncidentAtAsync(harness, At(2, 2)));

        await harness.RunAsync(TimeSpan.FromMinutes(3));

        int Into(GeoPoint junction) => network.EdgesNear(junction, 1).First(e => GeoMath.DistanceMeters(network.NodeLocation(e.To), junction) < 1).Id;
        Assert.Equal(TrafficSystem.DirectedFactor, live.Snapshot.Factor(Into(At(2, 2))), 3);
        Assert.Equal(TrafficSystem.DarkSignalFactor, live.Snapshot.Factor(Into(At(4, 4))), 3);
    }

    [Fact]
    public async Task A_closure_backs_traffic_up_into_gridlock_and_the_feed_sees_it_late()
    {
        var network = MainRoadGrid();
        var routing = Routing(network);
        var live = new LiveTraffic(new FreeFlowTraffic());
        var feed = new TrafficFeed(new FreeFlowTraffic());
        var harness = new TestHarness(new TrafficSystem(routing, live, feed));
        var mid = GeoMath.Destination(At(2, 2), 90, Spacing / 2);
        await harness.C2.DeclareZoneAsync(ZoneType.RoadClosure, "Closed for the fire", [GeoMath.Destination(mid, 180, 25), GeoMath.Destination(mid, 0, 25)]);

        var feeder = network.EdgesNear(At(2, 2), 1).First(e => GeoMath.DistanceMeters(network.NodeLocation(e.To), At(2, 2)) < 1
                                                               && e.Name == "Col 2").Id;
        await harness.RunAsync(TimeSpan.FromSeconds(110));
        Assert.True(feed.Snapshot.At < live.Snapshot.At);              // the feed refreshes every two minutes
        Assert.True(feed.Snapshot.Factor(feeder) > live.Snapshot.Factor(feeder)); // so it shows less of the growing queue

        await harness.RunAsync(TimeSpan.FromMinutes(14));
        Assert.True(live.Snapshot.Factor(feeder) < LiveTraffic.GridlockFactor);
        Assert.Contains(harness.World.Cascades, c => c.Effect == "Gridlock on Col 2" && c.Cause.EndsWith(" blocked"));
    }

    // ---- Dynamic updates ----

    [Fact]
    public async Task The_wind_wanders_and_the_met_service_reports_it_late_and_rounded()
    {
        var harness = new TestHarness(new WeatherSystem());
        await harness.Truth(new WeatherChanged(270, 5, 14, 0.7));

        await harness.RunAsync(TimeSpan.FromMinutes(65), TimeSpan.FromSeconds(30));

        var events = await harness.EventsAsync();
        Assert.True(events.Count(e => e.Payload is WeatherChanged) >= 3);
        var observations = events.Where(e => e.Payload is WeatherObserved).ToList();
        Assert.Equal(2, observations.Count);
        Assert.All(observations, e => Assert.Equal(EventVisibility.Perceived, e.Visibility));
        Assert.Equal(0, harness.Cop.Weather!.WindFromDegrees % 10);
        Assert.InRange(Math.Abs(((harness.World.Weather.WindFromDegrees - 270 + 540) % 360) - 180), 0, WeatherSystem.MaxVeerDegrees);
    }

    // ---- AI-controlled agencies ----

    [Fact]
    public async Task An_AI_agency_runs_its_own_jobs_and_command_cannot_dispatch_its_units()
    {
        var harness = new TestHarness(new AgencyAiSystem(), new UnitResponseSystem());
        var agency = Guid.NewGuid();
        await harness.Perceived(new AgencyRegistered(agency, "North District", "N", AgencyType.Fire, AiControlled: true));
        var engine = await UnitAtAsync(harness, "Engine 31", UnitType.Engine, Dublin, agency);

        var refused = await harness.C2.DispatchAsync(engine, await IncidentAtAsync(harness, Dublin));
        Assert.False(refused.Succeeded);
        Assert.Contains("not under your command", refused.Error);

        await harness.RunAsync(TimeSpan.FromMinutes(150), TimeSpan.FromSeconds(10));

        var events = await harness.EventsAsync();
        var firstTask = events.First(e => e.Payload is UnitTasked { UnitId: var u } && u == engine);
        Assert.Equal(EventVisibility.Perceived, firstTask.Visibility);
        Assert.Contains(events, e => e.Sequence > firstTask.Sequence && e.Payload is UnitStatusChanged { Status: UnitStatus.Operating } s && s.UnitId == engine);
        Assert.Contains(events, e => e.Sequence > firstTask.Sequence && e.Payload is UnitStatusChanged { Status: UnitStatus.Available } s && s.UnitId == engine);
    }

    [Fact]
    public async Task AI_police_set_up_a_cordon_at_a_major_incident_on_their_own_and_tell_command()
    {
        var harness = new TestHarness(new AgencyAiSystem(), new UnitResponseSystem());
        var agency = Guid.NewGuid();
        await harness.Perceived(new AgencyRegistered(agency, "Garda Roads Policing", "RP", AgencyType.Police, AiControlled: true));
        var car = await UnitAtAsync(harness, "Garda RP 41", UnitType.Traffic, GeoMath.Destination(Dublin, 0, 1500), agency);
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Explosion, Dublin, 0.5, 0));

        await harness.RunAsync(TimeSpan.FromMinutes(8));

        Assert.Contains(await harness.EventsAsync(), e => e.Payload is UnitTasked { Task: var task } t && t.UnitId == car && task.Contains("cordon"));
        Assert.Contains(harness.Cop.Reports, r => r.Source == ReportSource.Agency && r.SourceName == "Garda Roads Policing" && r.Claim.Contains("cordon"));
        Assert.Contains("Garda Roads Policing", harness.Cop.FindUnit(car)!.Tasking);
    }

    [Fact]
    public async Task An_AI_ambulance_service_sends_crews_when_casualties_are_left_waiting()
    {
        var harness = new TestHarness(new MedicalSystem(), new AgencyAiSystem());
        var agency = Guid.NewGuid();
        await harness.Perceived(new AgencyRegistered(agency, "Ambulance Service, Dublin South", "S", AgencyType.Ems, AiControlled: true));
        // Two crews: the service's own routine calls may already have taken one.
        await UnitAtAsync(harness, "Ambulance 52", UnitType.AmbulanceAls, GeoMath.Destination(Dublin, 180, 2000), agency);
        await UnitAtAsync(harness, "Ambulance 55", UnitType.AmbulanceBls, GeoMath.Destination(Dublin, 180, 2500), agency);
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Explosion, Dublin, 0.5, ActualCasualties: 8));

        await harness.RunAsync(TimeSpan.FromMinutes(10));

        Assert.Contains(await harness.EventsAsync(), e => e.Payload is UnitTasked t && t.Location == Dublin && t.Task.StartsWith("Casualties"));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Ambulance Service, Dublin South" && r.Claim.Contains("casualties"));
    }

    // ---- From truth to the COP: what crews report ----

    [Fact]
    public async Task The_first_fire_crew_sends_a_size_up_with_an_estimate_of_the_fire()
    {
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(1.0)), new UnitResponseSystem(), new HazardReportingSystem());
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.5, 0));
        var engine = await UnitAtAsync(harness, "Engine 4", UnitType.Engine, Dublin);
        await harness.C2.DispatchAsync(engine, await IncidentAtAsync(harness, Dublin));

        await harness.RunAsync(TimeSpan.FromMinutes(4));

        var sizeUp = Assert.Single(harness.Cop.Reports, r => r.SourceName == "Engine 4" && r.Claim.Contains("size-up"));
        Assert.Contains("m² involved", sizeUp.Claim);
        Assert.Equal(VerificationStatus.Reported, sizeUp.Verification); // an estimate, not a measurement
    }

    [Fact]
    public async Task A_crew_sent_to_the_wrong_place_says_so_and_which_way_the_smoke_is()
    {
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(1.0)), new UnitResponseSystem(), new HazardReportingSystem());
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.5, 0));
        var wrong = GeoMath.Destination(Dublin, 270, 600); // the caller's guess was 600 m out
        var engine = await UnitAtAsync(harness, "Engine 12", UnitType.Engine, wrong);
        await harness.C2.DispatchAsync(engine, await IncidentAtAsync(harness, wrong));

        await harness.RunAsync(TimeSpan.FromMinutes(2));

        var report = Assert.Single(harness.Cop.Reports, r => r.SourceName == "Engine 12");
        Assert.Contains("nothing showing here", report.Claim);
        Assert.Contains("smoke visible to the E", report.Claim);
    }

    [Fact]
    public async Task Crews_inside_the_toxic_cloud_report_what_they_smell()
    {
        var harness = new TestHarness(new HazardSystem(), new HazardReportingSystem());
        await harness.Truth(new WeatherChanged(270, 3, 14, 0.7));
        await UnitAtAsync(harness, "Police 21", UnitType.Patrol, GeoMath.Destination(Dublin, 90, 600));
        await harness.Truth(new HazardStarted(Guid.NewGuid(), null, HazardKind.Plume, Dublin, "Chlorine leak", Rate: 0.5, Substance: "chlorine", Inventory: 5000));

        await harness.RunAsync(TimeSpan.FromMinutes(6));

        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Police 21" && (r.Claim.Contains("bleach") || r.Claim.Contains("stinging")));
    }

    // ---- Truth and perception, end to end ----

    private static readonly HashSet<Type> TruthOnly =
    [
        typeof(WorldIncidentStarted), typeof(WorldIncidentChanged), typeof(WeatherChanged), typeof(UnitRadioFailed),
        typeof(RoadObstructed), typeof(RoadObstructionCleared), typeof(UnitBrokeDown), typeof(HazardStarted),
        typeof(HazardFootprintChanged), typeof(HazardRateChanged), typeof(HazardEnded), typeof(HazardSitePlaced),
        typeof(CasualtyInjured), typeof(CasualtyChanged), typeof(HospitalCapacityChanged), typeof(PowerOutageStarted),
        typeof(PowerRestored), typeof(CascadeOccurred), typeof(TransmissionLost), typeof(RadioDeadZonePlaced), typeof(CellTowerFailed),
        typeof(CellTowerRestored), typeof(RadioBatteryChanged), typeof(CrewWelfareChanged), typeof(FirefighterInDistress), typeof(DistressEnded),
        typeof(StructureCollapsed), typeof(HandoverInformationLost),
    ];

    [Fact]
    public async Task With_the_whole_world_running_and_nobody_in_command_the_COP_holds_only_what_has_been_reported()
    {
        var terrain = new UniformTerrain();
        var harness = new TestHarness(cop =>
        [
            BarrowStreetScenario.Create(), new WeatherSystem(), new HazardSystem(terrain), new InfrastructureSystem(),
            new UnitResponseSystem(), new MedicalSystem(), new CivilianSystem(terrain), new AgencyAiSystem(),
            new CommandResponseSystem(), new HazardReportingSystem(), new CrewSystem(), new CommsSystem(), new AttentionMonitor(cop, new AttentionOptions()),
        ]);
        await new DemoRosterScenario().SeedAsync(harness.Engine);

        // Engine 7's radio is dead from minute 8 to 18: the COP still shows it in contact (it is not committed).
        await harness.RunAsync(TimeSpan.FromMinutes(12));
        Assert.Contains(TruthComparison.Compare(harness.Engine.Snapshot, harness.Cop),
            g => g.Topic == "Engine 7" && g.Truth.StartsWith("Radio dead") && g.Severity == DivergenceSeverity.Unknown);

        await harness.RunAsync(TimeSpan.FromMinutes(8));

        // The world moved on: fire, casualties, people fleeing.
        Assert.Contains(harness.World.Hazards.Values, h => h.Kind == HazardKind.Fire);
        Assert.True(harness.World.Casualties.Count >= 6);
        Assert.Contains(harness.World.Civilians, c => c.State != CivilianState.Normal);

        // Command only has what reached it: calls and reports, but no incident (that is the trainee's call).
        Assert.Empty(harness.Cop.Incidents);
        Assert.Empty(harness.Cop.Zones);
        Assert.NotEmpty(harness.Cop.Reports);

        // Truth-only facts never travel as perceived events.
        var events = await harness.EventsAsync();
        Assert.All(events.Where(e => TruthOnly.Contains(e.Payload.GetType())), e => Assert.Equal(EventVisibility.Truth, e.Visibility));

        // The instructor's view shows the gap.
        var snapshot = harness.Engine.Snapshot;
        Assert.Equal(harness.Engine.SimTime, snapshot.At);
        Assert.NotEmpty(snapshot.Hazards);
        Assert.NotEmpty(snapshot.Civilians);
        var gaps = TruthComparison.Compare(snapshot, harness.Cop);
        Assert.Contains(gaps, g => g.Topic == "Explosion" && g.Cop == "Not on the COP" && g.Severity == DivergenceSeverity.Unknown);
        Assert.Contains(gaps, g => g.Topic == "Explosion: casualties" && g.Cop == "—");
    }
}
