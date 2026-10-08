using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 2: incident and resource objects, status dynamics, attention management, time dimension.</summary>
public class CopSituationTests
{
    // ---- Status dynamics ----

    [Fact]
    public void Status_rules_allow_the_design_cycle_and_nothing_else()
    {
        UnitStatus[] cycle = [UnitStatus.Available, UnitStatus.Dispatched, UnitStatus.EnRoute, UnitStatus.OnScene,
            UnitStatus.Operating, UnitStatus.Transporting, UnitStatus.Available];
        for (var i = 0; i < cycle.Length - 1; i++)
            Assert.True(UnitStatusRules.CanTransition(cycle[i], cycle[i + 1]), $"{cycle[i]} → {cycle[i + 1]}");

        Assert.True(UnitStatusRules.CanTransition(UnitStatus.EnRoute, UnitStatus.Cancelled));
        Assert.True(UnitStatusRules.CanTransition(UnitStatus.Cancelled, UnitStatus.Available));
        Assert.True(UnitStatusRules.CanTransition(UnitStatus.Operating, UnitStatus.OutOfService));
        Assert.False(UnitStatusRules.CanTransition(UnitStatus.Available, UnitStatus.OnScene));
        Assert.False(UnitStatusRules.CanTransition(UnitStatus.Dispatched, UnitStatus.Operating));
        Assert.False(UnitStatusRules.CanTransition(UnitStatus.OutOfService, UnitStatus.OutOfService));
    }

    [Fact]
    public async Task C2_refuses_status_jumps_the_rules_do_not_allow()
    {
        var harness = new TestHarness();
        var unit = await harness.RegisterUnitAsync();

        var result = await harness.C2.UpdateUnitStatusAsync(unit, UnitStatus.OnScene);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot go from Available to On scene", result.Error);
    }

    [Fact]
    public async Task Dispatched_units_turn_out_travel_arrive_and_start_work()
    {
        var harness = new TestHarness(new UnitResponseSystem());
        var unit = await harness.RegisterUnitAsync();
        var target = GeoMath.Destination(TestHarness.Dublin, 90, 1500);
        var incident = Guid.NewGuid();
        await harness.Perceived(new IncidentCreated(incident, "INC-1", IncidentType.StructureFire, IncidentPriority.High, target, null, null));
        await harness.C2.DispatchAsync(unit, incident);

        await StepAsync(harness, TimeSpan.FromSeconds(50));
        Assert.Equal(UnitStatus.EnRoute, harness.Cop.FindUnit(unit)!.Status);
        Assert.NotNull(harness.Cop.FindUnit(unit)!.Eta);

        var travel = UnitResponseSystem.EstimateTravelTime(TestHarness.Dublin, target, UnitType.Engine);
        await StepAsync(harness, travel / 2);
        var halfway = GeoPoint.FromPoint(harness.Cop.FindUnit(unit)!.Location!);
        Assert.InRange(GeoMath.DistanceMeters(halfway, target), 500, 1000);

        await StepAsync(harness, travel / 2 + TimeSpan.FromSeconds(2));
        var arrived = harness.Cop.FindUnit(unit)!;
        Assert.Equal(UnitStatus.OnScene, arrived.Status);
        Assert.True(GeoMath.DistanceMeters(GeoPoint.FromPoint(arrived.Location!), target) < 1);

        await StepAsync(harness, TimeSpan.FromSeconds(65));
        Assert.Equal(UnitStatus.Operating, harness.Cop.FindUnit(unit)!.Status);
    }

    [Fact]
    public async Task A_unit_whose_radio_fails_keeps_working_but_command_stops_hearing_it()
    {
        var harness = new TestHarness(new UnitResponseSystem());
        var unit = await harness.RegisterUnitAsync();
        var incident = await harness.CreateIncidentAsync();
        await harness.C2.DispatchAsync(unit, incident);
        await harness.Truth(new UnitRadioFailed(unit, Failed: true));

        await StepAsync(harness, TimeSpan.FromMinutes(5));

        Assert.Equal(ResponsePhase.Operating, harness.World.Units[unit].Phase);
        Assert.Equal(UnitStatus.Dispatched, harness.Cop.FindUnit(unit)!.Status);
    }

