using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Documentation;
using Emergency_Response_Simulator.Simulation.Systems;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 18: documentation and ICS forms. Every form generates from the COP and the record; the decision log attributes
/// every order, request and approval to who gave it and why; IAP versions are compared and bound with their history; the
/// safety and medical plans carry the 215A analysis and the medical surge; situation reports draft themselves.
/// </summary>
public class FormsTests
{
    private static readonly CommandActor Ops = new(Guid.NewGuid(), UserRole.Operations, "Aoife Byrne");
    private static readonly CommandActor Ic = new(Guid.NewGuid(), UserRole.IncidentCommander, "Chief Murphy");

    private static async Task<FormBuilder> FormsAsync(TestHarness harness) => new(harness.Cop, await harness.EventsAsync());

    private static async Task<(Guid Incident, Guid Engine, Guid Ambulance)> IncidentAsync(TestHarness harness)
    {
        await harness.Perceived(new ParticipantJoined(Ops.ParticipantId, Ops.Name, Ops.Role));
        await harness.Perceived(new ParticipantJoined(Ic.ParticipantId, Ic.Name, Ic.Role));
        var incident = await harness.CreateIncidentAsync();
        var engine = await harness.RegisterUnitAsync("Engine 12");
        var ambulance = await harness.RegisterUnitAsync("Ambulance 14", UnitType.AmbulanceAls);
        await harness.Perceived(new HospitalRegistered(Guid.NewGuid(), "St. James's Hospital", GeoMath.Destination(Dublin, 270, 2000), 40, 20));
        using (CommandContext.As(Ops))
        {
            Assert.True((await harness.C2.DispatchAsync(engine, incident)).Succeeded);
            Assert.True((await harness.C2.DispatchAsync(ambulance, incident)).Succeeded);
        }
        return (incident, engine, ambulance);
    }

    /// <summary>A complete plan, approved by the IC: v1 of period 1.</summary>
    private static async Task<Guid> ApprovedPlanAsync(TestHarness harness, Guid incident, Guid engine)
    {
        var period = (await harness.Iap.StartOperationalPeriodAsync(incident, harness.Engine.SimTime, harness.Engine.SimTime.AddHours(2), "Initial response")).EntityId!.Value;
        var planId = (await harness.Iap.CreateDraftAsync(period, "Officer Kelly")).EntityId!.Value;
        var content = await harness.Iap.PullFromCopAsync(incident, harness.Cop.ActionPlans.Single(p => p.Id == planId).Content);
        content.Objectives = [new StrategicObjective
        {
            Statement = "Protect life",
            Operational = [new OperationalObjective { Statement = "Complete primary search", Responsible = "Fire Group", PerformanceTarget = "All floors searched",
                TargetTime = Start.AddMinutes(40), Tactics = [new TacticalTask { Description = "Search with BA crews" }] }],
        }];
        content.Organization = [new IapPosition { Role = IcsRole.IncidentCommander, Name = "Chief Murphy" }, new IapPosition { Role = IcsRole.Safety, Name = "Officer O'Connor" }];
        content.Groups = [new IapGroup { Name = "Fire Group", Kind = IcsGroupKind.Group, Supervisor = "SO Kelly" }];
        foreach (var assignment in content.Assignments)
            if (assignment.UnitId == engine)
                (assignment.Group, assignment.Assignment, assignment.ObjectiveId) = ("Fire Group", "Primary search", content.Objectives[0].Operational[0].Id);
            else
                assignment.Assignment = "Standby for casualties";
        content.Medical.Hospitals = [new ReceivingHospital { Name = "St. James's Hospital", TravelMinutes = 9 }];
        content.Safety.Hazards = [new SafetyHazard { Hazard = "Fire", Mitigation = "BA and full fire kit" }];
        content.Safety.Message ??= "Stay with your crew.";
        Assert.True((await harness.Iap.SaveDraftAsync(planId, content)).Succeeded);
        var submitted = await harness.Iap.SubmitAsync(planId, "Officer Kelly");
        Assert.True(submitted.Succeeded, submitted.Error);
        using (CommandContext.As(Ic))
            Assert.True((await harness.Iap.ApproveAsync(planId, "Chief Murphy")).Succeeded);
        return planId;
    }

    // ---- The forms ----

