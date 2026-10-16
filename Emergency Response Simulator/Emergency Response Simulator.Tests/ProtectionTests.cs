using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.DependencyInjection;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 17: evacuation, shelter and infrastructure. Evacuation zones counted and reported, routes, buses, traffic
/// control points and re-entry; rest centres, registration, supplies and security; infrastructure that depends on power,
/// gas leaks and rail lines; GPS jamming and spoofing and ransomware; run-off, wildlife and environmental monitoring.
/// </summary>
public class ProtectionTests
{
    private static readonly IReadOnlyList<GeoPoint> Zone = Wgs84.Circle(Dublin, 250);
    private static readonly Guid Neighbourhood = new("4f6c2d9e-1b7a-4c35-9e0d-2a8b5f13c7e4");

    /// <summary>Ordinary people only, nobody with a car (vehicles need the traffic system).</summary>
    private static CivilianOptions People(double elderly = 0, double compliance = 1.0) => new()
    {
        PopulationPerIncident = 200, CarOwnership = 0, LimitedEnglish = 0, EvacuationCompliance = compliance,
        ElderlyShare = elderly, MobilityImpairedShare = 0, HomelessShare = 0, PetOwnerShare = 0,
    };

    private static async Task<TestHarness> PopulatedAsync(CivilianOptions options, params ISimulationSystem[] more)
    {
        var harness = new TestHarness([new CivilianSystem(new UniformTerrain(0.2), options), new EvacuationSystem(), .. more]);
        await harness.Truth(new WorldIncidentStarted(Neighbourhood, IncidentType.Other, Dublin, 0.2, 0)); // no hazard: only orders move people
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        return harness;
    }

    private static async Task<(Guid Incident, Guid Zone)> EvacuateAsync(TestHarness harness)
    {
        var incident = await harness.CreateIncidentAsync();
        var result = await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Barrow Street evacuation", Zone, incident);
        Assert.True(result.Succeeded, result.Error);
        return (incident, harness.Cop.Zones.Single(z => z.Type == ZoneType.EvacuationZone).Id);
    }

    private static async Task<List<T>> PayloadsAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Select(e => e.Payload).OfType<T>().ToList();

    private static async Task<List<SimEvent>> PerceivedAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Where(e => e.Visibility == EventVisibility.Perceived && e.Payload is T).ToList();

    private static bool OnDesk(TestHarness harness, UserRole role, string text) =>
        RoleDesk.For(role, harness.Cop, _ => true).Any(d => d.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

    // ---- Evacuation ----

    [Fact]
    public async Task An_evacuation_is_counted_and_reported_and_the_review_knows_how_many_got_out()
    {
        var harness = await PopulatedAsync(People());
        var (_, zone) = await EvacuateAsync(harness);
        Assert.True(OnDesk(harness, UserRole.Operations, "no evacuation route"));

        await harness.RunAsync(TimeSpan.FromMinutes(20));
        var evacuation = harness.World.Protection.Evacuations[zone];
        Assert.True(evacuation.Population.Count > 50);
        Assert.NotNull(evacuation.ClearedAt);

        // The gardaí's figures: an estimate, close to the truth.
        var reported = harness.Cop.Protection.Evacuation(zone)!;
        Assert.NotNull(reported.ReportedAt);
        Assert.InRange(reported.Estimated, evacuation.Population.Count * 0.8, evacuation.Population.Count * 1.2);
        Assert.True(reported.Left > evacuation.Population.Count / 2);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal(1, report.Protection.Zones);
        Assert.Equal(evacuation.Population.Count, report.Protection.Population);
        Assert.True(report.Protection.OutPercent >= 85);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("no evacuation route was set"));
    }