    // ---- Attention management ----

    [Fact]
    public async Task Silent_committed_units_raise_one_communication_failure_until_heard_again()
    {
        var harness = new TestHarness(cop => [new UnitResponseSystem(), new AttentionMonitor(cop, new AttentionOptions())]);
        var unit = await harness.RegisterUnitAsync();
        var incident = await harness.CreateIncidentAsync();
        await harness.C2.DispatchAsync(unit, incident);
        await StepAsync(harness, TimeSpan.FromMinutes(2));
        await harness.Truth(new UnitRadioFailed(unit, Failed: true));

        await StepAsync(harness, TimeSpan.FromMinutes(10));

        var alert = Assert.Single(harness.Cop.Alerts, a => a.Category == AlertCategory.CommunicationFailure);
        Assert.Equal(unit, alert.UnitId);
        Assert.Contains("has not transmitted for 4 minutes", alert.Message);
        Assert.False(harness.Cop.FindUnit(unit)!.CommsConnected);

        await harness.Truth(new UnitRadioFailed(unit, Failed: false));
        await StepAsync(harness, TimeSpan.FromMinutes(2));
        Assert.True(harness.Cop.FindUnit(unit)!.CommsConnected);
    }

    [Fact]
    public async Task Resource_shortage_is_raised_once_and_rearmed_when_units_free_up()
    {
        var harness = new TestHarness(cop => [new AttentionMonitor(cop, new AttentionOptions())]);
        var ambulances = new List<Guid>();
        for (var i = 1; i <= 3; i++)
            ambulances.Add(await harness.RegisterUnitAsync($"Ambulance {i}", UnitType.AmbulanceAls));
        var incident = await harness.CreateIncidentAsync();

        await harness.C2.DispatchAsync(ambulances[0], incident);
        await harness.C2.DispatchAsync(ambulances[1], incident);
        await StepAsync(harness, TimeSpan.FromSeconds(10));
        await StepAsync(harness, TimeSpan.FromSeconds(10));

        var shortage = Assert.Single(harness.Cop.Alerts, a => a.Category == AlertCategory.ResourceShortage);
        Assert.Equal("ALS ambulances: 1 of 3 available", shortage.Title);

        // Both return: slack again, so the condition re-arms; a later shortage alerts again.
        await harness.C2.CancelDispatchAsync(ambulances[0]);
        await harness.C2.CancelDispatchAsync(ambulances[1]);
        await StepAsync(harness, TimeSpan.FromSeconds(10));
        await harness.C2.DispatchAsync(ambulances[0], incident);
        await harness.C2.DispatchAsync(ambulances[2], incident);
        await StepAsync(harness, TimeSpan.FromSeconds(10));

        Assert.Equal(2, harness.Cop.Alerts.Count(a => a.Category == AlertCategory.ResourceShortage));
    }

    [Fact]
    public async Task Critical_incidents_and_wind_shifts_are_flagged()
    {
        var harness = new TestHarness(cop => [new AttentionMonitor(cop, new AttentionOptions())]);
        var incident = await harness.CreateIncidentAsync();
        await harness.Perceived(new WeatherObserved("Met", TestHarness.Dublin, 270, 4, 14, 0.7));
        await StepAsync(harness, TimeSpan.FromSeconds(5));

        await harness.C2.UpdateIncidentAsync(incident, priority: IncidentPriority.Critical);
        await harness.Perceived(new WeatherObserved("Met", TestHarness.Dublin, 225, 7, 13, 0.7));
        await StepAsync(harness, TimeSpan.FromSeconds(5));
        await StepAsync(harness, TimeSpan.FromSeconds(5));

        Assert.Single(harness.Cop.Alerts, a => a.Category == AlertCategory.Critical && a.IncidentId == incident);
        var wind = Assert.Single(harness.Cop.Alerts, a => a.Category == AlertCategory.SituationChange);
        Assert.Contains("Previous: W → E", wind.Message);
        Assert.Contains("Current: SW → NE", wind.Message);
    }

