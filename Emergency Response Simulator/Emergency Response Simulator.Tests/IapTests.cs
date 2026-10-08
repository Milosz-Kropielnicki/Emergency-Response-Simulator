using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 5: the IAP builder, compliance checks, operational periods, approval and briefing.</summary>
public class IapTests
{
    private static TestHarness Harness(AttentionOptions? options = null) =>
        new(cop => [new CommandResponseSystem(), new AttentionMonitor(cop, options ?? new AttentionOptions())]);

    private static async Task<(Guid Incident, List<Guid> Units)> IncidentWithUnits(TestHarness harness, int count)
    {
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, TestHarness.Dublin,
            "Barrow Street", "Barrow Street fire")).EntityId!.Value;
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
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(5))
            await harness.Engine.StepAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Fills in everything an IAP needs on top of what was pulled from the COP.</summary>
    private static IapContent Complete(IapContent content, string fireGroupSupervisor = "SO Kelly")
    {
        var plan = content.Clone();
        var search = new OperationalObjective
        {
            Statement = "Complete primary search of the affected building",
            Responsible = "Fire Group",
            PerformanceTarget = "All floors searched",
            TargetTime = TestHarness.Start.AddMinutes(40),
            Tactics = [new TacticalTask { Description = "Primary search" }],
        };
        var exposures = new OperationalObjective
        {
            Statement = "Protect exposures on all sides",
            PerformanceTarget = "No spread to adjacent buildings",
            Tactics = [new TacticalTask { Description = "Exposure protection" }],
        };
        plan.Objectives = [new StrategicObjective { Statement = "Protect life", Operational = [search] },
                           new StrategicObjective { Statement = "Prevent fire spread to adjacent structures", Operational = [exposures] }];
        plan.Organization = [new IapPosition { Role = IcsRole.IncidentCommander, Name = "Chief Murphy" },
                             new IapPosition { Role = IcsRole.Operations, Name = "DC Byrne" },
                             new IapPosition { Role = IcsRole.Safety, Name = "Officer O'Connor" }];
        plan.Groups = [new IapGroup { Name = "Fire Group", Kind = IcsGroupKind.Group, Supervisor = fireGroupSupervisor }];
        for (var i = 0; i < plan.Assignments.Count; i++)
        {
            plan.Assignments[i].Assignment = i == 0 ? "Primary search" : "Exposure protection";
            plan.Assignments[i].Group = "Fire Group";
            plan.Assignments[i].ObjectiveId = i == 0 ? search.Id : exposures.Id;
        }
        plan.Communications.Channels.Add(new RadioChannel { Function = "Tactical", Channel = "FIRE TAC 3", AssignedTo = "Fire Group" });
        plan.Medical.Hospitals = [new ReceivingHospital { Name = "St. James's Hospital", TravelMinutes = 9 }];
        plan.Safety.Hazards = [new SafetyHazard { Hazard = "Fire", Mitigation = "BA and full fire kit" }];
        return plan;
    }

    private static async Task<(Guid Period, Guid Plan)> DraftAsync(TestHarness harness, Guid incident, int minutes = 60)
    {
        var period = (await harness.Iap.StartOperationalPeriodAsync(incident, harness.Engine.SimTime,
            harness.Engine.SimTime.AddMinutes(minutes), "Initial response")).EntityId!.Value;
        var plan = (await harness.Iap.CreateDraftAsync(period, "Officer Kelly")).EntityId!.Value;
        return (period, plan);
    }

    private static IncidentActionPlan Plan(TestHarness harness, Guid planId) => harness.Cop.ActionPlans.Single(p => p.Id == planId);

    private static async Task<Guid> ApprovedPlanAsync(TestHarness harness, Guid incident)
    {
        var (_, planId) = await DraftAsync(harness, incident);
        Assert.True((await harness.Iap.SaveDraftAsync(planId, Complete(Plan(harness, planId).Content))).Succeeded);
        Assert.True((await harness.Iap.SubmitAsync(planId, "Officer Kelly")).Succeeded);
        Assert.True((await harness.Iap.ApproveAsync(planId, "Chief Murphy")).Succeeded);
        return planId;
    }

    // ---- Compliance ----

    [Fact]
    public void An_empty_plan_fails_every_mandatory_section()
    {
        var issues = IapCompliance.Check(new IapContent());
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Section).ToHashSet();

        Assert.Equal(new HashSet<IapSection> { IapSection.Objectives, IapSection.Organisation, IapSection.Communications,
            IapSection.Medical, IapSection.Safety }, errors);
    }

    [Fact]
    public void Objectives_must_break_down_from_strategic_to_operational_and_be_measurable()
    {
        var plan = new IapContent
        {
            Objectives =
            [
                new StrategicObjective { Statement = "Protect life" },
                new StrategicObjective
                {
                    Statement = "Evacuate exposed civilians",
                    Operational = [new OperationalObjective { Statement = "Evacuate all civilians within the projected hazard area" }],
                },
            ],
        };

        var issues = IapCompliance.Check(plan);

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("Objective 1 (Protect life) has no operational objectives"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("2.1") && i.Message.Contains("no performance target"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("2.1") && i.Message.Contains("no tactics"));
    }

    [Fact]
    public async Task Compliance_compares_the_plan_with_the_COP()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 3);
        var (_, planId) = await DraftAsync(harness, incident);
        var plan = Complete(Plan(harness, planId).Content);
        plan.Assignments.RemoveAt(2); // Engine 3 left out
        await harness.C2.UpdateUnitStatusAsync(units[0], UnitStatus.OutOfService);

        var issues = IapCompliance.Check(plan, null, harness.Cop.FindIncident(incident));

        Assert.Contains(issues, i => i.Message.Contains("Engine 3 is committed to") && i.Message.Contains("no assignment"));
        Assert.Contains(issues, i => i.Message.Contains("Engine 1 is no longer committed") && i.Message.Contains("not viable"));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Plan_span_of_control_is_checked_before_it_is_briefed()
    {
        var plan = new IapContent
        {
            Organization = [new IapPosition { Role = IcsRole.IncidentCommander, Name = "Chief Murphy" }],
            Assignments = Enumerable.Range(1, 9).Select(i => new IapAssignment { UnitId = Guid.NewGuid(), Callsign = $"Engine {i}", Assignment = "Fire attack" }).ToList(),
        };

        Assert.Contains(IapCompliance.Check(plan), i => i.Section == IapSection.Organisation && i.Message.Contains("Incident Commander would have 9 direct reports"));
    }

    [Theory]
    [InlineData("Mater Misericordiae University Hospital", true, "major trauma centre")]
    [InlineData("St. James's Hospital", true, "Burns")]
    [InlineData("The National Maternity Hospital", false, null)]
    [InlineData("Dublin Dental University Hospital", false, null)]
    [InlineData("Royal Hospital Donnybrook", false, null)]
    public void Only_emergency_hospitals_receive_casualties(string name, bool receiving, string? capability)
    {
        Assert.Equal(receiving, IapTemplates.IsReceivingHospital(name));
        if (capability is null)
            Assert.Null(IapTemplates.HospitalCapabilities(name));
        else
            Assert.Contains(capability, IapTemplates.HospitalCapabilities(name));
    }

    // ---- Operational periods ----

    [Fact]
    public async Task Operational_periods_are_numbered_validated_and_can_start_early()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 1);
        var start = harness.Engine.SimTime;

        Assert.False((await harness.Iap.StartOperationalPeriodAsync(incident, start, start.AddMinutes(5))).Succeeded);
        Assert.False((await harness.Iap.StartOperationalPeriodAsync(incident, start, start.AddHours(30))).Succeeded);
        var first = (await harness.Iap.StartOperationalPeriodAsync(incident, start, start.AddHours(1), "Initial response")).EntityId!.Value;
        Assert.False((await harness.Iap.StartOperationalPeriodAsync(incident, start, start.AddHours(2))).Succeeded);

        var second = (await harness.Iap.StartOperationalPeriodAsync(incident, start.AddMinutes(45), start.AddMinutes(105))).EntityId!.Value;

        var periods = harness.Cop.PeriodsOf(incident);
        Assert.Equal([1, 2], periods.Select(p => p.Number));
        Assert.Equal(start.AddMinutes(45), periods.Single(p => p.Id == first).End); // cut short by the early start
        Assert.Equal(second, harness.Cop.CurrentPeriod(incident, start.AddMinutes(50))!.Id);
        Assert.Equal(first, harness.Cop.CurrentPeriod(incident, start.AddMinutes(10))!.Id);
    }

    // ---- Drafting and the approval workflow ----

    [Fact]
    public async Task A_first_draft_is_pulled_from_the_COP()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 2);
        await harness.C2.AssignIncidentCommanderAsync(incident, "Chief Murphy");
        var group = (await harness.C2.FormGroupAsync(incident, "Fire Group", IcsGroupKind.Group, "SO Kelly")).EntityId!.Value;
        await harness.C2.AssignUnitToGroupAsync(units[0], group);
        await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, units[1], "Exposure protection on Grand Canal Street");
        await harness.C2.UpdateIncidentAsync(incident, threats: ["Fire", "Possible chemical storage"]);

        var (_, planId) = await DraftAsync(harness, incident);
        var plan = Plan(harness, planId);
        var content = plan.Content;

        Assert.Equal(1, plan.Version);
        Assert.Equal(IapStatus.Draft, plan.Status);
        Assert.Equal("Officer Kelly", plan.PreparedBy);
        Assert.Contains("Barrow Street fire", content.SituationSummary);
        Assert.Contains("Possible chemical storage", content.SituationSummary);
        Assert.Equal("Chief Murphy", content.NameFor(IcsRole.IncidentCommander));
        Assert.Equal("SO Kelly", Assert.Single(content.Groups).Supervisor);
        Assert.Equal("Fire Group", content.Assignments.Single(a => a.UnitId == units[0]).Group);
        Assert.Equal("Exposure protection on Grand Canal Street", content.Assignments.Single(a => a.UnitId == units[1]).Assignment);
        Assert.Contains(content.Communications.Channels, c => c.Function == "Command");
        Assert.Contains(content.Communications.Channels, c => c.AssignedTo == "Fire Group");
        Assert.Contains(content.Safety.Hazards, h => h.Hazard == "Possible chemical storage" && h.Mitigation!.Contains("upwind"));
        Assert.Contains("Chemical protective suits for hazmat entry", content.Safety.Ppe);
        Assert.False(string.IsNullOrWhiteSpace(content.Safety.Message));
    }

    [Fact]
    public async Task Plans_go_from_draft_to_approved_through_submission_and_return()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 2);
        var (_, planId) = await DraftAsync(harness, incident);

        // Incomplete: submission refused with the first compliance error.
        var refused = await harness.Iap.SubmitAsync(planId, "Officer Kelly");
        Assert.False(refused.Succeeded);
        Assert.Contains("compliance error", refused.Error);

        Assert.True((await harness.Iap.SaveDraftAsync(planId, Complete(Plan(harness, planId).Content))).Succeeded);
        Assert.Equal("Nothing changed.", (await harness.Iap.SaveDraftAsync(planId, Plan(harness, planId).Content)).Error);
        Assert.True((await harness.Iap.SubmitAsync(planId, "Officer Kelly")).Succeeded);
        Assert.Equal(IapStatus.PendingApproval, Plan(harness, planId).Status);

        // Locked while with the approver.
        Assert.False((await harness.Iap.SaveDraftAsync(planId, new IapContent())).Succeeded);
        Assert.False((await harness.Iap.ReturnAsync(planId, "Chief Murphy", " ")).Succeeded);

        Assert.True((await harness.Iap.ReturnAsync(planId, "Chief Murphy", "Add a decontamination tactic")).Succeeded);
        var returned = Plan(harness, planId);
        Assert.Equal(IapStatus.Draft, returned.Status);
        Assert.Equal("Add a decontamination tactic", returned.ReturnComments);

        Assert.True((await harness.Iap.SubmitAsync(planId, "Officer Kelly")).Succeeded);
        Assert.Null(Plan(harness, planId).ReturnComments);
        Assert.True((await harness.Iap.ApproveAsync(planId, "Chief Murphy")).Succeeded);

        var approved = Plan(harness, planId);
        Assert.Equal(IapStatus.Approved, approved.Status);
        Assert.Equal("Chief Murphy", approved.ApprovedBy);
        Assert.Same(approved, harness.Cop.PlanInForce(incident, harness.Engine.SimTime));
    }

    [Fact]
    public async Task Revising_an_approved_plan_creates_a_new_version_that_supersedes_it()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 2);
        var v1 = await ApprovedPlanAsync(harness, incident);
        var period = Plan(harness, v1).OperationalPeriodId;

        Assert.False((await harness.Iap.SaveDraftAsync(v1, new IapContent())).Succeeded); // approved plans are read-only

        var v2 = (await harness.Iap.CreateDraftAsync(period, "Officer Kelly")).EntityId!.Value;
        Assert.False((await harness.Iap.CreateDraftAsync(period)).Succeeded); // one open version at a time
        var revised = Plan(harness, v2).Content.Clone();
        Assert.Equal(Plan(harness, v1).Content.Objectives.Count, revised.Objectives.Count); // copied from v1
        revised.CommandersIntent = "Hold the fire to the building of origin; nobody enters without BA.";
        await harness.Iap.SaveDraftAsync(v2, revised);
        await harness.Iap.SubmitAsync(v2, "Officer Kelly");

        // v1 stays in force until v2 is approved.
        Assert.Equal(v1, harness.Cop.ApprovedPlan(period)!.Id);
        await harness.Iap.ApproveAsync(v2, "Chief Murphy");

        Assert.Equal(2, Plan(harness, v2).Version);
        Assert.Equal(v1, Plan(harness, v2).BasedOnId);
        Assert.Equal(IapStatus.Superseded, Plan(harness, v1).Status);
        Assert.Equal(v2, harness.Cop.ApprovedPlan(period)!.Id);
    }

    // ---- Briefing: push the plan to the COP ----

    [Fact]
    public async Task Briefing_staffs_positions_forms_groups_and_orders_each_unit()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 3);
        var planId = await ApprovedPlanAsync(harness, incident);

        var result = await harness.Iap.BriefAsync(planId);

        Assert.True(result.Succeeded);
        Assert.Contains("3 order(s) issued", result.Detail);
        var command = harness.Cop.FindIncident(incident)!.Command;
        Assert.Equal("DC Byrne", command.Positions[IcsRole.Operations]);
        var group = Assert.Single(command.Groups);
        Assert.Equal("Fire Group", group.Name);
        Assert.Equal(units.ToHashSet(), group.UnitIds.ToHashSet());
        Assert.Contains(harness.Cop.Orders, o => o.TargetId == units[0] && o.Text == "IAP period 1: Primary search, under Fire Group");
        Assert.NotNull(Plan(harness, planId).BriefedAt);
    }

    [Fact]
    public async Task Briefing_a_revision_only_orders_changed_assignments_and_skips_lost_units()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 3);
        var v1 = await ApprovedPlanAsync(harness, incident);
        await harness.Iap.BriefAsync(v1);
        var reserve = await harness.RegisterUnitAsync("Engine 9");

        // Engine 2 breaks down; the revision gives its task to the reserve engine (§8.6).
        await harness.C2.UpdateUnitStatusAsync(units[1], UnitStatus.OutOfService);
        var v2 = (await harness.Iap.CreateDraftAsync(Plan(harness, v1).OperationalPeriodId)).EntityId!.Value;
        var revised = Plan(harness, v2).Content.Clone();
        revised.Assignments.Add(new IapAssignment { UnitId = reserve, Callsign = "Engine 9", Assignment = "Exposure protection", Group = "Fire Group" });
        await harness.Iap.SaveDraftAsync(v2, revised);
        await harness.Iap.SubmitAsync(v2, "Officer Kelly");
        await harness.Iap.ApproveAsync(v2, "Chief Murphy");
        var before = harness.Cop.Orders.Count;

        var result = await harness.Iap.BriefAsync(v2);

        Assert.Contains("1 order(s) issued", result.Detail);
        Assert.Contains("1 unit(s) dispatched", result.Detail);
        Assert.Contains("Engine 2 (out of service)", result.Detail);
        Assert.Equal(before + 1, harness.Cop.Orders.Count);
        Assert.Equal(incident, harness.Cop.FindUnit(reserve)!.AssignedIncidentId);
    }

    // ---- Reassessment and the next period ----

    [Fact]
    public async Task The_next_period_carries_forward_unfinished_objectives_only()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 2);
        var v1 = await ApprovedPlanAsync(harness, incident);
        var first = Plan(harness, v1);
        var search = first.Content.Objectives[0].Operational[0];
        var exposures = first.Content.Objectives[1].Operational[0];
        Assert.True((await harness.Iap.SetObjectiveStatusAsync(incident, search.Id, ObjectiveStatus.Achieved)).Succeeded);
        Assert.Equal("Nothing changed.", (await harness.Iap.SetObjectiveStatusAsync(incident, search.Id, ObjectiveStatus.Achieved)).Error);

        var findings = IapReassessment.Findings(first, harness.Cop.FindIncident(incident)!, harness.Cop.ObjectiveProgress,
            harness.Cop.Zones, harness.Engine.SimTime);
        Assert.Contains("1 of 2 operational objective(s) achieved.", findings);
        Assert.Contains(findings, f => f.StartsWith("Open: 2.1 Protect exposures"));

        var start = first.OperationalPeriod!.End;
        var period2 = (await harness.Iap.StartOperationalPeriodAsync(incident, start, start.AddHours(1), "Suppression")).EntityId!.Value;
        var next = Plan(harness, (await harness.Iap.CreateDraftAsync(period2)).EntityId!.Value);

        Assert.Equal(v1, next.BasedOnId);
        Assert.Equal(1, next.Version);
        var carried = Assert.Single(next.Content.OperationalObjectives);
        Assert.Equal(exposures.Id, carried.Id);   // same objective, so progress tracking continues
        Assert.Equal("Prevent fire spread to adjacent structures", Assert.Single(next.Content.Objectives).Statement);
        Assert.DoesNotContain(next.Content.Assignments, a => a.ObjectiveId == search.Id);
    }

    // ---- Attention: the plan against reality ----

    [Fact]
    public async Task Command_is_alerted_when_reality_diverges_from_the_plan()
    {
        var harness = Harness();
        var (incident, units) = await IncidentWithUnits(harness, 2);
        var planId = await ApprovedPlanAsync(harness, incident);
        await harness.Iap.BriefAsync(planId);

        await harness.C2.UpdateUnitStatusAsync(units[1], UnitStatus.OutOfService);
        await Step(harness, TimeSpan.FromSeconds(10));
        var alert = Assert.Single(harness.Cop.Alerts, a => a.Title == "Plan not viable: Engine 2 is out of service");
        Assert.Equal(AlertCategory.Planning, alert.Category);
        Assert.Contains("IAP: Engine 2 → Exposure protection", alert.Message);

        // The search objective's target (start + 40 min) passes while still open.
        await Step(harness, TimeSpan.FromMinutes(41));
        Assert.Single(harness.Cop.Alerts, a => a.Title == "Objective overdue: 1.1 Complete primary search of the affected building");

        // Ten minutes before the hour-long period ends.
        await Step(harness, TimeSpan.FromMinutes(10));
        Assert.Single(harness.Cop.Alerts, a => a.Title.Contains("operational period 1 ends at"));
    }

    [Fact]
    public async Task Command_is_reminded_to_plan_and_to_approve()
    {
        var harness = Harness(new AttentionOptions { PlanningExpectedAfter = TimeSpan.FromMinutes(5), PlanApprovalGrace = TimeSpan.FromMinutes(5) });
        var (incident, _) = await IncidentWithUnits(harness, 4);

        await Step(harness, TimeSpan.FromMinutes(6));
        Assert.Single(harness.Cop.Alerts, a => a.Title.EndsWith("no Incident Action Plan"));

        var (_, planId) = await DraftAsync(harness, incident);
        await harness.Iap.SaveDraftAsync(planId, Complete(Plan(harness, planId).Content));
        await harness.Iap.SubmitAsync(planId, "Officer Kelly");
        await Step(harness, TimeSpan.FromMinutes(6));

        Assert.Single(harness.Cop.Alerts, a => a.Title.EndsWith("IAP version 1 awaiting approval"));
        Assert.Single(harness.Cop.Alerts, a => a.Title.EndsWith("no approved IAP for period 1"));
    }

    // ---- Events, replay and the document ----

    [Fact]
    public async Task Plans_round_trip_through_JSON_and_rebuild_from_the_event_stream()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 2);
        var planId = await ApprovedPlanAsync(harness, incident);
        var events = new List<SimEvent>();
        await foreach (var simEvent in harness.Store.ReadAsync(harness.Engine.SessionId))
            events.Add(simEvent);

        var created = events.Select(e => e.Payload).OfType<IapDraftSaved>().Single();
        var copy = (IapDraftSaved)EventJson.Deserialize(EventJson.Serialize(created));
        Assert.Equal(created.Content.Objectives[0].Operational[0].Id, copy.Content.Objectives[0].Operational[0].Id);
        Assert.Equal(created.Content.Objectives[0].Operational[0].TargetTime, copy.Content.Objectives[0].Operational[0].TargetTime);

        var replayed = new PerceivedState();
        foreach (var simEvent in events)
            replayed.Apply(simEvent);
        var plan = replayed.ActionPlans.Single(p => p.Id == planId);
        Assert.Equal(IapStatus.Approved, plan.Status);
        Assert.Equal("Chief Murphy", plan.ApprovedBy);
        Assert.Equal(2, plan.Content.Assignments.Count);
    }

    [Fact]
    public async Task The_document_reads_like_an_IAP()
    {
        var harness = Harness();
        var (incident, _) = await IncidentWithUnits(harness, 2);
        var plan = Plan(harness, await ApprovedPlanAsync(harness, incident));

        var text = IapDocument.Render(plan, harness.Cop.FindIncident(incident));

        Assert.Contains("OPERATIONAL PERIOD 1:", text);
        Assert.Contains("APPROVED by Chief Murphy", text);
        Assert.Contains("1. Protect life", text);
        Assert.Contains("1.1 Complete primary search of the affected building", text);
        Assert.Contains("Engine 1 → Primary search  [Fire Group · objective 1.1]", text);
        Assert.Contains("Operations Section Chief: DC Byrne", text);
        Assert.Contains("Hospital: St. James's Hospital (9 min)", text);
        Assert.Contains("SAFETY MESSAGE:", text);
    }
}
