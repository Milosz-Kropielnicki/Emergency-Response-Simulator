using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 4: orders, requests, approvals, notifications, ICS structure and span of control.</summary>
public class CommandControlTests
{
    private static TestHarness Harness() =>
        new(cop => [new CommandResponseSystem(), new AttentionMonitor(cop, new AttentionOptions())]);

    private static async Task<(Guid Incident, List<Guid> Units)> IncidentWithUnits(TestHarness harness, int count,
        IncidentPriority priority = IncidentPriority.High)
    {
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, priority, TestHarness.Dublin)).EntityId!.Value;
        var units = new List<Guid>();
        for (var i = 1; i <= count; i++)
        {
            var unit = await harness.RegisterUnitAsync($"Engine {i}");
            await harness.C2.DispatchAsync(unit, incident);
            units.Add(unit);
        }
        return (incident, units);
    }

    private static async Task Step(TestHarness harness, TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(1))
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>An order id whose seeded roll falls in [min, max), so the outcome is known in advance.</summary>
    private static Guid IdWithRoll(double min, double max)
    {
        while (true)
        {
            var id = Guid.NewGuid();
            var roll = CommandResponseSystem.Roll(id);
            if (roll >= min && roll < max) return id;
        }
    }

    // ---- Span of control ----

    [Fact]
    public void Span_of_control_counts_direct_reports_for_every_supervisor()
    {
        var units = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var flat = new IncidentCommand();
        flat.Positions[IcsRole.IncidentCommander] = "Chief Murphy";

        var ic = Assert.Single(SpanOfControl.Assess(flat, units));
        Assert.Equal(10, ic.DirectReports);
        Assert.True(ic.IsOverloaded);
        Assert.Equal(2.5, ic.DelayFactor);
        Assert.Equal(0.3, ic.LossProbability, 6);

        var structured = new IncidentCommand();
        structured.Positions[IcsRole.IncidentCommander] = "Chief Murphy";
        structured.Positions[IcsRole.Operations] = "DC Byrne";
        structured.Positions[IcsRole.Safety] = "Officer O'Connor";
        var fire = new IcsGroup { Id = Guid.NewGuid(), Name = "Fire Group" };
        var medical = new IcsGroup { Id = Guid.NewGuid(), Name = "Medical Group" };
        fire.UnitIds.AddRange(units.Take(5));
        medical.UnitIds.AddRange(units.Skip(5).Take(4));
        structured.Groups.AddRange([fire, medical]);

        var spans = SpanOfControl.Assess(structured, units).ToDictionary(s => s.Supervisor, s => s.DirectReports);
        Assert.Equal(3, spans["Operations"]);         // two groups + one ungrouped unit
        Assert.Equal(2, spans["Incident Commander"]);  // Operations + Safety
        Assert.Equal(5, spans["Fire Group"]);
        Assert.Equal(4, spans["Medical Group"]);
        Assert.False(SpanOfControl.SupervisorOf(units[0], structured, units).IsOverloaded);
    }

    [Fact]
    public async Task Overloading_the_commander_raises_an_alert_that_clears_once_groups_are_formed()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 9);
        await harness.C2.AssignIncidentCommanderAsync(incident, "Chief Murphy");
        await Step(harness, TimeSpan.FromSeconds(2));

        Assert.Single(harness.Cop.Alerts, a => a.Title.Contains("Incident Commander has 9 direct reports"));

        await harness.C2.AssignIcsPositionAsync(incident, IcsRole.Operations, "DC Byrne");
        var group = (await harness.C2.FormGroupAsync(incident, "Fire Group", IcsGroupKind.Group, "SO Kelly")).EntityId!.Value;
        foreach (var unit in units.Take(5))
            Assert.True((await harness.C2.AssignUnitToGroupAsync(unit, group)).Succeeded);
        await Step(harness, TimeSpan.FromSeconds(2));

        var spans = SpanOfControl.Assess(harness.Cop.FindIncident(incident)!.Command, units);
        Assert.DoesNotContain(spans, s => s.IsOverloaded);
        Assert.Single(harness.Cop.Alerts, a => a.Category == AlertCategory.Safety); // no new alert
    }

    [Fact]
    public async Task Ics_commands_validate_structure()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 1);
        var idle = await harness.RegisterUnitAsync("Engine 99");
        var group = (await harness.C2.FormGroupAsync(incident, "Fire Group", IcsGroupKind.Group)).EntityId!.Value;

        Assert.False((await harness.C2.FormGroupAsync(incident, "fire group", IcsGroupKind.Group)).Succeeded);
        Assert.False((await harness.C2.AssignUnitToGroupAsync(idle, group)).Succeeded); // not committed
        Assert.True((await harness.C2.AssignUnitToGroupAsync(units[0], group)).Succeeded);

        await harness.C2.DisbandGroupAsync(incident, group);
        Assert.Null(harness.Cop.FindIncident(incident)!.Command.GroupOf(units[0])); // back under command
    }

    // ---- Orders ----

    [Fact]
    public async Task Orders_are_read_back_by_the_unit_and_can_be_closed()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 1);

        var order = (await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, units[0], "Set up exposure protection on unit 4")).EntityId!.Value;
        await Step(harness, CommandResponseSystem.UnitReadBackDelay + TimeSpan.FromSeconds(1));

        var acknowledged = harness.Cop.Orders.Single(o => o.Id == order);
        Assert.Equal(OrderStatus.Acknowledged, acknowledged.Status);
        Assert.Equal("Engine 1: copy, Set up exposure protection on unit 4", acknowledged.ReadBack);

        await harness.C2.CloseOrderAsync(order, completed: true);
        Assert.Equal(OrderStatus.Completed, harness.Cop.Orders.Single().Status);
        Assert.False((await harness.C2.CloseOrderAsync(order, completed: false)).Succeeded);
    }

    [Fact]
    public async Task Orders_to_unstaffed_positions_are_refused()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 1);

        var result = await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Position, null, "Prepare the IAP", IcsRole.Planning);

        Assert.False(result.Succeeded);
        Assert.Contains("not staffed", result.Error);
    }

    [Fact]
    public async Task A_unit_with_a_dead_radio_never_reads_back_and_command_is_alerted()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 1);
        await harness.Truth(new UnitRadioFailed(units[0], Failed: true));

        await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, units[0], "Withdraw to the cold zone");
        await Step(harness, TimeSpan.FromMinutes(2.5));

        Assert.Equal(OrderStatus.Issued, harness.Cop.Orders.Single().Status);
        Assert.Single(harness.Cop.Alerts, a => a.Title == "Order to Engine 1 not acknowledged");
    }

    [Fact]
    public async Task An_overloaded_supervisor_reads_back_slowly_and_loses_some_orders()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 10); // all ten report straight to the IC: span 10
        var lost = IdWithRoll(0, 0.3);      // under the 30% loss probability
        var heard = IdWithRoll(0.7, 1.0);   // well clear of loss and garbling

        await harness.Perceived(new OrderIssued(lost, incident, OrderTargetKind.Unit, units[0], "Engine 1", "Ventilate the roof"));
        await harness.Perceived(new OrderIssued(heard, incident, OrderTargetKind.Unit, units[1], "Engine 2", "Lay a supply line"));

        // A normal read-back would arrive after 20 s; at span 10 it takes 2.5× as long.
        await Step(harness, TimeSpan.FromSeconds(45));
        Assert.All(harness.Cop.Orders, o => Assert.Equal(OrderStatus.Issued, o.Status));

        await Step(harness, TimeSpan.FromSeconds(10));
        Assert.Equal(OrderStatus.Acknowledged, harness.Cop.Orders.Single(o => o.Id == heard).Status);
        Assert.Equal(OrderStatus.Issued, harness.Cop.Orders.Single(o => o.Id == lost).Status);

        await Step(harness, TimeSpan.FromMinutes(2));
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Order to Engine 1 not acknowledged");
    }

    // ---- Resource requests ----

    [Fact]
    public async Task Additional_resources_are_approved_by_control_and_arrive_dispatched_to_the_incident()
    {
        var harness = new TestHarness(cop => [new CommandResponseSystem(), new UnitResponseSystem()]);
        var (incident, _) = await IncidentWithUnits(harness, 0);

        var request = (await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.AdditionalResources, UnitType.AmbulanceAls, 2)).EntityId!.Value;
        await Step(harness, TimeSpan.FromSeconds(2));
        var approved = harness.Cop.ResourceRequests.Single(r => r.Id == request);
        Assert.Equal(ResourceRequestStatus.Approved, approved.Status);
        Assert.Equal("Ambulance Control", approved.DecidedBy);

        await Step(harness, CommandResponseSystem.AdditionalLeadTime);

        Assert.Equal(ResourceRequestStatus.Fulfilled, harness.Cop.ResourceRequests.Single().Status);
        var arrivals = harness.Cop.Units.Where(u => approved.FulfilledBy.Contains(u.Id)).ToList();
        Assert.Equal(2, arrivals.Count);
        Assert.All(arrivals, u => Assert.Equal(incident, u.AssignedIncidentId));
        Assert.All(arrivals, u => Assert.StartsWith("Relief ALS ambulance", u.Callsign));
    }

    [Fact]
    public async Task Mutual_aid_needs_regional_approval_judged_on_the_incident_priority()
    {
        var harness = Harness();
        var (medium, _) = await IncidentWithUnits(harness, 0, IncidentPriority.Medium);
        var (high, _) = await IncidentWithUnits(harness, 0, IncidentPriority.High);

        Assert.False((await harness.C2.RequestResourcesAsync(high, ResourceRequestKind.MutualAid, UnitType.Engine, 2)).Succeeded); // no justification
        var refused = (await harness.C2.RequestResourcesAsync(medium, ResourceRequestKind.MutualAid, UnitType.Engine, 2, "Fire spreading")).EntityId!.Value;
        var granted = (await harness.C2.RequestResourcesAsync(high, ResourceRequestKind.MutualAid, UnitType.Engine, 2, "Chemical store involved")).EntityId!.Value;

        await Step(harness, TimeSpan.FromMinutes(2));
        Assert.All(harness.Cop.ResourceRequests, r => Assert.Equal(ResourceRequestStatus.Requested, r.Status)); // still with the approver

        await Step(harness, TimeSpan.FromSeconds(40));
        var denied = harness.Cop.ResourceRequests.Single(r => r.Id == refused);
        var approved = harness.Cop.ResourceRequests.Single(r => r.Id == granted);
        Assert.Equal(ResourceRequestStatus.Denied, denied.Status);
        Assert.Contains("reserved for High or Critical", denied.DecisionReason);
        Assert.Equal(ResourceRequestStatus.Approved, approved.Status);
        Assert.Equal("Regional Duty Officer", approved.DecidedBy);

        await Step(harness, CommandResponseSystem.MutualAidLeadTime);
        Assert.Equal(ResourceRequestStatus.Fulfilled, harness.Cop.ResourceRequests.Single(r => r.Id == granted).Status);
        Assert.Contains(harness.Cop.Agencies, a => a.Name == "Kildare Fire Service");
        Assert.Equal(2, harness.Cop.Units.Count(u => u.Agency?.Name == "Kildare Fire Service" && u.AssignedIncidentId == high));
    }

    // ---- Approvals and notifications ----

    [Fact]
    public async Task Approval_requests_wait_for_command_and_the_requester_acknowledges_the_decision()
    {
        var harness = Harness();
        var approval = Guid.NewGuid();
        await harness.Perceived(new ApprovalRequested(approval, null, "Authority to evacuate", "200 residents downwind", "Garda Inspector"));

        await Step(harness, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        Assert.Single(harness.Cop.Alerts, a => a.Title == "Decision awaited: Authority to evacuate");

        Assert.True((await harness.C2.DecideApprovalAsync(approval, approve: true, "Use the community centre")).Succeeded);
        Assert.False((await harness.C2.DecideApprovalAsync(approval, approve: false)).Succeeded);
        await Step(harness, CommandResponseSystem.ApprovalReplyDelay + TimeSpan.FromSeconds(1));

        Assert.Equal(ApprovalStatus.Approved, harness.Cop.Approvals.Single().Status);
        var reply = Assert.Single(harness.Cop.Reports, r => r.SourceName == "Garda Inspector");
        Assert.Equal("Understood, approved. Proceeding now. (Use the community centre)", reply.Claim);
    }

    [Fact]
    public async Task Notified_organisations_reply_within_a_few_minutes()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 0);

        await harness.C2.NotifyAsync("St. James's Hospital", "Expect up to 10 casualties from a warehouse fire", incident);
        await Step(harness, TimeSpan.FromSeconds(55));
        Assert.Null(harness.Cop.Notifications.Single().Reply);

        await Step(harness, TimeSpan.FromSeconds(100));
        Assert.StartsWith("St. James's Hospital: acknowledged. Major emergency plan activated", harness.Cop.Notifications.Single().Reply);
    }

    [Theory]
    [InlineData("ESB Networks", "isolate supply")]
    [InlineData("Met Éireann", "wind expected")]
    [InlineData("Dublin City Council", "rest centre")]
    [InlineData("Someone else", "message received")]
    public void Replies_depend_on_who_was_notified(string recipient, string expected) =>
        Assert.Contains(expected, CommandResponseSystem.ReplyFrom(recipient));
}