    [Fact]
    public async Task With_a_route_evacuees_make_for_its_end_and_a_route_must_lead_out()
    {
        var harness = await PopulatedAsync(People());
        var (_, zone) = await EvacuateAsync(harness);
        var end = GeoMath.Destination(Dublin, 270, 700);

        Assert.Contains("inside the zone", (await harness.C2.SetEvacuationRouteAsync(zone, [Dublin, GeoMath.Destination(Dublin, 270, 100)])).Error);
        var set = await harness.C2.SetEvacuationRouteAsync(zone, [Dublin, end]);
        Assert.True(set.Succeeded, set.Error);
        Assert.False(OnDesk(harness, UserRole.Operations, "no evacuation route"));

        await harness.RunAsync(TimeSpan.FromMinutes(25));
        var out_ = harness.World.Civilians.Where(c => c.EvacuatedFrom == zone && c.State == CivilianState.Safe).ToList();
        Assert.True(out_.Count > 50);
        Assert.All(out_, c => Assert.True(GeoMath.DistanceMeters(c.Location, end) < 5, "everyone went to the route's end"));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Single(report.Decisions, d => d.Category == "Evacuation");
        Assert.DoesNotContain(report.Findings, f => f.Text.Contains("no evacuation route was set"));
    }

    [Fact]
    public async Task A_route_through_a_hazard_area_is_set_but_flagged()
    {
        var harness = new TestHarness(new EvacuationSystem());
        var (_, zone) = await EvacuateAsync(harness);
        var hot = GeoMath.Destination(Dublin, 90, 500);
        Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.HotZone, "Chemical store hot zone", Wgs84.Circle(hot, 100))).Succeeded);

        var set = await harness.C2.SetEvacuationRouteAsync(zone, [Dublin, GeoMath.Destination(Dublin, 90, 800)]);
        Assert.True(set.Succeeded, set.Error);
        Assert.Contains("crosses Chemical store hot zone", set.Detail);
    }

    [Fact]
    public async Task Buses_carry_people_on_foot_to_the_rest_centre_at_the_end_of_the_route()
    {
        var routing = new RoutingService(new FreeFlowTraffic(), new ServiceCollection().BuildServiceProvider());
        var harness = await PopulatedAsync(People(), new ShelterSystem(), new TrafficSystem(routing, new LiveTraffic()));
        var (incident, zone) = await EvacuateAsync(harness);
        var centre = GeoMath.Destination(Dublin, 270, 3000); // a long walk
        var opened = await harness.C2.OpenShelterAsync("Ringsend College", centre, 300, incident);
        Assert.True(opened.Succeeded, opened.Error);
        Assert.True((await harness.C2.SetEvacuationRouteAsync(zone, [Dublin], opened.EntityId)).Succeeded); // the route ends at the centre
        Assert.True((await harness.C2.RequestEvacuationBusesAsync(zone, 2)).Succeeded);
        Assert.Contains("already coming", (await harness.C2.RequestEvacuationBusesAsync(zone, 19)).Error);

        // People are still walking out when the buses come.
        await harness.RunAsync(EvacuationSystem.BusLeadTime + TimeSpan.FromSeconds(10));
        Assert.Equal(2, harness.Cop.Protection.Evacuation(zone)!.BusesWorking);
        var carried = harness.World.Protection.Buses.Single().Carried;
        Assert.True(carried > 0, "the buses took the people still walking");

        await harness.RunAsync(TimeSpan.FromMinutes(30));
        var shelter = harness.World.Protection.Shelters[opened.EntityId!.Value];
        Assert.True(shelter.Arrivals.Count >= carried, $"{shelter.Arrivals.Count} at the centre, {carried} carried");
    }

    [Fact]
    public async Task Re_entry_sends_people_home_and_going_home_to_contaminated_ground_is_recorded()
    {
        var harness = await PopulatedAsync(People(), new ShelterSystem());
        var (_, zone) = await EvacuateAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(20));
        var evacuees = harness.World.Civilians.Where(c => c.EvacuatedFrom == zone).ToList();
        Assert.True(evacuees.Count > 50);

        // The ground under the zone is contaminated, and nobody has sampled it.
        harness.World.Protection.Pollutions[Guid.Empty] = new WorldProtection.Pollution { Watercourse = "Grand Canal", WatercoursePoint = Dublin };
        harness.World.Protection.Pollutions[Guid.Empty].Ground.Add((Dublin, 400));
        var reEntry = await harness.C2.AuthoriseReEntryAsync(zone);
        Assert.True(reEntry.Succeeded, reEntry.Error);
        Assert.Contains("nobody has sampled", reEntry.Detail);
        Assert.DoesNotContain(harness.Cop.Zones, z => z.Id == zone);

        await harness.RunAsync(TimeSpan.FromMinutes(105));
        Assert.All(evacuees, c => Assert.True(c.Location == c.Home && c.ShelterId is null, $"person {c.Id} is not home"));
        var unsafeHome = (await PayloadsAsync<UnsafeReEntry>(harness)).Last();
        Assert.Equal(evacuees.Count, unsafeHome.Count);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Residents sent home to danger");
        Assert.Equal(evacuees.Count, report.Protection.UnsafeReEntries);
    }

    // ---- Rest centres ----

    [Fact]
    public async Task Evacuees_with_nowhere_to_go_are_reported_until_a_rest_centre_opens_then_go_there_and_are_registered()
    {
        var harness = await PopulatedAsync(People(elderly: 0.5), new ShelterSystem());
        var (incident, _) = await EvacuateAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(25));
        Assert.True(harness.Cop.Protection.Unsheltered.GetValueOrDefault(incident) >= 10, $"{harness.Cop.Protection.Unsheltered.GetValueOrDefault(incident)} reported");
        Assert.True(OnDesk(harness, UserRole.Logistics, "nowhere to go"));

        Assert.Contains("inside", (await harness.C2.OpenShelterAsync("In the zone", Dublin, 100, incident)).Error);
        var opened = await harness.C2.OpenShelterAsync("Ringsend College", GeoMath.Destination(Dublin, 270, 1200), 300, incident);
        Assert.True(opened.Succeeded, opened.Error);

        await harness.RunAsync(Protection.ShelterSetup + TimeSpan.FromMinutes(30));
        var shelter = harness.Cop.Protection.Shelters.Single();
        Assert.True(shelter.Ready);
        Assert.True(shelter.Occupants >= 10, $"{shelter.Occupants} there");
        Assert.True(shelter.Registered > 0);
        Assert.Equal(0, harness.Cop.Protection.Unsheltered.GetValueOrDefault(incident));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.True(report.Protection.ShelterArrivals >= 10);
        Assert.True(report.Protection.UnshelteredPeak >= 10);
        Assert.Single(report.Decisions, d => d.Category == "Shelter");
    }

    /// <summary>A rest centre with people in it who are not otherwise simulated (only the centre's own system runs).</summary>
    private static async Task<(TestHarness Harness, Guid Incident, Guid Shelter)> FullCentreAsync(Guid shelterId, int people, int capacity)
    {
        var harness = new TestHarness(new ShelterSystem());
        var incident = await harness.CreateIncidentAsync();
        await harness.Engine.PublishAsync(new ShelterOpened(shelterId, "Ringsend College", GeoMath.Destination(Dublin, 270, 1500), capacity, incident),
            EventVisibility.Perceived, EventSources.C2);
        harness.World.Protection.Shelters[shelterId].Occupants.UnionWith(Enumerable.Range(10_000, people));
        return (harness, incident, shelterId);
    }

    [Fact]
    public async Task A_rest_centre_draws_meals_water_and_bedding_from_the_incident_stock_and_says_what_runs_out()
    {
        var (harness, incident, shelter) = await FullCentreAsync(new Guid("6a1b6f1c-2d6e-4b7f-9a0e-3c5d8e7f1a20"), 80, 200);
        harness.World.Logistics.Active = true;
        await harness.RunAsync(Protection.ShelterSetup + TimeSpan.FromMinutes(1));

        var reported = harness.Cop.Protection.Shelters.Single();
        Assert.Equal([SupplyKind.Food, SupplyKind.Water, SupplyKind.Bedding], reported.Short.Order());
        Assert.True(OnDesk(harness, UserRole.Logistics, "Ringsend College has run out of"));

        harness.World.Logistics.Add(incident, SupplyKind.Bedding, 100);
        harness.World.Logistics.Add(incident, SupplyKind.Food, 100);
        harness.World.Logistics.Add(incident, SupplyKind.Water, 100);
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(harness.Cop.Protection.Shelters.Single().Short);
        Assert.Equal(80, harness.World.Protection.Shelters[shelter].Bedded.Count);
        Assert.Equal(20, harness.World.Logistics.Stock(incident, SupplyKind.Bedding));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("ran out of"));
    }

    [Fact]
    public async Task Trouble_in_an_overcrowded_rest_centre_is_mostly_stopped_by_security()
    {
        async Task<(int Unprevented, int Prevented, TestHarness Harness)> Run(bool security)
        {
            var (harness, _, shelter) = await FullCentreAsync(new Guid("0d3f9c51-7e2a-4a8b-b6c4-5f1e2d3a4b6c"), 200, 120);
            if (security) Assert.True((await harness.C2.ArrangeShelterSecurityAsync(shelter)).Succeeded);
            await harness.RunAsync(TimeSpan.FromHours(4), TimeSpan.FromMinutes(1));
            var trouble = await PayloadsAsync<ShelterIncident>(harness);
            return (trouble.Count(t => !t.Prevented), trouble.Count(t => t.Prevented), harness);
        }

        var without = await Run(security: false);
        Assert.True(without.Unprevented >= 2, $"{without.Unprevented} incidents");
        Assert.True(OnDesk(without.Harness, UserRole.Logistics, "no security"));
        Assert.Contains(await PayloadsAsync<ReportReceived>(without.Harness), r => r.Claim.StartsWith("Trouble at the rest centre"));

        var with = await Run(security: true);
        Assert.True(with.Prevented > 0);
        Assert.True(with.Unprevented < without.Unprevented, $"{with.Unprevented} with security, {without.Unprevented} without");
    }

    // ---- Infrastructure ----

    private static readonly Guid PumpingStation = new("8b2e4f60-9c1d-4e3a-8f7b-1a2c3d4e5f60");

    private static async Task<TestHarness> PowerCutAtPumpingStationAsync(double standbyHours)
    {
        var harness = new TestHarness(new InfrastructureNetworkSystem());
        await harness.Truth(new HazardSitePlaced(PumpingStation, HazardSiteKind.WaterPumping, "Ringsend pumping station", Dublin,
            Quantity: standbyHours, ServiceRadiusMeters: 800));
        await harness.Perceived(new HospitalRegistered(Guid.NewGuid(), "Near General", GeoMath.Destination(Dublin, 0, 300), 40, 10));
        await harness.Truth(new PowerOutageStarted(Guid.NewGuid(), Dublin, 1000, "test substation fault"));
        return harness;
    }

    [Fact]
    public async Task A_pumping_station_runs_on_standby_through_a_power_cut_then_fails_and_takes_hydrants_and_hospitals_with_it()
    {
        var harness = await PowerCutAtPumpingStationAsync(0.5);
        var engine = await harness.RegisterUnitAsync();
        await harness.RunAsync(InfrastructureNetworkSystem.OperatorReportDelay + TimeSpan.FromMinutes(1));
        var asset = harness.Cop.Protection.Infrastructure.Single();
        Assert.Equal((InfrastructureKind.Water, InfrastructureStatus.OnBackup), (asset.Kind, asset.Status));
        Assert.True(OnDesk(harness, UserRole.Logistics, "send a generator"));

        await harness.RunAsync(TimeSpan.FromMinutes(30));
        Assert.True(harness.World.Protection.LowWaterPressure(Dublin));
        Assert.Equal(0.5, SupplyFactors.WaterPressure(harness.World, harness.World.Units[engine]));
        Assert.Equal(36, harness.World.Hospitals.Values.Single().Capacity);
        Assert.Equal(InfrastructureStatus.Failed, harness.Cop.Protection.Infrastructure.Single().Status);
        Assert.True(harness.Cop.Protection.Infrastructure.Single().Customers > 0);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal(1, report.Protection.InfrastructureFailures);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("no generator was sent"));
    }

    [Fact]
    public async Task A_generator_sent_in_time_keeps_the_pumps_going()
    {
        var harness = await PowerCutAtPumpingStationAsync(1);
        await harness.RunAsync(InfrastructureNetworkSystem.OperatorReportDelay + TimeSpan.FromMinutes(1));
        var generator = await harness.C2.DeployGeneratorAsync(PumpingStation);
        Assert.True(generator.Succeeded, generator.Error);
        Assert.False((await harness.C2.DeployGeneratorAsync(PumpingStation)).Succeeded);

        await harness.RunAsync(TimeSpan.FromMinutes(70));
        Assert.False(harness.World.Protection.LowWaterPressure(Dublin));
        Assert.Equal(InfrastructureStatus.Normal, harness.World.Protection.Assets[PumpingStation].Status);
        Assert.Equal(40, harness.World.Hospitals.Values.Single().Capacity);
    }

    [Fact]
    public async Task A_leaking_gas_main_is_called_in_reported_and_isolated_on_request_cutting_supply()
    {
        var main = new Guid("c4d5e6f7-0819-4a2b-8c3d-4e5f60718293");
        var harness = new TestHarness(new InfrastructureNetworkSystem());
        await harness.Truth(new HazardSitePlaced(main, HazardSiteKind.GasMain, "Barrow Street gas regulator", Dublin, ServiceRadiusMeters: 500));
        await harness.Truth(new InfrastructureDamaged(main, "Barrow Street gas regulator struck by a digger"));

        await harness.RunAsync(InfrastructureNetworkSystem.OperatorReportDelay + TimeSpan.FromMinutes(1));
        Assert.Contains(await PayloadsAsync<CallReceived>(harness), c => c.Summary.Contains("smell of gas"));
        Assert.Equal(InfrastructureStatus.Leaking, harness.Cop.Protection.Infrastructure.Single().Status);
        Assert.True(OnDesk(harness, UserRole.Liaison, "Gas escaping"));

        Assert.True((await harness.C2.IsolateGasAsync(main)).Succeeded);
        Assert.False((await harness.C2.IsolateGasAsync(main)).Succeeded);
        await harness.RunAsync(InfrastructureNetworkSystem.IsolationTime + InfrastructureNetworkSystem.OperatorReportDelay + TimeSpan.FromMinutes(1));
        var isolated = harness.Cop.Protection.Infrastructure.Single();
        Assert.Equal(InfrastructureStatus.Isolated, isolated.Status);
        Assert.True(isolated.Customers > 1000);
        Assert.Empty(await PayloadsAsync<GasIgnited>(harness));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Findings, f => f.Text.Contains("was isolated") && f.Text.Contains("after it was reported"));
    }

    [Fact]
    public async Task Trains_run_through_an_evacuation_zone_until_the_operator_is_told()
    {
        var line = new Guid("5e6f7081-92a3-4b4c-9d5e-6f708192a3b4");
        var harness = new TestHarness(new InfrastructureNetworkSystem());
        await harness.Truth(new HazardSitePlaced(line, HazardSiteKind.Rail, "DART line at Barrow Street", Dublin, ServiceRadiusMeters: 300));
        await EvacuateAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Single(await PayloadsAsync<RailServiceExposed>(harness));
        Assert.Contains(await PayloadsAsync<ReportReceived>(harness), r => r.SourceName == "Irish Rail" && r.Claim.Contains("do you want them stopped"));

        Assert.True((await harness.C2.NotifyAsync("Irish Rail", "Stop DART services through Barrow Street: evacuation in force.")).Succeeded);
        await harness.RunAsync(InfrastructureNetworkSystem.RailStopTime + InfrastructureNetworkSystem.OperatorReportDelay + TimeSpan.FromMinutes(1));
        Assert.Equal(InfrastructureStatus.Suspended, harness.Cop.Protection.Infrastructure.Single().Status);
        await harness.RunAsync(TimeSpan.FromMinutes(20));
        Assert.Single(await PayloadsAsync<RailServiceExposed>(harness));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Trains through danger");
    }

    // ---- GPS ----

    private static async Task<(TestHarness Harness, Guid Unit)> CrewOnSceneAsync()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new CyberSystem());
        var unit = await harness.RegisterUnitAsync();
        var incident = await harness.CreateIncidentAsync();
        Assert.True((await harness.C2.DispatchAsync(unit, incident)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.FindUnit(unit)?.Status == UnitStatus.OnScene, TimeSpan.FromMinutes(5)));
        return (harness, unit);
    }

    [Fact]
    public async Task GPS_jamming_stops_AVL_crews_report_it_and_voice_position_reports_bring_them_back()
    {
        var (harness, unit) = await CrewOnSceneAsync();
        var jam = Guid.NewGuid();
        await harness.Truth(new GnssInterferenceStarted(jam, GnssInterferenceKind.Jamming, Dublin, 1000, DurationMinutes: 30));
        var before = (await PerceivedAsync<UnitPositionReported>(harness)).Count;

        await harness.RunAsync(CyberSystem.JammingNoticed + TimeSpan.FromMinutes(1));
        Assert.Equal(before, (await PerceivedAsync<UnitPositionReported>(harness)).Count); // nothing gets through
        var reported = Assert.Single(harness.Cop.Protection.Gnss);
        Assert.Equal(GnssInterferenceKind.Jamming, reported.Kind);
        Assert.True(OnDesk(harness, UserRole.Dispatcher, "positions by voice"));

        Assert.True((await harness.C2.SetManualPositionReportingAsync(true)).Succeeded);
        Assert.False((await harness.C2.SetManualPositionReportingAsync(true)).Succeeded);
        await harness.RunAsync(CyberSystem.VoicePositionInterval + TimeSpan.FromSeconds(10));
        Assert.Contains(await PerceivedAsync<UnitPositionReported>(harness), e => e.Source == EventSources.Comms);

        await harness.RunAsync(TimeSpan.FromMinutes(25));
        Assert.NotNull(harness.Cop.Protection.Gnss.Single().EndedAt);
        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("gave positions by voice"));
    }

    [Fact]
    public async Task GPS_spoofing_puts_units_confidently_in_the_wrong_place()
    {
        var (harness, unit) = await CrewOnSceneAsync();
        await harness.Truth(new GnssInterferenceStarted(Guid.NewGuid(), GnssInterferenceKind.Spoofing, Dublin, 1000, OffsetMeters: 400, BearingDegrees: 90));
        await harness.RunAsync(UnitResponseSystem.HeartbeatInterval * 2);

        var fix = (await PerceivedAsync<UnitPositionReported>(harness)).Last();
        var shown = ((UnitPositionReported)fix.Payload).Location;
        Assert.InRange(GeoMath.DistanceMeters(shown, harness.World.Units[unit].Location), 395, 405);
        Assert.Empty(harness.Cop.Protection.Gnss); // nobody has noticed yet
        await harness.RunAsync(CyberSystem.SpoofingNoticed);
        Assert.Equal(GnssInterferenceKind.Spoofing, harness.Cop.Protection.Gnss.Single().Kind);
    }

    // ---- Ransomware ----

    [Fact]
    public async Task Ransomware_on_dispatch_holds_crews_status_until_manual_working_and_the_NCSC_speeds_recovery()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new CyberSystem());
        var attack = Guid.NewGuid();
        await harness.Truth(new CyberAttackStarted(attack, CyberTarget.Dispatch, null, "control room CAD encrypted"));
        var unit = await harness.RegisterUnitAsync();
        var incident = await harness.CreateIncidentAsync();
        Assert.True((await harness.C2.DispatchAsync(unit, incident)).Succeeded);

        await harness.RunAsync(CyberSystem.DiscoveryDelay + TimeSpan.FromSeconds(10));
        Assert.Equal(UnitStatus.Dispatched, harness.Cop.FindUnit(unit)!.Status); // the crew pressed "on scene"; nobody heard
        Assert.True(harness.World.Units[unit].PendingData.Count > 0);
        var cyber = Assert.Single(harness.Cop.Protection.Cyber);
        Assert.True(OnDesk(harness, UserRole.IncidentCommander, "order manual working"));

        var fallback = await harness.C2.ActivateCyberFallbackAsync(attack);
        Assert.True(fallback.Succeeded, fallback.Error);
        Assert.Contains("National Cyber Security Centre", fallback.Detail);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(UnitStatus.Dispatched, harness.Cop.FindUnit(unit)!.Status); // read over the radio

        Assert.True((await harness.C2.NotifyAsync("NCSC", "Ransomware on our CAD: please assist.")).Succeeded);
        await harness.RunAsync(CyberSystem.AssistedRecoveryTime, TimeSpan.FromMinutes(1));
        Assert.NotNull(harness.Cop.Protection.Cyber.Single().RestoredAt);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("manual working"));
        Assert.Single(report.Decisions, d => d.Category == "Infrastructure");
    }

    [Fact]
    public async Task Water_treatment_left_without_manual_working_needs_a_boil_water_notice()
    {
        async Task<TestHarness> Run(bool tellThePublic)
        {
            var harness = new TestHarness(new CyberSystem());
            await harness.Truth(new CyberAttackStarted(Guid.NewGuid(), CyberTarget.WaterTreatment, null, "treatment works SCADA encrypted"));
            await harness.RunAsync(CyberSystem.WaterFallbackWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Assert.Single(await PayloadsAsync<DrinkingWaterUnsafe>(harness));
            Assert.Contains("boil water", harness.Cop.Protection.Cyber.Single().Impact);
            Assert.True(OnDesk(harness, UserRole.PublicInformation, "boil water notice"));
            if (tellThePublic)
                Assert.True((await harness.C2.IssuePublicStatementAsync("Boil water notice: boil all tap water before drinking until further notice.")).Succeeded);
            await harness.RunAsync(TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(1));
            return harness;
        }

        var silent = await Run(tellThePublic: false);
        Assert.Contains((await silent.Aar.BuildReportAsync(silent.Engine.SessionId, "Test")).Liabilities, l => l.Kind == "No boil water notice");
        var told = await Run(tellThePublic: true);
        Assert.False(OnDesk(told, UserRole.PublicInformation, "boil water notice"));
        Assert.DoesNotContain((await told.Aar.BuildReportAsync(told.Engine.SessionId, "Test")).Liabilities, l => l.Kind == "No boil water notice");
    }

    [Fact]
    public async Task Hospital_ransomware_cuts_emergency_capacity_less_so_on_paper()
    {
        var harness = new TestHarness(new InfrastructureNetworkSystem(), new CyberSystem());
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "Near General", Dublin, 40, 10));
        var attack = Guid.NewGuid();
        await harness.Truth(new CyberAttackStarted(attack, CyberTarget.Hospital, hospital, "hospital IT encrypted"));
        await harness.RunAsync(CyberSystem.DiscoveryDelay + TimeSpan.FromSeconds(10));
        Assert.Equal(28, harness.World.Hospitals[hospital].Capacity);
        Assert.Contains("Near General", harness.Cop.Protection.Cyber.Single().System);

        Assert.True((await harness.C2.ActivateCyberFallbackAsync(attack)).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(34, harness.World.Hospitals[hospital].Capacity);
        await harness.RunAsync(CyberSystem.RecoveryTime, TimeSpan.FromMinutes(1));
        Assert.Equal(40, harness.World.Hospitals[hospital].Capacity);
    }

    // ---- Environment ----

    [Fact]
    public async Task Fire_water_run_off_reaches_the_canal_and_kills_fish_unless_contained()
    {
        async Task<(TestHarness Harness, WorldProtection.Pollution Pollution)> Run(bool contain)
        {
            // A fire that stays as it is (no hazard system to grow or put it out): only the crews' water matters here.
            var harness = new TestHarness(new UnitResponseSystem(), new EnvironmentSystem());
            var world = new Guid("71829304-a5b6-4c7d-8e9f-0a1b2c3d4e5f");
            var fire = new Guid("0a1b2c3d-4e5f-4607-8192-a3b4c5d6e7f8");
            harness.World.Hazards[fire] = new WorldHazard
            {
                Id = fire, Kind = HazardKind.Fire, IncidentId = world, Origin = Dublin, Description = "Warehouse fire", StartedAt = Start,
                Model = new FireSpreadModel(Dublin, 0.8, new UniformTerrain(0.9), 1),
            };
            var incident = await harness.CreateIncidentAsync();
            if (contain) Assert.True((await harness.C2.ContainRunoffAsync(incident)).Succeeded);
            foreach (var callsign in new[] { "Engine 1", "Engine 2" })
                Assert.True((await harness.C2.DispatchAsync(await harness.RegisterUnitAsync(callsign), incident)).Succeeded);
            await harness.RunAsync(TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(15));
            return (harness, harness.World.Protection.Pollutions[world]);
        }

        var (open, polluted) = await Run(contain: false);
        Assert.True(polluted.ToWater > EnvironmentSystem.FishKillLitres, $"{polluted.ToWater:N0} L reached water");
        Assert.True(polluted.Fish > 0);
        Assert.Contains(open.Cop.Protection.Impacts, i => i.Medium == EnvironmentMedium.Water && i.What.Contains("Fish kill"));
        Assert.True(OnDesk(open, UserRole.Operations, "contain the run-off"));
        Assert.True(OnDesk(open, UserRole.Liaison, "notify the EPA"));
        var report = await open.Aar.BuildReportAsync(open.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Pollution not reported");
        Assert.True(report.Protection.Wildlife > 0);
        Assert.True(report.Protection.ToWaterLitres > EnvironmentSystem.FishKillLitres);

        var (_, contained) = await Run(contain: true);
        Assert.True(contained.Contained > 0);
        Assert.True(contained.ToWater < polluted.ToWater / 2, $"{contained.ToWater:N0} L with containment, {polluted.ToWater:N0} L without");
        Assert.Equal(0, contained.Fish);
    }

    [Fact]
    public async Task Monitoring_teams_report_readings_and_re_entry_is_warned_by_a_dirty_one()
    {
        var harness = new TestHarness(new EnvironmentSystem(), new EvacuationSystem());
        var (_, zone) = await EvacuateAsync(harness);
        harness.World.Protection.Pollutions[Guid.Empty] = new WorldProtection.Pollution { Watercourse = "Grand Canal", WatercoursePoint = Dublin };
        harness.World.Protection.Pollutions[Guid.Empty].Ground.Add((Dublin, 200));

        Assert.True((await harness.C2.DeployEnvironmentalMonitoringAsync(EnvironmentMedium.Soil, Dublin)).Succeeded);
        Assert.True((await harness.C2.DeployEnvironmentalMonitoringAsync(EnvironmentMedium.Air, Dublin)).Succeeded);
        Assert.False((await harness.C2.DeployEnvironmentalMonitoringAsync(EnvironmentMedium.Air, Dublin)).Succeeded);
        await harness.RunAsync(EnvironmentSystem.MonitorLeadTime + TimeSpan.FromMinutes(1));

        var monitors = harness.Cop.Protection.Monitors;
        Assert.Equal(PollutionLevel.High, monitors.Single(m => m.Medium == EnvironmentMedium.Soil).Level);
        Assert.Equal(PollutionLevel.Clear, monitors.Single(m => m.Medium == EnvironmentMedium.Air).Level);

        var reEntry = await harness.C2.AuthoriseReEntryAsync(zone);
        Assert.True(reEntry.Succeeded, reEntry.Error);
        Assert.Contains("soil reading there was HIGH", reEntry.Detail);
    }
}
