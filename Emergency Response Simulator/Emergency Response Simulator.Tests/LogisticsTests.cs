using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Crews;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Emergency_Response_Simulator.ViewModels;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 15: logistics and resource sustainment. Supplies that run out and the orders that bring more, from local,
/// regional and national tiers; fuel and the bowser; staging areas and base camps; wear, breakdowns, servicing and
/// repair; contracts, costs and claiming them back; and what the review makes of it all.
/// </summary>
public class LogisticsTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    // Fixed ids: wear, fuel and faults are seeded from them, so every run sees the same fleet.
    private static readonly Guid EngineId = new("6d1f3a52-8b4e-4c7a-9f20-3e5b7c1d9a84");
    private static readonly Guid HazmatId = new("a3c7e915-2f6d-4b81-8e4a-c09d5b7f3216");

    private static async Task<List<T>> PayloadsAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Select(e => e.Payload).OfType<T>().ToList();

    private static async Task<Guid> UnitAsync(TestHarness harness, Guid id, string callsign, UnitType type = UnitType.Engine, int crew = 5)
    {
        await harness.Perceived(new UnitRegistered(id, callsign, type, null, Dublin, "Station 3", crew, []));
        return id;
    }

    private static async Task<Guid> IncidentAsync(TestHarness harness, IncidentType type = IncidentType.StructureFire, IncidentPriority priority = IncidentPriority.High)
    {
        var created = await harness.C2.CreateIncidentAsync(type, priority, Dublin, "Barrow Street");
        Assert.True(created.Succeeded, created.Error);
        return created.EntityId!.Value;
    }

    private static async Task<Guid> StagingAsync(TestHarness harness, Guid incident, ZoneType type = ZoneType.StagingArea, double metres = 300)
    {
        var at = GeoMath.Destination(Dublin, 90, metres);
        var declared = await harness.C2.DeclareZoneAsync(type, type == ZoneType.BaseCamp ? "Base camp" : "Staging A", Wgs84.Circle(at, 40), incident);
        Assert.True(declared.Succeeded, declared.Error);
        return harness.Cop.Zones.Single(z => z.Type == type).Id;
    }

    private static async Task AtSceneAsync(TestHarness harness, Guid incident, params Guid[] units)
    {
        foreach (var unit in units)
            Assert.True((await harness.C2.DispatchAsync(unit, incident)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => units.All(u => harness.World.Units[u].Phase == ResponsePhase.Operating), TimeSpan.FromMinutes(5)));
    }

    private static Task FireAsync(TestHarness harness, string key, Dictionary<string, string> values)
    {
        var inject = InjectTemplates.Create(key, values)!;
        return harness.Engine.ApplyAsync(context =>
        {
            foreach (var (visibility, payload) in inject.Produce(context)) context.Emit(visibility, payload, EventSources.Instructor);
            return true;
        });
    }

    // ---- Supplies ----

    [Fact]
    public async Task Foam_runs_out_on_a_chemical_fire_after_a_warning_and_a_local_order_brings_more()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new LogisticsSystem());
        var incident = await IncidentAsync(harness, IncidentType.HazmatRelease);
        await UnitAsync(harness, EngineId, "Engine 12");
        await UnitAsync(harness, HazmatId, "Hazmat 2", UnitType.Hazmat, 6);
        await AtSceneAsync(harness, incident, EngineId, HazmatId);

        // What the appliances carried is the incident's first stock: 200 L of foam on the engine, 600 on the hazmat unit.
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Incidents.ContainsKey(incident), TimeSpan.FromMinutes(6), Step));
        Assert.InRange(harness.World.Logistics.Stock(incident, SupplyKind.Foam), 700, 800);

        // Both pump 8 L/min: the supply unit warns with 20 minutes left, then it runs out.
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Incidents[incident].Stock(SupplyKind.Foam)?.OutSince is not null,
            TimeSpan.FromMinutes(70), Step));
        var warnings = await PayloadsAsync<SupplyShortage>(harness);
        var low = warnings.First(w => w.Kind == SupplyKind.Foam && !w.Exhausted);
        Assert.InRange(low.MinutesLeft!.Value, 1, 20);
        Assert.Contains(warnings, w => w.Kind == SupplyKind.Foam && w.Exhausted);
        Assert.Contains(harness.Cop.Alerts, a => a.Category == AlertCategory.ResourceShortage && a.Title.StartsWith("Foam exhausted"));
        Assert.Equal(0.35, SupplyFactors.Suppression(harness.World, harness.World.Units[EngineId]), 3); // water alone won't hold it
        Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.StartsWith("Foam has run out"));

        // Somewhere to unload, then a local order: decided in a minute, here 25 minutes after.
        await StagingAsync(harness, incident);
        var ordered = await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Foam, 1000, SupplyTier.Local);
        Assert.True(ordered.Succeeded, ordered.Error);
        Assert.DoesNotContain(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.StartsWith("Foam has run out"));
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single().Status == SupplyOrderStatus.Delivered, TimeSpan.FromMinutes(30), Step));

        var order = harness.Cop.Logistics.Orders.Single();
        Assert.Equal(Supplies.Supplier(SupplyTier.Local), order.Supplier);
        Assert.Equal(6000m, order.Cost);
        Assert.Equal(1, SupplyFactors.Suppression(harness.World, harness.World.Units[EngineId]));
        Assert.Equal(6000m, harness.World.Logistics.Costs.Total[CostCategory.Supplies]);

        // The review: the outage, its warning and the order, and the order timed from the warning.
        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        var outage = Assert.Single(report.Logistics.Outages, o => o.Kind == SupplyKind.Foam);
        Assert.Equal(low.MinutesLeft is not null, outage.WarnedAt is not null);
        Assert.NotNull(outage.OrderedAt);
        Assert.NotNull(outage.To);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("Foam ran out"));
        var decision = Assert.Single(report.Decisions, d => d.Category == "Logistics");
        Assert.Contains("foam was low", decision.Trigger);
        Assert.Equal(Rating.Poor, decision.Rating); // ordered only after it had run out
    }

    [Fact]
    public async Task A_delivery_with_nowhere_to_unload_waits_at_the_cordon()
    {
        var harness = new TestHarness(new LogisticsSystem());
        var incident = await IncidentAsync(harness);
        var ordered = await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Water, 200, SupplyTier.Local);
        Assert.True(ordered.Succeeded, ordered.Error);
        Assert.Contains("no staging area or base camp", ordered.Detail);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single().Reason?.Contains("nowhere") == true
                                                      || harness.Cop.Logistics.Orders.Single().Reason?.Contains("no staging area") == true,
            TimeSpan.FromMinutes(30), Step));
        var held = Assert.Single(await PayloadsAsync<SupplyDeliveryDelayed>(harness));
        var due = Start + Supplies.DecisionTime(SupplyTier.Local) + Supplies.LeadTime(SupplyTier.Local) + LogisticsSystem.NoDropPointDelay;
        Assert.InRange(held.ExpectedAt, due, due + TimeSpan.FromSeconds(30));
        Assert.Equal(SupplyOrderStatus.Approved, harness.Cop.Logistics.Orders.Single().Status);

        // Held once: it is delivered the next time round, staging area or not.
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single().Status == SupplyOrderStatus.Delivered, TimeSpan.FromMinutes(20), Step));
        Assert.Equal(200, harness.World.Logistics.Stock(incident, SupplyKind.Water));
    }

    [Fact]
    public async Task Orders_climb_the_tiers_local_then_regional_then_national_reserves_in_a_major_emergency()
    {
        var harness = new TestHarness(new LogisticsSystem());
        var incident = await IncidentAsync(harness, IncidentType.HazmatRelease, IncidentPriority.Critical);
        await StagingAsync(harness, incident);

        // The regional and national levels want to know why.
        Assert.False((await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Foam, 2000, SupplyTier.Regional)).Succeeded);
        Assert.False((await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Foam, 0, SupplyTier.Local)).Succeeded);

        // The local depot has nothing left: the order is declined, pointing up a tier.
        await FireAsync(harness, "local-shortage", new() { ["kind"] = "Foam", ["reason"] = "sent to another fire" });
        var local = (await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Foam, 1000, SupplyTier.Local)).EntityId!.Value;
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single(o => o.Id == local).Status == SupplyOrderStatus.Declined,
            TimeSpan.FromMinutes(3), Step));
        Assert.Contains("regional", harness.Cop.Logistics.Orders.Single(o => o.Id == local).Reason);

        // The regional stores release up to five typical orders at once, at a premium.
        var regional = (await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Foam, 8000, SupplyTier.Regional, "Tank fire; local foam exhausted")).EntityId!.Value;
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single(o => o.Id == regional).Status == SupplyOrderStatus.Delivered,
            TimeSpan.FromMinutes(60), Step));
        var regionalOrder = harness.Cop.Logistics.Orders.Single(o => o.Id == regional);
        Assert.Equal(5000, regionalOrder.Granted);
        Assert.Equal(5000 * 6m * 1.15m, regionalOrder.Cost);
        Assert.Equal(5000, harness.World.Logistics.Stock(incident, SupplyKind.Foam));

        // National reserves only for a declared major emergency.
        var refused = (await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Water, 1000, SupplyTier.National, "Decontamination of 300 people")).EntityId!.Value;
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single(o => o.Id == refused).Status == SupplyOrderStatus.Declined,
            TimeSpan.FromMinutes(8), Step));
        Assert.Contains("major emergency", harness.Cop.Logistics.Orders.Single(o => o.Id == refused).Reason);

        await harness.Perceived(new EocLevelReached(EocLevel.Partial));
        var national = (await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Water, 1000, SupplyTier.National, "Decontamination of 300 people")).EntityId!.Value;
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single(o => o.Id == national).Status == SupplyOrderStatus.Delivered,
            TimeSpan.FromMinutes(100), Step));
        Assert.Equal("Civil Defence", harness.Cop.Logistics.Orders.Single(o => o.Id == national).Supplier);
    }

    [Fact]
    public async Task A_late_delivery_is_reported_and_arrives_late()
    {
        var harness = new TestHarness(new LogisticsSystem());
        var incident = await IncidentAsync(harness);
        await StagingAsync(harness, incident);
        await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Food, 60, SupplyTier.Local);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single().ExpectedAt is not null, TimeSpan.FromMinutes(3), Step));
        var due = harness.Cop.Logistics.Orders.Single().ExpectedAt!.Value;

        await FireAsync(harness, "supply-delay", new() { ["minutes"] = "30", ["reason"] = "lorry stuck behind a crash" });
        Assert.Equal(due + TimeSpan.FromMinutes(30), harness.Cop.Logistics.Orders.Single().ExpectedAt);
        await harness.RunAsync(due - harness.Cop.AsOf + TimeSpan.FromMinutes(5), Step);
        Assert.Equal(SupplyOrderStatus.Approved, harness.Cop.Logistics.Orders.Single().Status);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Orders.Single().Status == SupplyOrderStatus.Delivered, TimeSpan.FromMinutes(30), Step));
    }

    // ---- Fuel ----

    [Fact]
    public async Task A_pumping_engine_calls_its_fuel_runs_dry_and_stops_until_the_bowser_reaches_it()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new LogisticsSystem());
        var incident = await IncidentAsync(harness);
        await UnitAsync(harness, EngineId, "Engine 12");
        await AtSceneAsync(harness, incident, EngineId);
        await harness.Engine.ApplyAsync(context => context.World.Units[EngineId].Fuel = 0.12);

        Assert.True(await harness.RunUntilAsync(() => harness.World.Units[EngineId].OutOfFuel, TimeSpan.FromMinutes(40), Step));
        var calls = await PayloadsAsync<UnitFuelReported>(harness);
        Assert.Contains(calls, c => c.Level is > 0 and <= 0.1);
        Assert.Contains(calls, c => c.Level == 0);
        Assert.True(harness.World.Units[EngineId].Disabled); // the pump has stopped
        Assert.True(harness.Cop.Logistics.FleetOf(EngineId)!.Dry);
        Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.Contains("run out of fuel"));
        Assert.False((await harness.C2.StageUnitAsync(EngineId, Guid.NewGuid())).Succeeded);

        // Fuel to the incident: the bowser fills the empty one first.
        await StagingAsync(harness, incident);
        await harness.C2.OrderSuppliesAsync(incident, SupplyKind.Fuel, 1000, SupplyTier.Local);
        Assert.True(await harness.RunUntilAsync(() => !harness.World.Units[EngineId].OutOfFuel, TimeSpan.FromMinutes(30), Step));
        Assert.True(harness.World.Units[EngineId].Fuel > 0.95);
        var refuel = Assert.Single(await PayloadsAsync<UnitRefuelled>(harness));
        Assert.InRange(refuel.Litres, 190, 200);
        Assert.InRange(harness.World.Logistics.Stock(incident, SupplyKind.Fuel), 800, 810);
        Assert.False(harness.Cop.Logistics.FleetOf(EngineId)!.Dry);
    }

    // ---- Staging ----

    [Fact]
    public async Task A_staged_unit_drives_to_the_staging_area_checks_in_and_waits_until_committed()
    {
        var harness = new TestHarness(new UnitResponseSystem());
        var incident = await IncidentAsync(harness);
        await UnitAsync(harness, EngineId, "Engine 12");
        var staging = await StagingAsync(harness, incident, metres: 800);

        Assert.False((await harness.C2.StageUnitAsync(EngineId, Guid.NewGuid())).Succeeded); // not a staging area
        Assert.True((await harness.C2.StageUnitAsync(EngineId, staging)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(EngineId)?.StagedAt is not null, TimeSpan.FromMinutes(5)));
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(EngineId)!.Status);
        Assert.True(GeoMath.DistanceMeters(harness.World.Units[EngineId].Location, GeoMath.Destination(Dublin, 90, 800)) < 60);
        Assert.False((await harness.C2.StageUnitAsync(EngineId, staging)).Succeeded); // already there

        // Committed from staging: it leaves it.
        Assert.True((await harness.C2.DispatchAsync(EngineId, incident)).Succeeded);
        Assert.Null(harness.Cop.Logistics.FleetOf(EngineId)!.StagingZoneId);
        Assert.False(harness.World.Logistics.Staged.ContainsKey(EngineId));
        Assert.False((await harness.C2.StageUnitAsync(EngineId, staging)).Succeeded); // only available units
    }

    // ---- Maintenance and failure ----

    [Fact]
    public async Task A_worn_appliance_warns_before_failing_and_servicing_it_resets_the_wear()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MaintenanceSystem());
        var incident = await IncidentAsync(harness);
        await UnitAsync(harness, EngineId, "Engine 12");
        await AtSceneAsync(harness, incident, EngineId);
        await harness.Engine.ApplyAsync(context => context.World.Units[EngineId].Wear = 0.745);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(EngineId)?.Defect is not null, TimeSpan.FromMinutes(5)));
        var warning = harness.Cop.Logistics.FleetOf(EngineId)!;
        Assert.False(warning.Immobilised);
        Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.Contains("service it before it fails"));

        var serviced = await harness.C2.ServiceUnitAsync(EngineId);
        Assert.True(serviced.Succeeded, serviced.Error);
        Assert.Contains("comes off", serviced.Detail);
        Assert.False((await harness.C2.ServiceUnitAsync(EngineId)).Succeeded); // already with the mechanic
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UnitStatus.OutOfService, harness.Cop.FindUnit(EngineId)!.Status);
        Assert.True(harness.World.Units[EngineId].UnderRepair); // at its own station: the mechanic is there

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(EngineId)!.RepairedAt is not null, MaintenanceSystem.ServiceTime + TimeSpan.FromMinutes(1)));
        Assert.Equal(0.2, harness.World.Units[EngineId].Wear, 3);
        Assert.Null(harness.Cop.Logistics.FleetOf(EngineId)!.Defect);
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(EngineId)!.Status);
        Assert.Empty(await PayloadsAsync<UnitBrokeDown>(harness));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal(1, report.Logistics.Serviced);
        Assert.Contains(report.Findings, f => f.Strength && f.Text.Contains("sent for servicing"));
    }

    [Fact]
    public async Task A_warning_ignored_ends_in_a_breakdown_and_the_workshop_returns_the_unit_to_its_station()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MaintenanceSystem());
        var incident = await IncidentAsync(harness);
        await UnitAsync(harness, EngineId, "Engine 12");
        await AtSceneAsync(harness, incident, EngineId);
        await harness.Engine.ApplyAsync(context => context.World.Units[EngineId].Wear = 0.95);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(EngineId)?.Immobilised == true, TimeSpan.FromHours(3), Step));
        var broken = harness.Cop.Logistics.FleetOf(EngineId)!;
        Assert.Equal(UnitStatus.OutOfService, harness.Cop.FindUnit(EngineId)!.Status);
        Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Text.Contains("broken down"));
        if (!broken.FieldRepairable)
            Assert.Contains("needs the workshop", (await harness.C2.RequestRepairAsync(EngineId, RepairKind.FieldRepair)).Error);

        Assert.True((await harness.C2.RequestRepairAsync(EngineId, RepairKind.Workshop)).Succeeded);
        Assert.False((await harness.C2.RequestRepairAsync(EngineId, RepairKind.Workshop)).Succeeded); // already being repaired
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        var due = harness.Cop.Logistics.FleetOf(EngineId)!.RepairExpectedAt!.Value;
        Assert.InRange(due - harness.Cop.AsOf, TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(185));

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(EngineId)!.RepairedAt is not null, TimeSpan.FromHours(3.5), Step));
        Assert.True((await PayloadsAsync<UnitRepaired>(harness)).Single().AtHome);
        Assert.Equal(harness.World.Units[EngineId].Home, harness.World.Units[EngineId].Location);
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(EngineId)!.Status);
        Assert.InRange(harness.World.Logistics.Costs.Total[CostCategory.Repairs], 2000m, 6000m);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        var breakdown = Assert.Single(report.Logistics.Breakdowns);
        Assert.NotNull(breakdown.WarnedAt);
        Assert.NotNull(breakdown.BackAt);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("after its crew warned of it"));
        Assert.Contains("Logistics &amp; finance", Simulation.Aar.AarExport.Html(report, new Simulation.Aar.AarSignOff("Alex", "Logistics", "Olly", null)));
        Assert.Contains("Breakdown,", Simulation.Aar.AarExport.Csv(report));
    }

    [Fact]
    public async Task A_flat_tyre_is_fixed_where_it_stands_but_a_failed_pump_needs_the_workshop()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new MaintenanceSystem());
        var incident = await IncidentAsync(harness);
        await UnitAsync(harness, EngineId, "Engine 12");
        await UnitAsync(harness, HazmatId, "Engine 14");
        await AtSceneAsync(harness, incident, EngineId, HazmatId);

        await FireAsync(harness, "breakdown", new() { ["callsign"] = "Engine 12", ["fault"] = "flat tyre" });
        await FireAsync(harness, "breakdown", new() { ["callsign"] = "Engine 14", ["fault"] = "pump seal has failed" });
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(HazmatId)?.Immobilised == true
                                                      && harness.Cop.Logistics.FleetOf(EngineId)?.Immobilised == true, TimeSpan.FromMinutes(4)));
        Assert.True(harness.Cop.Logistics.FleetOf(EngineId)!.FieldRepairable);
        Assert.False(harness.Cop.Logistics.FleetOf(HazmatId)!.FieldRepairable);
        Assert.False((await harness.C2.RequestRepairAsync(HazmatId, RepairKind.FieldRepair)).Succeeded);
        Assert.False((await harness.C2.RequestRepairAsync(EngineId, RepairKind.Service)).Succeeded);

        var location = harness.World.Units[EngineId].Location;
        Assert.True((await harness.C2.RequestRepairAsync(EngineId, RepairKind.FieldRepair)).Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.FleetOf(EngineId)!.RepairedAt is not null, TimeSpan.FromMinutes(55), Step));
        Assert.False((await PayloadsAsync<UnitRepaired>(harness)).Single().AtHome);
        Assert.Equal(location, harness.World.Units[EngineId].Location);
        Assert.False(harness.World.Units[EngineId].BrokenDown);
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(EngineId)!.Status);
    }

    // ---- Contracts, costs and claims ----

    [Fact]
    public async Task Contracts_deliver_on_a_schedule_and_plant_hire_brings_an_excavator()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new LogisticsSystem());
        var incident = await IncidentAsync(harness);
        await UnitAsync(harness, EngineId, "Engine 12");
        await AtSceneAsync(harness, incident, EngineId);

        Assert.False((await harness.C2.AwardContractAsync(ContractKind.Catering, "Bia Catering", "Hot meals", 8000m)).Succeeded); // for which incident?
        var catering = await harness.C2.AwardContractAsync(ContractKind.Catering, "Bia Catering", "Hot meals and water every two hours", 8000m, incidentId: incident);
        Assert.True(catering.Succeeded, catering.Error);
        Assert.False((await harness.C2.AwardContractAsync(ContractKind.Catering, "Other Catering", "Meals", 5000m, incidentId: incident)).Succeeded);
        Assert.True((await harness.C2.AwardContractAsync(ContractKind.PlantHire, "Dublin Plant Hire", "Excavator and operator", 6000m, incidentId: incident)).Succeeded);

        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Units.Any(u => u.Type == UnitType.HeavyMachinery), TimeSpan.FromMinutes(50), Step));
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Contracts.Single(c => c.Kind == ContractKind.Catering).Deliveries > 0, TimeSpan.FromMinutes(5), Step));
        var delivered = (await PayloadsAsync<SuppliesDelivered>(harness)).Where(d => d.SourceId == catering.EntityId).ToList();
        Assert.Contains(delivered, d => d.Kind == SupplyKind.Food && d.Quantity >= 6);
        Assert.Contains(delivered, d => d.Kind == SupplyKind.Water && d.Quantity >= 15);
        Assert.All(delivered, d => Assert.Equal(0m, d.Cost)); // the contract's value covers them
        Assert.Equal(14_000m, harness.World.Logistics.Costs.Total[CostCategory.Contracts]);

        // The next delivery comes two hours on.
        var count = delivered.Count;
        await harness.RunAsync(TimeSpan.FromMinutes(100), TimeSpan.FromSeconds(30));
        Assert.Equal(count, (await PayloadsAsync<SuppliesDelivered>(harness)).Count(d => d.SourceId == catering.EntityId));
        await harness.RunAsync(TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(30));
        Assert.Equal(count * 2, (await PayloadsAsync<SuppliesDelivered>(harness)).Count(d => d.SourceId == catering.EntityId));
    }

    [Fact]
    public async Task Costs_are_kept_by_category_and_a_major_emergencys_eligible_costs_are_claimed_back()
    {
        var harness = new TestHarness(new UnitResponseSystem(), new LogisticsSystem(), new CommandResponseSystem());
        var incident = await IncidentAsync(harness, priority: IncidentPriority.Critical);
        await UnitAsync(harness, EngineId, "Engine 12");
        // Already past the end of their shift: every hour is overtime.
        await harness.Perceived(new CrewRostered(EngineId, CrewRoster.Generate("Engine 12", UnitType.Engine, 5, []),
            CrewRoster.ShiftLength(UnitType.Engine) + TimeSpan.FromHours(1), CrewRoster.ShiftLength(UnitType.Engine)));
        await AtSceneAsync(harness, incident, EngineId);

        // Mutual aid: billed by its provider.
        Assert.True((await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.MutualAid, UnitType.Engine, 1, "Second line of hose")).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(30), Step);
        Assert.NotNull(harness.Cop.Logistics.CostsReportedAt);
        var lines = harness.Cop.Logistics.Costs.ToDictionary(l => l.Category, l => l.Amount);
        Assert.True(lines[CostCategory.Overtime] > 0);
        Assert.True(lines[CostCategory.Equipment] > 0);
        Assert.True(lines[CostCategory.MutualAid] > 0);
        Assert.False(lines.ContainsKey(CostCategory.Personnel));
        Assert.Equal(0m, harness.Cop.Logistics.Reimbursable);

        // No major emergency, no claim.
        Assert.Contains("major emergency", (await harness.C2.SubmitReimbursementClaimAsync()).Error);

        await harness.Perceived(new EocLevelReached(EocLevel.Partial));
        var contract = await harness.C2.AwardContractAsync(ContractKind.Other, "Lumen Hire", "Floodlighting for the night", 40_000m, incidentId: incident);
        Assert.Contains("can't be claimed", contract.Detail);
        var justified = await harness.C2.AwardContractAsync(ContractKind.FuelSupply, "Emo Oil", "Bowser on site, refilled every two hours", 30_000m,
            "Only supplier able to deliver diesel to the cordon tonight", incident);
        Assert.DoesNotContain("can't be claimed", justified.Detail);
        await harness.RunAsync(LogisticsSystem.CostReportInterval + TimeSpan.FromMinutes(1), Step);
        var claimable = harness.Cop.Logistics.Reimbursable;
        Assert.True(claimable > 30_000m); // the justified contract, and what has been spent since the declaration
        Assert.True(harness.Cop.Logistics.TotalCost > claimable + 40_000m); // not the undocumented contract, nor anything before the declaration
        Assert.Contains(RoleDesk.For(UserRole.FinanceAdmin, harness.Cop, _ => true), d => d.Text.Contains("not yet claimed"));

        var claim = await harness.C2.SubmitReimbursementClaimAsync();
        Assert.True(claim.Succeeded, claim.Error);
        Assert.False((await harness.C2.SubmitReimbursementClaimAsync()).Succeeded); // one at a time
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Logistics.Claims.Single().DecidedAt is not null,
            LogisticsSystem.ClaimDecisionTime + TimeSpan.FromMinutes(1), Step));
        var decided = harness.Cop.Logistics.Claims.Single();
        Assert.Equal(claimable, decided.Approved);
        Assert.Contains(decided.Disallowed, d => d.Contains("Lumen Hire") && d.Contains("no written justification"));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal(claimable, report.Logistics.Claimed);
        Assert.Equal(claimable, report.Logistics.Approved);
        Assert.True(report.Logistics.Total > 0);
        Assert.Contains(report.Liabilities, l => l.Kind == "Procurement");
        Assert.Contains(report.Documentation.Undocumented, u => u.Contains("Lumen Hire")); // a significant decision, never logged
    }

    // ---- What running short does ----

    [Fact]
    public void Running_short_slows_the_work_and_nothing_changes_without_the_logistics_model()
    {
        var world = new WorldState();
        var incident = Guid.NewGuid();
        world.Logistics.IncidentTypes[incident] = IncidentType.HazmatRelease;
        var crew = new WorldUnit { Id = Guid.NewGuid(), Callsign = "Ambulance 14", Type = UnitType.AmbulanceAls, OrderedIncidentId = incident };

        // Off: no shortage counts.
        world.Logistics.Draw(incident, SupplyKind.Water, 10, Start);
        Assert.Equal(1, SupplyFactors.Welfare(world, crew));
        Assert.Equal(1, SupplyFactors.Treat(world, crew, 1, Start));

        world.Logistics.Active = true;
        Assert.Equal(1.35, SupplyFactors.Welfare(world, crew), 3);
        Assert.Equal(0.6, SupplyFactors.Rehab(world, crew), 3);

        world.Logistics.Add(incident, SupplyKind.MedicalSupplies, 2);
        Assert.Equal(1, SupplyFactors.Treat(world, crew, 2, Start)); // a P1: two kits
        Assert.Equal(2, SupplyFactors.Treat(world, crew, 1, Start)); // none left: improvising
        Assert.True(world.Logistics.Short(incident, SupplyKind.MedicalSupplies));

        world.Logistics.Add(incident, SupplyKind.Batteries, 1);
        Assert.True(SupplyFactors.SpareBattery(world, crew, Start));
        Assert.False(SupplyFactors.SpareBattery(world, crew, Start));

        world.Logistics.Draw(incident, SupplyKind.Foam, 1, Start);
        world.Logistics.Draw(incident, SupplyKind.Ppe, 1, Start);
        Assert.Equal(0.35 * 0.5, SupplyFactors.Suppression(world, crew), 3);

        // Water back, and a base camp: rehab works better than usual.
        world.Logistics.Add(incident, SupplyKind.Water, 100);
        world.Apply(new SimEvent
        {
            Sequence = 1, SimTime = Start, Visibility = EventVisibility.Perceived, Source = EventSources.C2,
            Payload = new ZoneDeclared(Guid.NewGuid(), ZoneType.BaseCamp, "Base camp", Wgs84.Circle(Dublin, 40), incident),
        });
        Assert.Equal(1.3, SupplyFactors.Rehab(world, crew), 3);
        Assert.Equal(1, SupplyFactors.Welfare(world, crew));
    }

    [Fact]
    public void Supply_ordering_is_logistics_staging_is_operations_and_claims_are_finance()
    {
        static bool May(UserRole role, string method) => PermissionMatrix.Decide<IC2Service>(role, method, _ => true).Allowed;

        Assert.True(May(UserRole.Logistics, nameof(IC2Service.OrderSuppliesAsync)));
        Assert.False(May(UserRole.Operations, nameof(IC2Service.OrderSuppliesAsync)));
        Assert.True(May(UserRole.Operations, nameof(IC2Service.StageUnitAsync)));
        Assert.True(May(UserRole.Dispatcher, nameof(IC2Service.StageUnitAsync)));
        Assert.True(May(UserRole.Logistics, nameof(IC2Service.RequestRepairAsync)));
        Assert.True(May(UserRole.FinanceAdmin, nameof(IC2Service.AwardContractAsync)));
        Assert.True(May(UserRole.FinanceAdmin, nameof(IC2Service.SubmitReimbursementClaimAsync)));
        Assert.False(May(UserRole.Logistics, nameof(IC2Service.SubmitReimbursementClaimAsync)));
        Assert.False(May(UserRole.PublicInformation, nameof(IC2Service.ServiceUnitAsync)));

        // With nobody in Finance, the claim falls to the IC.
        Assert.True(PermissionMatrix.Decide<IC2Service>(UserRole.IncidentCommander, nameof(IC2Service.SubmitReimbursementClaimAsync),
            r => r != UserRole.FinanceAdmin).ByFallback);

        // Logistics and Finance work from the LOGISTICS tab; the IC and Operations have it too.
        Assert.Equal(WorkspacePanel.Logistics, RoleWorkspaces.For(UserRole.Logistics).Focus);
        Assert.Equal(WorkspacePanel.Logistics, RoleWorkspaces.For(UserRole.FinanceAdmin).Focus);
        Assert.Contains(WorkspacePanel.Logistics, RoleWorkspaces.For(UserRole.IncidentCommander).Panels);
        Assert.Contains(WorkspacePanel.Logistics, RoleWorkspaces.For(UserRole.Operations).Panels);
    }
}