    [Fact]
    public async Task Every_form_generates_for_a_live_incident_as_text_and_HTML()
    {
        var harness = new TestHarness(new UnitResponseSystem());
        var (incident, engine, _) = await IncidentAsync(harness);
        var plan = await ApprovedPlanAsync(harness, incident, engine);
        await harness.RunAsync(TimeSpan.FromMinutes(3));

        var forms = await FormsAsync(harness);
        foreach (var kind in IcsForms.All)
        {
            var form = forms.Build(kind, incident);
            Assert.NotEmpty(form.Sections);
            Assert.Contains(form.Header, f => f.Label == "Incident" && f.Value.Contains("INC-00241"));
            var text = IcsForms.ToText(form);
            Assert.StartsWith(IcsForms.Number(kind), text);
            Assert.Contains("Prepared by", text);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(IcsForms.Title(kind)), IcsForms.ToHtml([form]));
        }

        // The IAP forms come from the plan in force.
        var ics204 = forms.Build(IcsFormKind.Ics204, incident);
        var fireGroup = Assert.Single(ics204.Sections, s => s.Title == "Fire Group");
        Assert.Contains(fireGroup.Table!.Rows, r => r[0] == "Engine 12" && r[1] == "Primary search" && r[2] == "1.1");
        Assert.Contains(forms.Build(IcsFormKind.Ics202, incident).Header, f => f.Label == "IAP version" && f.Value.Contains("APPROVED"));
        Assert.Equal(1 + IcsForms.IapForms.Count, forms.Package(incident, plan).Count);

