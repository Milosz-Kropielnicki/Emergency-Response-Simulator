using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Emergency_Response_Simulator.ViewModels;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 16: medical, mass casualty and public health. Triage tags and the tracking board, specialist centres and
/// transport destinations, blood, the dead and the mortuary, public-health threats and mass prophylaxis, isolation and
/// quarantine, and behavioural health.
/// </summary>
public class HealthTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    private static async Task<List<T>> PayloadsAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Select(e => e.Payload).OfType<T>().ToList();

    private static Task FireAsync(TestHarness harness, string key, Dictionary<string, string> values)
    {
        var inject = InjectTemplates.Create(key, values)!;
        return harness.Engine.ApplyAsync(context =>
        {
            foreach (var (visibility, payload) in inject.Produce(context)) context.Emit(visibility, payload, EventSources.Instructor);
            return true;
        });
    }

    /// <summary>An ordinary hospital close by, and a burns centre further away.</summary>
    private static async Task<(Guid Near, Guid Burns)> HospitalsAsync(TestHarness harness)
    {
        var near = Guid.NewGuid();
        var burns = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(near, "Near General", GeoMath.Destination(Dublin, 0, 600), 30, 10));
        await harness.Perceived(new HospitalRegistered(burns, "Burns Centre", GeoMath.Destination(Dublin, 180, 3000), 30, 10));
        await harness.Perceived(new HospitalServicesRegistered(near, [], BloodUnits: 50, MorgueSpaces: 4));
        await harness.Perceived(new HospitalServicesRegistered(burns, [new(Specialty.Burns, 6)], BloodUnits: 50, MorgueSpaces: 4));
        return (near, burns);
    }

    /// <summary>A firefighter with burns (not a child: firefighters are adults), and an ambulance working the scene.</summary>
    private static async Task<(Guid Incident, Guid Ambulance)> BurnedFirefighterAsync(TestHarness harness)
    {
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, Dublin, "Barrow Street")).EntityId!.Value;
        var world = Guid.NewGuid();
        await harness.Truth(new WorldIncidentStarted(world, IncidentType.StructureFire, Dublin, 0.1, 0));
        await harness.Truth(new CasualtyInjured(Guid.NewGuid(), world, Dublin, Triage.Urgent, "firefighter, burns: caught in a flashover"));
        var ambulance = await harness.RegisterUnitAsync("Ambulance 14", UnitType.AmbulanceAls);
        return (incident, ambulance);
    }

    // ---- Patients and specialist centres ----

    [Fact]
    public async Task A_tagged_burns_patient_taken_to_the_nearest_hospital_is_transferred_to_the_burns_unit()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MedicalSystem(), new PatientTrackingSystem());
        var (near, burns) = await HospitalsAsync(harness);
        var (incident, ambulance) = await BurnedFirefighterAsync(harness);
        Assert.True((await harness.C2.DispatchAsync(ambulance, incident)).Succeeded);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Patients.Count == 1, TimeSpan.FromMinutes(5)));
        var patient = harness.Cop.Health.Patients.Single();
        Assert.Equal("T-0001", patient.Tag);
        Assert.Equal(Specialty.Burns, patient.Need);
        Assert.Contains(RoleDesk.For(UserRole.Operations, harness.Cop, _ => true), d => d.Text.Contains("burns patient(s)") && d.Text.Contains("no destination"));

        // No destination set: the crew goes to the nearest, which has no burns unit.
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Patients.Single().ArrivedAt is not null, TimeSpan.FromMinutes(30), Step));
        patient = harness.Cop.Health.Patients.Single();
        Assert.Equal(near, patient.HospitalId);
        Assert.False(patient.RightPlace);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Patients.Single().Transfers == 1,
            PatientTrackingSystem.TransferDelay + TimeSpan.FromMinutes(1), Step));
        Assert.Equal(burns, harness.Cop.Health.Patients.Single().HospitalId);
        Assert.Equal(1, harness.World.Health.Hospitals[burns].Occupied[Specialty.Burns]);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal((1, 1, 0, 1), (report.Health.Tagged, report.Health.Arrived, report.Health.RightFirstTime, report.Health.Transfers));
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("0 of 1 patients reached a hospital that could treat them first time"));
    }

    [Fact]
    public async Task With_a_destination_set_burns_patients_go_straight_to_the_burns_unit()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MedicalSystem(), new PatientTrackingSystem());
        var (_, burns) = await HospitalsAsync(harness);
        var (incident, ambulance) = await BurnedFirefighterAsync(harness);

        var set = await harness.C2.SetTransportDestinationAsync(incident, Specialty.Burns, burns);
        Assert.True(set.Succeeded, set.Error);
        Assert.False((await harness.C2.SetTransportDestinationAsync(incident, Specialty.Burns, burns)).Succeeded); // already
        Assert.True((await harness.C2.DispatchAsync(ambulance, incident)).Succeeded);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Patients.SingleOrDefault()?.ArrivedAt is not null, TimeSpan.FromMinutes(40), Step));
        var patient = harness.Cop.Health.Patients.Single();
        Assert.Equal(burns, patient.HospitalId);
        Assert.True(patient.RightPlace);
        Assert.Equal(0, patient.Transfers);
        await harness.RunAsync(PatientTrackingSystem.ReportInterval, Step);
        Assert.Equal(1, harness.Cop.Health.Hospitals[burns].Beds(Specialty.Burns)!.Occupied);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal(100, report.Health.RightFirstTimePercent);
        Assert.Single(report.Decisions, d => d.Category == "Medical");
    }

    [Fact]
    public async Task A_childrens_hospital_takes_children_only()
    {
        var harness = new TestHarness();
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.Critical, Dublin)).EntityId!.Value;
        var childrens = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(childrens, "Children's Hospital", Dublin, 20, 5));
        await harness.Perceived(new HospitalServicesRegistered(childrens, [new(Specialty.Paediatric, 10)], 20, 2, ChildrenOnly: true));

        Assert.Contains("children only", (await harness.C2.SetTransportDestinationAsync(incident, Specialty.Trauma, childrens)).Error);
        Assert.True((await harness.C2.SetTransportDestinationAsync(incident, Specialty.Paediatric, childrens)).Succeeded);
        Assert.False(harness.World.Health.Accepts(harness.World.Hospitals[childrens], null));
        Assert.True(harness.World.Health.Accepts(harness.World.Hospitals[childrens], Specialty.Paediatric));

        // Clearing a destination sends them back to the nearest.
        Assert.True((await harness.C2.SetTransportDestinationAsync(incident, Specialty.Paediatric, null)).Succeeded);
        Assert.Empty(harness.Cop.Health.Destinations);
        Assert.False((await harness.C2.SetTransportDestinationAsync(incident, Specialty.Paediatric, null)).Succeeded);
    }

    [Fact]
    public async Task A_hospital_running_low_on_blood_says_so_and_the_IBTS_restocks_it()
    {
        var harness = new TestHarness(new PatientTrackingSystem());
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "St. James's Hospital", Dublin, 40, 20));
        await harness.Perceived(new HospitalServicesRegistered(hospital, [], BloodUnits: 14, MorgueSpaces: 6));

        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Blood running low at St. James's Hospital");
        Assert.NotNull(harness.Cop.Health.Hospitals[hospital].BloodLowSince);
        Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.StartsWith("Blood low at St. James's"));

        Assert.False((await harness.C2.RequestBloodAsync(hospital, 0)).Succeeded);
        Assert.True((await harness.C2.RequestBloodAsync(hospital, 40)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Hospitals[hospital].BloodUnits >= 50,
            PatientTrackingSystem.BloodLeadTime + TimeSpan.FromMinutes(1), Step));
        Assert.Null(harness.Cop.Health.Hospitals[hospital].BloodLowSince);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("reported blood low; the IBTS was asked"));
    }

    // ---- The dead ----

    [Fact]
    public async Task The_dead_stay_at_the_scene_until_the_coroner_is_notified_then_go_to_the_temporary_mortuary_and_are_identified()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MassFatalitySystem());
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructuralCollapse, IncidentPriority.Critical, Dublin, "Barrow Street")).EntityId!.Value;
        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructuralCollapse, Dublin, 0.5, 0));
        var engine = await harness.RegisterUnitAsync("Engine 12");
        Assert.True((await harness.C2.DispatchAsync(engine, incident)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.World.Units[engine].Phase == ResponsePhase.Operating, TimeSpan.FromMinutes(5)));

        await FireAsync(harness, "fatalities", new() { ["count"] = "6", ["cause"] = "found in the collapsed building" });
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Fatalities.AtScene.GetValueOrDefault(incident) == 6, TimeSpan.FromMinutes(1)));
        Assert.Contains(RoleDesk.For(UserRole.Liaison, harness.Cop, _ => true), d => d.Text.Contains("notify the coroner"));
        Assert.Contains(RoleDesk.For(UserRole.Operations, harness.Cop, _ => true), d => d.Text.Contains("open a temporary mortuary"));

        // Without the coroner, nobody is moved.
        await harness.RunAsync(TimeSpan.FromMinutes(30), Step);
        Assert.Equal(6, harness.Cop.Health.Fatalities.AtScene[incident]);

        var opened = await harness.C2.OpenTemporaryMortuaryAsync("Irishtown Stadium", GeoMath.Destination(Dublin, 90, 1500), incident);
        Assert.Contains("coroner", opened.Detail);
        Assert.False((await harness.C2.OpenTemporaryMortuaryAsync("Another hall", Dublin)).Succeeded);
        Assert.True((await harness.C2.NotifyAsync("Dublin District Coroner", "Six dead at Barrow Street; requesting authority to recover")).Succeeded);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Fatalities.Recovered == 6,
            MassFatalitySystem.RecoveryStart + MassFatalitySystem.RecoveryInterval * 2 + TimeSpan.FromMinutes(1), Step));
        Assert.Equal(6, harness.Cop.Health.Fatalities.InMortuary);
        Assert.Equal(0, harness.Cop.Health.Fatalities.AtScene[incident]);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Fatalities.Identified == 6, TimeSpan.FromHours(1), Step));
        Assert.All(await PayloadsAsync<VictimIdentified>(harness), v => Assert.Equal("Irishtown Stadium", v.Where));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal(6, report.Health.Fatalities);
        Assert.Equal(6, report.Health.Identified);
        Assert.NotNull(report.Health.CoronerAfter);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.StartsWith("Coroner notified"));
        Assert.Contains(report.Documentation.Undocumented, u => u.Contains("temporary mortuary opened"));
        Assert.Contains("Medical, fatalities &amp; public health", Simulation.Aar.AarExport.Html(report, new Simulation.Aar.AarSignOff("Alex", "Operations", "Olly", null)));
        Assert.Contains("Health,", Simulation.Aar.AarExport.Csv(report));
    }

    [Fact]
    public async Task A_full_hospital_mortuary_holds_the_dead_in_the_emergency_department_until_a_temporary_mortuary_takes_them()
    {
        var harness = new TestHarness(new MassFatalitySystem());
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "Near General", Dublin, 20, 10));
        await harness.Perceived(new HospitalServicesRegistered(hospital, [], BloodUnits: 30, MorgueSpaces: 1));
        // Two brought in dead by ambulance.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var id = Guid.NewGuid();
            await harness.Truth(new CasualtyInjured(id, null, Dublin, Triage.Urgent, "crushed"));
            await harness.Truth(new CasualtyChanged(id, Triage.Deceased, CasualtyState.Deceased, hospital));
        }
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(11, harness.World.Hospitals[hospital].Occupied); // one in the mortuary, one held in the department
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Mortuary full at Near General");

        await harness.C2.OpenTemporaryMortuaryAsync("Irishtown Stadium", Dublin);
        await harness.RunAsync(MassFatalitySystem.MortuaryTransfer + TimeSpan.FromMinutes(1), Step);
        Assert.Equal(10, harness.World.Hospitals[hospital].Occupied);
        Assert.Equal(WorldHealth.BodyPlace.Mortuary, harness.World.Health.Bodies.Values.Single(b => b.Place != WorldHealth.BodyPlace.HospitalMorgue).Place);
    }

    // ---- Public health ----

    [Fact]
    public async Task An_outbreak_spreads_until_isolated_and_quarantined_and_ordering_it_without_public_health_is_a_legal_exposure()
    {
        async Task<(TestHarness Harness, int Ill)> Outbreak(bool control, bool consult)
        {
            var harness = new TestHarness(new PublicHealthSystem());
            await harness.C2.CreateIncidentAsync(IncidentType.Other, IncidentPriority.High, Dublin, "Rest centre");
            await FireAsync(harness, "outbreak", new() { ["agent"] = "measles", ["cases"] = "4", ["contacts"] = "200" });
            Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Threats.Count == 1, PublicHealthSystem.InfectiousAlertDelay + TimeSpan.FromMinutes(1)));
            var threat = harness.Cop.Health.Threats.Single();
            Assert.Equal(HealthThreatKind.Infectious, threat.Kind);
            Assert.Equal(204, threat.EstimatedExposed);
            Assert.Contains(RoleDesk.For(UserRole.IncidentCommander, harness.Cop, _ => true), d => d.Text.Contains("isolate the cases"));
            if (control)
            {
                if (consult) await harness.C2.NotifyAsync("HSE Public Health", "Measles at the rest centre: isolation and quarantine needed");
                var isolated = await harness.C2.OrderIsolationAsync(threat.Id);
                Assert.True(isolated.Succeeded, isolated.Error);
                Assert.Equal(consult, isolated.Detail is null);
                Assert.True((await harness.C2.OrderQuarantineAsync(threat.Id)).Succeeded);
                Assert.False((await harness.C2.OrderQuarantineAsync(threat.Id)).Succeeded);
            }
            await harness.RunAsync(TimeSpan.FromHours(4), TimeSpan.FromSeconds(30));
            return (harness, harness.World.Health.Threats.Values.Single().Ill);
        }

        var (_, unchecked_) = await Outbreak(control: false, consult: false);
        var (controlled, controlledIll) = await Outbreak(control: true, consult: true);
        Assert.True(unchecked_ > 2 * controlledIll, $"{unchecked_} ill unchecked, {controlledIll} with isolation and quarantine");
        Assert.Empty(await PayloadsAsync<LegalExposure>(controlled));

        var (alone, _) = await Outbreak(control: true, consult: false);
        Assert.Equal(2, (await PayloadsAsync<LegalExposure>(alone)).Count(l => l.Kind == "Public health order"));
        var report = await alone.Aar.BuildReportAsync(alone.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Public health order");
        Assert.Contains(report.Findings, f => f.Text.Contains("measles: cases isolated"));
    }

    [Fact]
    public async Task A_biological_release_is_answered_with_points_of_dispensing_and_people_come_when_told()
    {
        var options = new CivilianOptions { PopulationPerIncident = 300, CarOwnership = 0, LimitedEnglish = 0 };
        var harness = new TestHarness(new HazardSystem(new UniformTerrain(0.2)), new CivilianSystem(new UniformTerrain(0.2), options),
            new PublicHealthSystem(), new LogisticsSystem());
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.Critical, Dublin, "Barrow Street")).EntityId!.Value;
        await harness.C2.DeclareZoneAsync(ZoneType.StagingArea, "Staging", Wgs84.Circle(GeoMath.Destination(Dublin, 270, 800), 40), incident);
        await harness.Truth(new WorldIncidentStarted(new Guid("4f6c2d9e-1b7a-4c35-9e0d-2a8b5f13c7e4"), IncidentType.HazmatRelease, Dublin, 0.3, 0));
        await harness.Truth(new HazardStarted(new Guid("b7e2c9a4-5d31-4f8e-a6c0-19d4e7b3f582"), null, HazardKind.Plume, Dublin, "Aerosol release", 0.05, "anthrax spores", 60));

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Health.Threats.Count == 1, PublicHealthSystem.AlertDelay + TimeSpan.FromMinutes(1), Step));
        var threat = harness.Cop.Health.Threats.Single();
        Assert.Equal(HealthThreatKind.Biological, threat.Kind);
        Assert.Equal(incident, threat.IncidentId);
        Assert.True(threat.EstimatedExposed >= 10);
        Assert.Contains("not passed from person to person", (await harness.C2.OrderIsolationAsync(threat.Id)).Error);
        Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.Contains("open points of dispensing"));

        var pod = await harness.C2.OpenDispensingPointAsync(threat.Id, "Ringsend College", GeoMath.Destination(Dublin, 270, 1200));
        Assert.True(pod.Succeeded, pod.Error);
        Assert.Contains("order prophylaxis", pod.Detail);
        Assert.True((await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Prophylaxis, 300, SupplyTier.Local)).Succeeded);
        await harness.C2.IssuePublicStatementAsync("If you were near Barrow Street this afternoon, collect antibiotics at Ringsend College now.", incident);

        await harness.RunAsync(TimeSpan.FromHours(2), Step);
        var exposed = harness.World.Health.Threats.Values.Single().TotalExposed;
        var outcome = (await PayloadsAsync<HealthThreatOutcome>(harness)).Last();
        Assert.True(outcome.ProtectedInTime > exposed / 2, $"{outcome.ProtectedInTime} of {exposed} protected in time");
        Assert.True(harness.Cop.Health.Dispensing.Single().Dispensed > 0);
        Assert.True(outcome.Ill < exposed / 10, $"{outcome.Ill} ill"); // illness starts two hours after exposure, and most are protected by then

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.True(report.Health.ProtectedInTime > 0);
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("given antibiotics in time"));
        var decision = Assert.Single(report.Decisions, d => d.Category == "Public health");
        Assert.Contains("HSE Public Health confirmed", decision.Trigger);
    }

    // ---- Behavioural health ----

    [Fact]
    public async Task Distress_brings_the_worried_well_to_the_emergency_department_and_counsellors_triage_it()
    {
        var harness = new TestHarness(new BehaviouralHealthSystem(), new PatientTrackingSystem());
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.Critical, Dublin, "Barrow Street")).EntityId!.Value;
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "Near General", GeoMath.Destination(Dublin, 0, 800), 40, 20));
        await harness.Perceived(new HospitalServicesRegistered(hospital, [new(Specialty.Psychiatric, 4)], 40, 6));
        var world = Guid.NewGuid();
        foreach (var i in Enumerable.Range(0, 20))
            await harness.Truth(new CasualtyInjured(Guid.NewGuid(), world, GeoMath.Destination(Dublin, i * 18, 30), i < 4 ? Triage.Deceased : Triage.Delayed, "blast injuries"));

        await harness.RunAsync(BehaviouralHealthSystem.ReportInterval + TimeSpan.FromSeconds(10));
        var distressed = harness.Cop.Health.Distressed[incident];
        Assert.InRange(distressed, 50, 70); // 16 injured × 2 + 4 dead × 6
        Assert.True(harness.World.Health.Hospitals[hospital].WorriedWellNow > 0);
        Assert.Contains(RoleDesk.For(UserRole.Liaison, harness.Cop, _ => true), d => d.Text.Contains("send crisis counselling"));

        Assert.False((await harness.C2.DeployCrisisCounsellingAsync(incident, CounsellingSetting.FamilyAssistanceCentre)).Succeeded); // none open
        Assert.True((await harness.C2.DeployCrisisCounsellingAsync(incident, CounsellingSetting.Scene)).Succeeded);
        Assert.True((await harness.C2.DeployCrisisCounsellingAsync(null, CounsellingSetting.Helpline)).Succeeded);
        Assert.False((await harness.C2.DeployCrisisCounsellingAsync(null, CounsellingSetting.Helpline)).Succeeded);

        await harness.RunAsync(TimeSpan.FromHours(1), Step);
        var team = harness.Cop.Health.Counselling.Single(c => c.Setting == CounsellingSetting.Scene);
        Assert.InRange(team.Seen, 40, 56);
        Assert.Equal(team.Seen / 10, team.High);
        Assert.True(team.Admitted is > 0 and <= 4); // a psychiatric bed for each high-risk person, while there are beds

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.True(report.Health.SeenByCounsellors >= team.Seen);
        Assert.Contains(report.Findings, f => f.Strength && f.Text.StartsWith("Crisis counselling saw"));
    }

    // ---- Who does what ----

    [Fact]
    public void Patient_distribution_is_operations_quarantine_the_ic_and_dispensing_and_counselling_are_arranged_through_liaison()
    {
        static bool May(UserRole role, string method) => PermissionMatrix.Decide<IC2Service>(role, method, _ => true).Allowed;
        Assert.True(May(UserRole.Operations, nameof(IC2Service.SetTransportDestinationAsync)));
        Assert.False(May(UserRole.Logistics, nameof(IC2Service.SetTransportDestinationAsync)));
        Assert.True(May(UserRole.Logistics, nameof(IC2Service.RequestBloodAsync)));
        Assert.True(May(UserRole.IncidentCommander, nameof(IC2Service.OrderQuarantineAsync)));
        Assert.False(May(UserRole.Operations, nameof(IC2Service.OrderIsolationAsync)));
        Assert.True(May(UserRole.Liaison, nameof(IC2Service.OpenDispensingPointAsync)));
        Assert.True(May(UserRole.Liaison, nameof(IC2Service.DeployCrisisCounsellingAsync)));
        Assert.True(May(UserRole.Operations, nameof(IC2Service.OpenTemporaryMortuaryAsync)));

        Assert.Contains(WorkspacePanel.Health, RoleWorkspaces.For(UserRole.Operations).Panels);
        Assert.Contains(WorkspacePanel.Health, RoleWorkspaces.For(UserRole.Liaison).Panels);
    }
}