    // ---- Incident objects, reports and alerts through C2 ----

    [Fact]
    public async Task Incidents_open_from_reports_and_carry_command_edits()
    {
        var harness = new TestHarness();
        var call = Guid.NewGuid();
        await harness.Perceived(new CallReceived(call, "999 caller", "Smoke from a warehouse", TestHarness.Dublin, 150));

        var created = await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High,
            TestHarness.Dublin, "Barrow Street", fromReportId: call);
        var id = created.EntityId!.Value;
        await harness.C2.UpdateIncidentAsync(id, casualtiesReported: 5, threats: ["Fire", "Possible chemical storage"]);
        await harness.C2.AssignIncidentCommanderAsync(id, "Chief Murphy");
        await harness.C2.AssessReportAsync(call, VerificationStatus.Confirmed, Confidence.High);

        var incident = harness.Cop.FindIncident(id)!;
        Assert.Equal("INC-00001", incident.Number);
        Assert.Equal(5, incident.CasualtiesReported);
        Assert.Equal(["Fire", "Possible chemical storage"], incident.Threats);
        Assert.Equal("Chief Murphy", incident.IncidentCommanderName);
        var report = Assert.Single(incident.Reports);
        Assert.Equal(VerificationStatus.Confirmed, report.Verification);

        Assert.False((await harness.C2.UpdateIncidentAsync(id, casualtiesReported: 5)).Succeeded); // nothing changed
    }

    [Fact]
    public async Task Incidents_cannot_close_with_units_still_assigned()
    {
        var harness = new TestHarness();
        var unit = await harness.RegisterUnitAsync();
        var incident = await harness.CreateIncidentAsync();
        await harness.C2.DispatchAsync(unit, incident);

        var result = await harness.C2.UpdateIncidentAsync(incident, status: IncidentStatus.Closed);

        Assert.False(result.Succeeded);
        Assert.Contains("Release the 1 assigned unit", result.Error);
    }

    [Fact]
    public async Task Alerts_are_acknowledged_once()
    {
        var harness = new TestHarness();
        var alert = Guid.NewGuid();
        await harness.Perceived(new AlertRaised(alert, AlertCategory.Critical, AlertSeverity.Critical, "ED full", "No beds", null, null));

        Assert.True((await harness.C2.AcknowledgeAlertAsync(alert)).Succeeded);
        Assert.False((await harness.C2.AcknowledgeAlertAsync(alert)).Succeeded);
        Assert.NotNull(harness.Cop.Alerts.Single().AcknowledgedAt);
    }

    // ---- Operational boundaries ----

    [Fact]
    public async Task Hazard_zones_are_concentric_circles_of_the_requested_radii()
    {
        var harness = new TestHarness();
        var incident = await harness.CreateIncidentAsync();

        var result = await harness.C2.EstablishHazardZonesAsync(TestHarness.Dublin, 50, 150, 400, incident);

        Assert.True(result.Succeeded, result.Error);
        var projection = MetricProjection.For(TestHarness.Dublin);
        var byType = harness.Cop.Zones.ToDictionary(z => z.Type);
        foreach (var (type, radius) in new[] { (ZoneType.HotZone, 50.0), (ZoneType.WarmZone, 150.0), (ZoneType.ColdZone, 400.0) })
        {
            var area = projection.AreaSquareMeters(byType[type].Area);
            Assert.Equal(Math.PI * radius * radius, area, Math.PI * radius * radius * 0.02);
            Assert.Equal(incident, byType[type].IncidentId);
        }
        Assert.False((await harness.C2.EstablishHazardZonesAsync(TestHarness.Dublin, 150, 50, 400)).Succeeded);
    }

    // ---- Time dimension ----

    [Fact]
    public void History_lines_read_like_the_design_document()
    {
        var unit = Guid.NewGuid();
        var incident = Guid.NewGuid();
        string? Unit(Guid id) => id == unit ? "Engine 12" : null;
        string? Incident(Guid id) => id == incident ? "INC-00241" : null;
        SimEvent Event(DomainEvent payload) => new() { Sequence = 1, Source = "test", Payload = payload, Visibility = EventVisibility.Perceived };

        var status = EventDescriber.Describe(Event(new UnitStatusChanged(unit, UnitStatus.EnRoute)), Unit, Incident)!;
        var dispatch = EventDescriber.Describe(Event(new UnitDispatched(unit, incident, null)), Unit, Incident)!;
        var update = EventDescriber.Describe(Event(new IncidentUpdated(incident, Priority: IncidentPriority.Critical)), Unit, Incident)!;

        Assert.Equal(("UNIT STATUS", "Engine 12 → En route"), (status.Category, status.Text));
        Assert.Equal(("UNIT DISPATCHED", "Engine 12 → INC-00241"), (dispatch.Category, dispatch.Text));
        Assert.Equal("INC-00241 Priority: Critical", update.Text);
        Assert.True(update.Important);
        Assert.Null(EventDescriber.Describe(Event(new UnitPositionReported(unit, TestHarness.Dublin, 40, 90, null)), Unit, Incident));
    }

    [Fact]
    public async Task Cop_view_switches_between_live_and_a_replayed_moment()
    {
        var harness = new TestHarness();
        var view = new CopView(harness.Cop);
        await harness.CreateIncidentAsync("INC-1");
        await harness.Engine.StepAsync(TimeSpan.FromMinutes(5));
        await harness.CreateIncidentAsync("INC-2");
        var changes = 0;
        view.Changed += (_, _) => changes++;

        view.ShowReplay(await harness.Aar.ReplayToAsync(harness.Engine.SessionId, TestHarness.Start.AddMinutes(1)),
            TestHarness.Start.AddMinutes(1));
        Assert.True(view.IsReplay);
        Assert.Single(view.Incidents);

        await harness.CreateIncidentAsync("INC-3"); // live keeps moving underneath
        Assert.Single(view.Incidents);

        view.ShowLive();
        Assert.Equal(3, view.Incidents.Count);
        Assert.Equal(2, changes); // one per switch; live changes during replay are not forwarded
    }

    // ---- Scripted scenario ----

    [Fact]
    public async Task Barrow_street_scenario_delivers_uncertain_information_but_never_decides_for_the_trainee()
    {
        var harness = new TestHarness(cop => [BarrowStreetScenario.Create(), new UnitResponseSystem(), new AttentionMonitor(cop, new AttentionOptions())]);
        await new DemoRosterScenario().SeedAsync(harness.Engine);

        await StepAsync(harness, TimeSpan.FromMinutes(12), step: TimeSpan.FromSeconds(5));

        Assert.Single(harness.World.Incidents); // the fire really exists
        Assert.Empty(harness.Cop.Incidents);    // but only the trainee can open an incident
        Assert.Equal(2, harness.Cop.Reports.Count(r => r.Source == ReportSource.EmergencyCall));
        Assert.Contains(harness.Cop.Reports, r => r.Source == ReportSource.Media && r.Confidence == Confidence.Low);
        Assert.Equal(210, harness.Cop.Weather!.WindFromDegrees);
        Assert.Contains(harness.Cop.Alerts, a => a.Category == AlertCategory.SituationChange);
        Assert.Contains(harness.Cop.Alerts, a => a.Title.Contains("St. James's"));
    }

    private static async Task StepAsync(TestHarness harness, TimeSpan total, TimeSpan? step = null)
    {
        var increment = step ?? TimeSpan.FromSeconds(1);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += increment)
            await harness.Engine.StepAsync(increment);
    }
}
