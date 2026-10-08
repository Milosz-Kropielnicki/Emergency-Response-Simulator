using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Tests;

public class C2AndCopTests
{
    [Fact]
    public async Task Dispatch_commits_an_available_unit_and_cancel_returns_it()
    {
        var harness = new TestHarness();
        var unitId = await harness.RegisterUnitAsync();
        var incidentId = await harness.CreateIncidentAsync();

        var dispatched = await harness.C2.DispatchAsync(unitId, incidentId);

        Assert.True(dispatched.Succeeded, dispatched.Error);
        var unit = harness.Cop.FindUnit(unitId)!;
        Assert.Equal(UnitStatus.Dispatched, unit.Status);
        Assert.Equal(incidentId, unit.AssignedIncidentId);
        Assert.Contains(unit, harness.Cop.FindIncident(incidentId)!.AssignedUnits);

        var cancelled = await harness.C2.CancelDispatchAsync(unitId, "Recalled");

        Assert.True(cancelled.Succeeded, cancelled.Error);
        Assert.Equal(UnitStatus.Available, unit.Status);
        Assert.Null(unit.AssignedIncidentId);
        Assert.Empty(harness.Cop.FindIncident(incidentId)!.AssignedUnits);
        Assert.Null(harness.World.Units[unitId].OrderedIncidentId);
    }

    [Fact]
    public async Task Dispatch_is_refused_for_a_unit_command_believes_is_busy()
    {
        var harness = new TestHarness();
        var unitId = await harness.RegisterUnitAsync();
        var first = await harness.CreateIncidentAsync("INC-1");
        var second = await harness.CreateIncidentAsync("INC-2");
        await harness.C2.DispatchAsync(unitId, first);

        var result = await harness.C2.DispatchAsync(unitId, second);

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.Error);
    }

    [Fact]
    public async Task Reassign_moves_a_committed_unit_between_incidents()
    {
        var harness = new TestHarness();
        var unitId = await harness.RegisterUnitAsync();
        var first = await harness.CreateIncidentAsync("INC-1");
        var second = await harness.CreateIncidentAsync("INC-2");
        await harness.C2.DispatchAsync(unitId, first);

        var result = await harness.C2.ReassignAsync(unitId, second);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(harness.Cop.FindIncident(first)!.AssignedUnits);
        Assert.Single(harness.Cop.FindIncident(second)!.AssignedUnits);
        Assert.Equal(second, harness.World.Units[unitId].OrderedIncidentId);
    }

    [Fact]
    public async Task Zones_appear_when_declared_and_disappear_when_lifted()
    {
        var harness = new TestHarness();
        var incidentId = await harness.CreateIncidentAsync();
        GeoPoint[] square = [new(53.34, -6.27), new(53.35, -6.27), new(53.35, -6.25), new(53.34, -6.25)];

        await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evac sector 1", square, incidentId);
        var zone = Assert.Single(harness.Cop.Zones);
        Assert.Equal("Polygon", zone.Area.GeometryType);
        Assert.True(zone.Area.Contains(TestHarness.Dublin.ToPoint()));

        await harness.C2.LiftZoneAsync(zone.Id);
        Assert.Empty(harness.Cop.Zones);
        Assert.Empty(harness.Cop.FindIncident(incidentId)!.Zones);
    }

    [Fact]
    public async Task Cop_ignores_truth_events_even_when_they_describe_the_same_place()
    {
        var harness = new TestHarness();

        await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.HazmatRelease, TestHarness.Dublin, 0.8, 20));
        await harness.Truth(new WeatherChanged(90, 8, 12, 0.6));

        Assert.Empty(harness.Cop.Incidents);
        Assert.Equal(0, harness.Cop.LastSequence);
        Assert.Null(harness.Cop.Weather);
        Assert.Equal(90, harness.World.Weather.WindFromDegrees);
    }

    [Fact]
    public async Task Cop_weather_is_the_latest_report_not_the_true_weather()
    {
        var harness = new TestHarness();
        await harness.Perceived(new WeatherObserved("Met service", TestHarness.Dublin, 270, 4.5, 14, 0.7));
        await harness.Engine.StepAsync(TimeSpan.FromMinutes(10));

        // The wind really shifts, but nobody has reported it yet.
        await harness.Truth(new WeatherChanged(45, 9, 13, 0.6));

        Assert.Equal(270, harness.Cop.Weather!.WindFromDegrees);
        Assert.Equal(TestHarness.Start, harness.Cop.Weather.ObservedAt);
        Assert.Equal(45, harness.World.Weather.WindFromDegrees);
    }

    [Fact]
    public async Task Aar_replay_shows_the_cop_as_it_stood_at_an_earlier_time()
    {
        var harness = new TestHarness();
        var unitId = await harness.RegisterUnitAsync();
        var incidentId = await harness.CreateIncidentAsync();
        await harness.Engine.StepAsync(TimeSpan.FromMinutes(2));
        await harness.C2.DispatchAsync(unitId, incidentId);
        await harness.Engine.StepAsync(TimeSpan.FromMinutes(3));
        await harness.Perceived(new UnitStatusChanged(unitId, UnitStatus.OnScene));

        var beforeDispatch = await harness.Aar.ReplayToAsync(harness.Engine.SessionId, TestHarness.Start.AddMinutes(1));
        var afterDispatch = await harness.Aar.ReplayToAsync(harness.Engine.SessionId, TestHarness.Start.AddMinutes(3));

        Assert.Equal(UnitStatus.Available, beforeDispatch.FindUnit(unitId)!.Status);
        Assert.Equal(UnitStatus.Dispatched, afterDispatch.FindUnit(unitId)!.Status);
        Assert.Equal(UnitStatus.OnScene, harness.Cop.FindUnit(unitId)!.Status);
    }
}