        // The briefing form lists who was sent and when.
        var resources = forms.Build(IcsFormKind.Ics201, incident).Sections.Single(s => s.Title == "Resources").Table!;
        Assert.Contains(resources.Rows, r => r[0] == "Engine 12" && r[3] != "—");
    }

    [Fact]
    public async Task Without_a_plan_the_IAP_forms_say_so_and_the_others_come_from_the_COP()
    {
        var harness = new TestHarness();
        var (incident, _, _) = await IncidentAsync(harness);
        var forms = await FormsAsync(harness);
        Assert.Contains("No IAP has been drafted", IcsForms.ToText(forms.Build(IcsFormKind.Ics202, incident)));
        Assert.Contains("the organisation as the COP shows it now", IcsForms.ToText(forms.Build(IcsFormKind.Ics203, incident)));
        Assert.Contains("No air operations", IcsForms.ToText(forms.Build(IcsFormKind.Ics220, incident)));
        Assert.Contains(forms.Build(IcsFormKind.Ics205A, incident).Sections.Single().Table!.Rows, r => r[0] == "St. James's Hospital");
    }

    [Fact]
    public async Task Status_changes_and_check_in_come_from_the_record()
    {
        var harness = new TestHarness(new UnitResponseSystem());
        var (incident, engine, _) = await IncidentAsync(harness);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.FindUnit(engine)?.Status == UnitStatus.OnScene, TimeSpan.FromMinutes(10)));

        var forms = await FormsAsync(harness);
        var changes = forms.Build(IcsFormKind.Ics210, incident).Sections.Single().Table!;
        Assert.Contains(changes.Rows, r => r[1] == "Engine 12" && r[2] == "Dispatched" && r[4] == "Aoife Byrne");
        Assert.Contains(changes.Rows, r => r[1] == "Engine 12" && r[2] == "On scene" && r[4].StartsWith("crew"));
        var checkIn = forms.Build(IcsFormKind.Ics211, incident).Sections.Single().Table!;
        Assert.Contains(checkIn.Rows, r => r[1] == "Engine 12" && r[0] != "—");
    }

    // ---- The decision log ----

    [Fact]
    public async Task The_decision_log_attributes_every_order_request_and_approval_to_who_gave_it_and_why()
    {
        var harness = new TestHarness();
        var (incident, engine, _) = await IncidentAsync(harness);
        long orderSequence;
        using (CommandContext.As(Ops))
        {
            orderSequence = (await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Ventilate the roof")).EventSequence!.Value;
            Assert.True((await harness.C2.RecordDecisionAsync("Roof ventilation", "Smoke logging the stairwell; crews can't search", orderSequence)).Succeeded);
            Assert.True((await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.AdditionalResources, UnitType.Ladder, 1, "Aerial for the roof")).Succeeded);
        }
        await ApprovedPlanAsync(harness, incident, engine);
        await harness.Perceived(new ApprovalRequested(Guid.NewGuid(), incident, "Authority to evacuate Barrow Street", "Smoke drifting over the flats", "Garda Inspector Walsh"));

        var log = (await FormsAsync(harness)).DecisionLog(incident);
        var dispatch = Assert.Single(log, r => r.Kind == "Order" && r.Text.Contains("Engine 12") && r.Text.Contains("INC-00241") && r.Category.Contains("DISPATCH"));
        Assert.Equal(("Aoife Byrne", "Operations Section Chief"), (dispatch.Actor, dispatch.Role));
        var order = Assert.Single(log, r => r.Sequence == orderSequence);
        Assert.Equal(("Order", "Aoife Byrne", "Smoke logging the stairwell; crews can't search"), (order.Kind, order.Actor, order.Rationale));
        Assert.DoesNotContain(log, r => r.Text.Contains("Roof ventilation")); // the reason went on the order it explains
        Assert.Contains(log, r => r.Kind == "Request" && r.Actor == "Aoife Byrne" && r.Text.Contains("Ladder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log, r => r.Kind == "Approval" && r.Actor == "Chief Murphy" && r.Role == "Incident Commander");
        Assert.Contains(log, r => r.Kind == "Request" && r.Actor == "Garda Inspector Walsh" && r.Text.StartsWith("Authority to evacuate"));
        Assert.All(log.Zip(log.Skip(1)), p => Assert.True(p.First.At <= p.Second.At));
        Assert.DoesNotContain(log, r => r.Text.Contains("draft saved", StringComparison.OrdinalIgnoreCase)); // bookkeeping isn't a decision

        // One person's activity log (ICS 214) is theirs alone.
        var forms = await FormsAsync(harness);
        var activity = forms.Build(IcsFormKind.Ics214, incident, participantId: Ops.ParticipantId);
        var rows = activity.Sections.Single(s => s.Title == "Activity log").Table!.Rows;
        Assert.Contains(rows, r => r[1].Contains("Ventilate the roof") && r[2].StartsWith("Smoke logging"));
        Assert.DoesNotContain(rows, r => r[1].Contains("approved", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Aoife Byrne (Operations Section Chief)", activity.PreparedBy);
        Assert.Contains(forms.Build(IcsFormKind.Ics214, incident).Sections, s => s.Title.StartsWith("Chief Murphy"));
    }

    // ---- IAP versions ----

    [Fact]
    public async Task IAP_versions_are_compared_and_the_package_binds_the_history_and_the_changes()
    {
        var harness = new TestHarness();
        var (incident, engine, ambulance) = await IncidentAsync(harness);
        var v1 = await ApprovedPlanAsync(harness, incident, engine);
        var period = harness.Cop.ActionPlans.Single(p => p.Id == v1).OperationalPeriodId;

        var v2 = (await harness.Iap.CreateDraftAsync(period, "Officer Kelly")).EntityId!.Value;
        var content = harness.Cop.ActionPlans.Single(p => p.Id == v2).Content.Clone();
        content.Objectives[0].Operational.Add(new OperationalObjective { Statement = "Treat and transport casualties", Responsible = "Medical Group",
            PerformanceTarget = "All P1s away", Tactics = [new TacticalTask { Description = "Casualty clearing" }] });
        content.Assignments.Single(a => a.UnitId == engine).Assignment = "Exposure protection";
        content.Safety.Hazards.Add(new SafetyHazard { Hazard = "Roof collapse", Mitigation = "Defensive from the east side" });
        content.Groups.Add(new IapGroup { Name = "Medical Group", Kind = IcsGroupKind.Group });
        var changes = IapVersions.Compare(harness.Cop.ActionPlans.Single(p => p.Id == v1).Content, content);
        Assert.Contains("Objective 1.2 added: Treat and transport casualties", changes);
        Assert.Contains("Engine 12 reassigned: Exposure protection (Fire Group)", changes);
        Assert.Contains("New hazard: Roof collapse", changes);
        Assert.Contains("Medical Group formed", changes);
        Assert.Empty(IapVersions.Compare(content, content.Clone()));

        Assert.True((await harness.Iap.SaveDraftAsync(v2, content)).Succeeded);
        Assert.True((await harness.Iap.SubmitAsync(v2, "Officer Kelly")).Succeeded);
        using (CommandContext.As(Ic))
            Assert.True((await harness.Iap.ApproveAsync(v2, "Chief Murphy")).Succeeded);

        var cover = (await FormsAsync(harness)).Package(incident).First();
        var history = cover.Sections.Single(s => s.Title == "Version history").Text!;
        Assert.Contains("v1: drafted", history);
        Assert.Contains("superseded", history);
        Assert.Contains("v2: drafted", history);
        Assert.Contains("approved", history.Split(Environment.NewLine)[1]);
        Assert.Contains("Objective 1.2 added", cover.Sections.Single(s => s.Title == "Changes from v1").Text);
        _ = ambulance;
    }

    // ---- Safety and medical plans in the IAP ----

    [Fact]
    public async Task The_safety_and_medical_plans_carry_the_215A_analysis_and_the_medical_surge()
    {
        var harness = new TestHarness();
        var (incident, _, _) = await IncidentAsync(harness);
        var hospital = harness.Cop.Hospitals.Single().Id;
        Assert.True((await harness.C2.SetTransportDestinationAsync(incident, Specialty.Burns, hospital)).Succeeded);
        Assert.True((await harness.C2.OpenTemporaryMortuaryAsync("Irishtown Stadium", GeoMath.Destination(Dublin, 90, 1500), incident)).Succeeded);
        Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.HotZone, "Warehouse hot zone", Wgs84.Circle(Dublin, 60), incident)).Succeeded);

        var content = new IapContent { Groups = [new IapGroup { Name = "Fire Group" }, new IapGroup { Name = "Medical Group" }] };
        content = await harness.Iap.PullFromCopAsync(incident, content, IapPullParts.Medical | IapPullParts.Safety);

        Assert.Contains("Ambulance 14 (", Assert.Single(content.Medical.MedicalUnits));
        Assert.Equal("Burns → St. James's Hospital", Assert.Single(content.Medical.PatientDestinations));
        Assert.Equal("Irishtown Stadium", content.Medical.Mortuary);
        Assert.Equal(["Fire Group", "Medical Group"], content.Safety.Analysis.Select(r => r.Area));
        Assert.All(content.Safety.Analysis, r => Assert.Contains("Hot zone: Warehouse hot zone", r.Hazards));
        Assert.Contains("Contaminated or distressed casualties", content.Safety.Analysis[1].Hazards);
        Assert.All(content.Safety.Analysis, r => Assert.False(string.IsNullOrWhiteSpace(r.Mitigations)));

        // The document shows them; the compliance check wants every group covered.
        var document = IapDocument.Render(new IncidentActionPlan { Content = content });
        Assert.Contains("SAFETY ANALYSIS (ICS 215A)", document);
        Assert.Contains("Patients: Burns → St. James's Hospital", document);
        content.Groups.Add(new IapGroup { Name = "Search Group" });
        Assert.Contains(IapCompliance.Check(content), i => i is { Severity: IssueSeverity.Warning, Section: IapSection.Safety } && i.Message.Contains("doesn't cover Search Group"));
    }

    // ---- Situation reports ----

    [Fact]
    public async Task A_situation_report_drafts_itself_and_the_ones_sent_are_on_record()
    {
        var harness = new TestHarness();
        var (incident, _, _) = await IncidentAsync(harness);
        Assert.True((await harness.C2.UpdateIncidentAsync(incident, casualtiesReported: 3, threats: ["Roof collapse"])).Succeeded);
        await harness.Perceived(new EocLevelReached(EocLevel.Partial));

        var draft = FormBuilder.DraftSitrep(harness.Cop);
        Assert.Contains("INC-00241", draft);
        Assert.Contains("Casualties: 3 reported", draft);
        Assert.Contains("Threats: Roof collapse", draft);
        Assert.Contains("Resources: 2 committed", draft);

        using (CommandContext.As(Ic))
            Assert.True((await harness.C2.SubmitSitrepAsync(draft)).Succeeded);
        var sitrep = (await FormsAsync(harness)).Build(IcsFormKind.Sitrep, incident);
        Assert.Contains(sitrep.Sections.Single(s => s.Title == "Sent to the EOC").Table!.Rows, r => r[1] == "Chief Murphy" && r[2].Contains("INC-00241"));
        var status = (await FormsAsync(harness)).Build(IcsFormKind.Ics209, incident);
        Assert.Contains(status.Sections.Single(s => s.Title == "Casualties").Fields!, f => f.Label == "Reported / confirmed" && f.Value == "3 / 0");
    }
}
