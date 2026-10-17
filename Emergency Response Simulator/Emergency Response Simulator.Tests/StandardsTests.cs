using System.Xml.Linq;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.Standards;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 20: data standards and NIMS. CAP, EDXL (DE, HAVE, RM, SitRep), NIEM and HL7 v2 export from the COP and import
/// as information received; the NIMS/ICS check finds gaps in command, organisation, forms and plain language.
/// </summary>
public class StandardsTests
{
    private static IntegrationService Integration(TestHarness harness) => new(harness.Cop, harness.Engine, harness.Engine);

    private static XElement Xml(StandardDocument document) => XElement.Parse(document.Content);

    [Fact]
    public async Task An_evacuation_zone_exports_as_a_CAP_exercise_alert_that_reads_back()
    {
        var harness = new TestHarness();
        var incident = await harness.CreateIncidentAsync();
        var ring = Wgs84.Circle(Dublin, 400, 12);
        Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Ringsend evacuation", ring, incident)).Succeeded);

        var document = await Integration(harness).ExportAsync(DataStandards.Cap);
        Assert.Equal("application/cap+xml", document.MediaType);
        var alert = Cap.Read(document.Content[document.Content.IndexOf('<', 1)..]); // after the XML declaration

        Assert.Equal(Cap.ExerciseStatus, alert.Status); // never Actual: it comes from a simulation
        var info = Assert.Single(alert.Info);
        Assert.Equal("Evacuate", info.ResponseType);
        Assert.Equal("Safety", info.Category);
        var polygon = Assert.Single(Assert.Single(info.Areas).Polygons);
        var points = Cap.ParsePoints(polygon);
        Assert.True(points.Count >= 4);
        Assert.Equal(points[0], points[^1]); // CAP polygons are closed
        Assert.Equal(alert, Cap.Read(Cap.Write(alert)) with { Info = alert.Info }); // round trip
    }

    [Fact]
    public async Task An_outside_CAP_warning_becomes_a_report_and_an_alert_once()
    {
        var harness = new TestHarness();
        var integration = Integration(harness);
        const string warning = """
            <alert xmlns="urn:oasis:names:tc:emergency:cap:1.2">
              <identifier>ME-2026-1008-0042</identifier><sender>met.ie</sender><sent>2026-10-08T13:30:00+00:00</sent>
              <status>Exercise</status><msgType>Alert</msgType><scope>Public</scope>
              <info><category>Met</category><event>Wind warning</event><urgency>Expected</urgency><severity>Moderate</severity>
                <certainty>Likely</certainty><senderName>Met Éireann</senderName><headline>Status Orange wind warning for Dublin</headline>
                <instruction>Secure loose objects.</instruction>
                <area><areaDesc>Dublin</areaDesc><circle>53.344,-6.26 15</circle></area></info>
            </alert>
            """;

        var result = await integration.ImportAsync(DataStandards.Cap, warning);
        Assert.True(result.Succeeded, result.Message);
        var report = Assert.Single(harness.Cop.Reports);
        Assert.Equal("Met Éireann", report.SourceName);
        Assert.Contains("Status Orange wind warning", report.Claim);
        Assert.Equal(53.344, report.Location!.Y, 3);
        var alert = Assert.Single(harness.Cop.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(AlertCategory.Hazard, alert.Category);
        Assert.All(await harness.EventsAsync(), e => Assert.True(e.Visibility == EventVisibility.Perceived));

        var again = await integration.ImportAsync(DataStandards.Cap, warning);
        Assert.Equal(0, again.Accepted);
        Assert.Contains(again.Notes, n => n.Contains("already received"));
        Assert.False((await integration.ImportAsync(DataStandards.Cap, "<alert>not CAP</alert>")).Succeeded);
    }

    [Fact]
    public async Task HAVE_carries_hospital_availability_both_ways()
    {
        var harness = new TestHarness();
        var hospital = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "St. James's Hospital", Dublin, 40, 20));
        await harness.Perceived(new HospitalResourcesReported(hospital, [new SpecialtyBeds(Specialty.Burns, 6, 2)], 30, 0, 0));
        var integration = Integration(harness);

        var exported = EdxlHave.Read(Xml(await integration.ExportAsync(DataStandards.EdxlHave)));
        var have = Assert.Single(exported);
        Assert.Equal("Normal", have.EmsTraffic);
        Assert.Equal((20, 40), have.Beds[EdxlHave.EmergencyDepartment]);
        Assert.Equal((4, 6), have.Beds["Burn"]);

        // The hospital's own system reports it is closed to ambulances, with two ED places and no burns beds left;
        // a hospital not yet on the picture reports in with its location.
        var update = EdxlHave.Write(
        [
            have with { EmsTraffic = "Closed", Reason = "ED full", Beds = new Dictionary<string, (int, int)> { [EdxlHave.EmergencyDepartment] = (2, 40), ["Burn"] = (0, 6) } },
            new HaveHospital("", "Tallaght University Hospital", GeoMath.Destination(Dublin, 225, 8000), "Normal", null,
                new Dictionary<string, (int, int)> { [EdxlHave.EmergencyDepartment] = (12, 30) }, Start),
        ]);
        var result = await integration.ImportAsync(DataStandards.EdxlHave, update.ToString());
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Accepted);

        var james = harness.Cop.Hospitals.Single(h => h.Id == hospital);
        Assert.True(james.OnDiversion);
        Assert.Equal(38, james.Occupied);
        Assert.Equal(0, harness.Cop.Health.Hospitals[hospital].Beds(Specialty.Burns)!.Free);
        Assert.Equal(30, harness.Cop.Health.Hospitals[hospital].BloodUnits); // what HAVE doesn't carry is kept
        var tallaght = harness.Cop.Hospitals.Single(h => h.Name == "Tallaght University Hospital");
        Assert.Equal(12, tallaght.Available);
    }

    [Fact]
    public async Task EDXL_RM_and_SitRep_carry_requests_typed_to_NIMS_and_the_situation()
    {
        var harness = new TestHarness();
        var incident = await harness.CreateIncidentAsync();
        Assert.True((await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.MutualAid, UnitType.Engine, 3, "Exposure protection")).Succeeded);
        var integration = Integration(harness);

        var rm = Xml(await integration.ExportAsync(DataStandards.EdxlRm));
        Assert.Equal(EdxlRm.Msg + "RequestResource", rm.Name);
        Assert.Equal("Engine, Fire, Type 1", rm.Descendants(EdxlRm.Rm + "Value").Single().Value);
        Assert.Equal("3", rm.Descendants(EdxlRm.Rm + "Amount").Single().Value);
        Assert.Equal("INC-00241", rm.Descendants(EdxlRm.Rm + "IncidentID").Single().Value);

        var sitrep = Xml(await integration.ExportAsync(DataStandards.EdxlSitRep));
        Assert.Equal(EdxlSitRep.Ns + "SitRep", sitrep.Name);
        Assert.Equal("INC-00241", sitrep.Element(EdxlSitRep.Ns + "IncidentID")!.Value);
        Assert.Equal("Initial", sitrep.Element(EdxlSitRep.Ns + "ReportVersion")!.Value);
        Assert.Equal("1", sitrep.Descendants(EdxlSitRep.Ns + "OpenRequests").Single().Value);
        Assert.Equal("Update", Xml(await integration.ExportAsync(DataStandards.EdxlSitRep)).Element(EdxlSitRep.Ns + "ReportVersion")!.Value);
    }

    [Fact]
    public async Task An_EDXL_DE_envelope_carries_the_whole_picture_and_imports_its_contents()
    {
        var harness = new TestHarness();
        await harness.CreateIncidentAsync();
        await harness.Perceived(new HospitalRegistered(Guid.NewGuid(), "Mater Hospital", Dublin, 30, 10));
        var integration = Integration(harness);

        var envelope = Xml(await integration.ExportAsync(DataStandards.EdxlDe));
        var contents = EdxlDe.Unwrap(envelope);
        Assert.Contains(contents, c => c.Name == EdxlHave.Ns + "HospitalStatus");
        Assert.Contains(contents, c => c.Name == EdxlSitRep.Ns + "SitRep");
        Assert.Equal(Cap.ExerciseStatus, envelope.Element(EdxlDe.Ns + "distributionStatus")!.Value);

        // A neighbouring county's system sends a warning and a CAD incident in one envelope.
        var other = new TestHarness();
        var theirIncident = await other.CreateIncidentAsync("KCC-0077");
        var inbound = EdxlDe.Wrap("KCC-1", "kildare.cad", Start,
        [
            new EdxlDe.Content("Warning", Cap.ToElement(new CapAlert("KCC-W1", "kildare.cad", Start, "Exercise", "Alert", "Public",
                [new CapInfo("Fire", "Gorse fire", "Immediate", "Severe", "Observed", "Gorse fire near Naas", null, "Keep windows closed.", "Kildare Fire Service", [])]))),
            new EdxlDe.Content("Incidents", Niem.FromCop(other.Cop, "Kildare CAD")),
        ]);
        var result = await integration.ImportAsync(DataStandards.EdxlDe, inbound.ToString());
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Accepted);
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Kildare Fire Service");
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Kildare CAD" && r.Claim.Contains("KCC-0077"));
        _ = theirIncident;
    }

    [Fact]
    public async Task NIEM_exports_incidents_and_typed_units_and_takes_a_CADs_incidents_in_as_reports()
    {
        var harness = new TestHarness();
        var incident = await harness.CreateIncidentAsync();
        var engine = await harness.RegisterUnitAsync("Engine 12");
        Assert.True((await harness.C2.DispatchAsync(engine, incident)).Succeeded);
        var integration = Integration(harness);

        var niem = Xml(await integration.ExportAsync(DataStandards.Niem));
        var exported = Assert.Single(niem.Elements(Niem.Nc + "Incident"));
        Assert.Equal("INC-00241", exported.Element(Niem.Nc + "ActivityIdentification")!.Element(Niem.Nc + "IdentificationID")!.Value);
        Assert.Equal("StructureFire", exported.Element(Niem.Nc + "ActivityCategoryText")!.Value);
        var unit = Assert.Single(niem.Elements(Niem.Exchange + "Unit"));
        Assert.Equal("Engine, Fire, Type 1", unit.Element(Niem.Nc + "ItemCategoryText")!.Value);
        Assert.Equal("INC-00241", unit.Element(Niem.Exchange + "AssignedIncidentID")!.Value);

        var (sender, incidents) = Niem.Read(niem);
        Assert.Equal(IntegrationService.DefaultSender, sender);
        Assert.Equal(Dublin.Latitude, Assert.Single(incidents).Location!.Value.Latitude, 5);

        var other = new TestHarness();
        await other.CreateIncidentAsync("CAD-9001");
        var cad = Niem.FromCop(other.Cop, "Dublin CAD").ToString();
        Assert.Equal(1, (await integration.ImportAsync(DataStandards.Niem, cad)).Accepted);
        Assert.Equal(0, (await integration.ImportAsync(DataStandards.Niem, cad)).Accepted); // already received
        var report = Assert.Single(harness.Cop.Reports);
        Assert.Equal(ReportSource.Agency, report.Source);
        Assert.Contains("CAD-9001", report.Claim);
        Assert.Contains("14 Industrial Road", report.Claim);
    }

    /// <summary>A patient tagged at the scene and on the way to St. James's.</summary>
    private static async Task<(Guid Patient, Guid Hospital, Guid Other)> PatientEnRouteAsync(TestHarness harness)
    {
        var incident = await harness.CreateIncidentAsync();
        var ambulance = await harness.RegisterUnitAsync("Ambulance 14", UnitType.AmbulanceAls);
        var hospital = Guid.NewGuid();
        var other = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(hospital, "St. James's Hospital", Dublin, 40, 20));
        await harness.Perceived(new HospitalRegistered(other, "Beaumont Hospital", GeoMath.Destination(Dublin, 0, 6000), 40, 20));
        var patient = Guid.NewGuid();
        await harness.Perceived(new PatientTagged(patient, "T-0001", incident, Triage.Immediate, null, "Burns to arms", ambulance));
        await harness.Perceived(new PatientTransported(patient, ambulance, hospital));
        return (patient, hospital, other);
    }

    [Fact]
    public async Task HL7_tells_the_hospital_a_patient_is_coming_as_a_training_message()
    {
        var harness = new TestHarness();
        await PatientEnRouteAsync(harness);

        var document = await Integration(harness).ExportAsync(DataStandards.Hl7);
        Assert.Equal(1, document.Items);
        var message = Hl7Message.Parse(document.Content);
        Assert.Equal("ADT^A05", message.Type);
        Assert.Equal(Hl7.ProcessingId, message.Get("MSH", 11)); // T: training
        Assert.Equal("2.5.1", message.Get("MSH", 12));
        Assert.Equal("St. James's Hospital", message.Get("MSH", 6));
        Assert.Equal("T-0001", message.Get("PID", 3, 1));
        Assert.Equal("E", message.Get("PV1", 2));
        Assert.Equal("P1", message.Get("OBX", 5, 1));
        Assert.Equal("Burns to arms", message.Get("DG1", 3, 2));
    }

    [Fact]
    public async Task Hospitals_ADT_messages_move_patients_on_the_board_and_are_acknowledged()
    {
        var harness = new TestHarness();
        var (patient, hospital, other) = await PatientEnRouteAsync(harness);
        var integration = Integration(harness);
        string Adt(string trigger, string facility, string extra = "") =>
            $"MSH|^~\\&|PAS|{facility}|ERS|ERS|20261008141500||ADT^{trigger}|MSG-{trigger}|T|2.5.1\rEVN|{trigger}|20261008141500\r" +
            $"PID|1||T-0001^^^ERS^PI||UNKNOWN^T-0001\rPV1|1|E|ED^^^{facility}{extra}\r";

        var arrived = await integration.ImportAsync(DataStandards.Hl7, Adt("A04", "St. James's Hospital"));
        Assert.True(arrived.Succeeded, arrived.Message);
        Assert.StartsWith("MSH|^~\\&|ERS|ERS|PAS|", arrived.Reply);
        Assert.Contains("MSA|AA|MSG-A04", arrived.Reply);
        PatientRecord Record() => harness.Cop.Health.Patients.Single(p => p.Id == patient); // the COP hands out copies
        var record = Record();
        Assert.Equal(CasualtyState.AtHospital, record.State);
        Assert.Equal(hospital, record.HospitalId);

        Assert.True((await integration.ImportAsync(DataStandards.Hl7, Adt("A02", "Beaumont Hospital"))).Succeeded);
        Assert.Equal(other, Record().HospitalId);
        Assert.Equal(1, Record().Transfers);

        var expired = Adt("A03", "Beaumont Hospital", new string('|', 33) + Hl7.Expired);
        Assert.True((await integration.ImportAsync(DataStandards.Hl7, expired)).Succeeded);
        Assert.Equal(CasualtyState.Deceased, Record().State);

        var unknown = await integration.ImportAsync(DataStandards.Hl7, Adt("A04", "St. James's Hospital").Replace("T-0001", "T-9999"));
        Assert.False(unknown.Succeeded);
        Assert.Contains("MSA|AE|MSG-A04", unknown.Reply);
    }

    [Fact]
    public async Task An_outside_tracker_feeds_unit_positions_by_callsign()
    {
        var harness = new TestHarness();
        var engine = await harness.RegisterUnitAsync("Engine 12");
        var to = GeoMath.Destination(Dublin, 90, 1500);

        var result = await Integration(harness).FeedPositionsAsync(
            [new ExternalPosition("engine 12", to.Latitude, to.Longitude, 48, 90, UnitStatus.EnRoute), new ExternalPosition("Engine 99", 53.3, -6.2)]);
        Assert.Equal(1, result.Accepted);
        Assert.Contains(result.Notes, n => n.Contains("Engine 99"));
        var unit = harness.Cop.FindUnit(engine)!;
        Assert.Equal(to.Latitude, unit.Location!.Y, 6);
        Assert.Equal(UnitStatus.EnRoute, unit.Status);
        Assert.Contains(await harness.EventsAsync(), e => e.Payload is UnitPositionReported && e.Source == EventSources.Avl);
    }

    [Fact]
    public void Plain_language_check_finds_codes_once_each_and_leaves_plain_words_alone()
    {
        var issues = NimsTerminology.CheckPlainLanguage("Engine 12, 10-4, what's your 10-20? Respond code 3, then 10-97 on arrival.");
        Assert.Equal(["10-20", "10-4", "10-97", "code 3"], issues.Select(i => i.Found).Order(StringComparer.Ordinal));
        Assert.Equal("\"understood\"", issues.Single(i => i.Found == "10-4").Say);
        Assert.Empty(NimsTerminology.CheckPlainLanguage("Engine 12, received. Go to Pearse Street, lights and sirens."));
    }

    [Fact]
    public async Task NIMS_check_finds_missing_command_overloaded_span_and_no_safety_officer_then_credits_the_fix()
    {
        var harness = new TestHarness();
        var incident = Guid.NewGuid();
        await harness.Perceived(new IncidentCreated(incident, "INC-00300", IncidentType.HazmatRelease, IncidentPriority.High, Dublin, "Docklands", null));
        for (var i = 1; i <= 9; i++)
            Assert.True((await harness.C2.DispatchAsync(await harness.RegisterUnitAsync($"Engine {i}"), incident)).Succeeded);

        var before = NimsCompliance.Check(harness.Cop);
        Assert.Contains(before.Findings, f => f is { Severity: NimsSeverity.Gap, Area: NimsArea.Organisation } && f.Message.Contains("no Incident Commander"));
        Assert.Contains(before.Findings, f => f.Severity == NimsSeverity.Gap && f.Message.Contains("supervises 9"));
        Assert.Contains(before.Findings, f => f.Severity == NimsSeverity.Gap && f.Message.Contains("no Safety Officer"));
        Assert.Contains(before.Findings, f => f.Severity == NimsSeverity.Advisory && f.Message.Contains("no Operations Section Chief"));
        Assert.Contains(before.Findings, f => f is { Area: NimsArea.ResourceTyping, Severity: NimsSeverity.Met });

        Assert.True((await harness.C2.AssignIcsPositionAsync(incident, IcsRole.IncidentCommander, "Chief Murphy")).Succeeded);
        Assert.True((await harness.C2.AssignIcsPositionAsync(incident, IcsRole.Safety, "Officer O'Connor")).Succeeded);
        Assert.True((await harness.C2.AssignIcsPositionAsync(incident, IcsRole.Operations, "SO Kelly")).Succeeded);
        var north = (await harness.C2.FormGroupAsync(incident, "Division A", IcsGroupKind.Division, "SO Byrne")).EntityId!.Value;
        var south = (await harness.C2.FormGroupAsync(incident, "Division B", IcsGroupKind.Division, "SO Walsh")).EntityId!.Value;
        var units = harness.Cop.FindIncident(incident)!.AssignedUnits.Select(u => u.Id).ToList();
        for (var i = 0; i < units.Count; i++)
            Assert.True((await harness.C2.AssignUnitToGroupAsync(units[i], i % 2 == 0 ? north : south)).Succeeded);

        var after = NimsCompliance.Check(harness.Cop);
        Assert.DoesNotContain(after.Findings, f => f.Severity == NimsSeverity.Gap);
        Assert.Contains(after.Findings, f => f.Severity == NimsSeverity.Met && f.Message.Contains("Safety Officer appointed"));
        Assert.True(after.Score > before.Score);
    }

    [Fact]
    public async Task NIMS_check_flags_a_period_running_an_hour_without_an_approved_plan_and_codes_on_the_radio()
    {
        var harness = new TestHarness();
        var incident = await harness.CreateIncidentAsync();
        var engine = await harness.RegisterUnitAsync("Engine 12");
        Assert.True((await harness.C2.DispatchAsync(engine, incident)).Succeeded);
        Assert.True((await harness.C2.AssignIcsPositionAsync(incident, IcsRole.IncidentCommander, "Chief Murphy")).Succeeded);
        Assert.True((await harness.Iap.StartOperationalPeriodAsync(incident, Start, Start.AddHours(4), "Initial response")).Succeeded);
        await harness.Perceived(new CommsLogged(Guid.NewGuid(), "fire-1", "Control", "Engine 12", "Engine 12, 10-4, give me a 10-20.",
            CommsQuality.Clear, 4, engine, true));
        await harness.RunAsync(TimeSpan.FromMinutes(70), TimeSpan.FromMinutes(1));

        var report = await Integration(harness).CheckNimsAsync(incident);
        Assert.Contains(report.Findings, f => f is { Area: NimsArea.Forms, Severity: NimsSeverity.Gap } && f.Message.Contains("no approved IAP"));
        Assert.Contains(report.Findings, f => f.Area == NimsArea.Terminology && f.Message.Contains("\"10-4\""));
        Assert.Contains(report.Findings, f => f.Area == NimsArea.Forms && f.Message.Contains("no decision logged"));
    }

    [Fact]
    public async Task The_review_carries_the_NIMS_check_and_lists_its_gaps_among_the_findings()
    {
        var harness = new TestHarness();
        var incident = await harness.CreateIncidentAsync();
        Assert.True((await harness.C2.DispatchAsync(await harness.RegisterUnitAsync("Engine 12"), incident)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(2));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId);
        Assert.NotEmpty(report.Nims.Findings);
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.StartsWith("NIMS/ICS:") && f.Text.Contains("no Incident Commander"));
        Assert.Contains(Simulation.Aar.AarExport.NimsLines(report.Nims), l => l.Label == "Score");
    }

    [Fact]
    public async Task The_shipped_sample_documents_import()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "standards", "samples"))) folder = folder.Parent;
        Assert.NotNull(folder);
        string Sample(string name) => File.ReadAllText(Path.Combine(folder.FullName, "standards", "samples", name));

        var harness = new TestHarness();
        var james = Guid.NewGuid();
        await harness.Perceived(new HospitalRegistered(james, "St. James's Hospital", new GeoPoint(53.3415, -6.2949), 40, 35));
        var integration = Integration(harness);

        Assert.True((await integration.ImportAsync(DataStandards.Cap, Sample("wind-warning.cap.xml"))).Succeeded);
        Assert.True((await integration.ImportAsync(DataStandards.EdxlHave, Sample("hospital-status.have.xml"))).Succeeded);
        Assert.True((await integration.ImportAsync(DataStandards.Niem, Sample("cad-incident.niem.xml"))).Succeeded);
        Assert.True(harness.Cop.Hospitals.Single(h => h.Id == james).OnDiversion);
        Assert.Equal(2, harness.Cop.Reports.Count);
    }

    [Fact]
    public void Every_unit_type_but_Other_has_a_NIMS_resource_type_and_the_catalogue_lists_every_standard()
    {
        Assert.All(Enum.GetValues<UnitType>().Where(t => t != UnitType.Other), t => Assert.NotEqual("Not typed", NimsResourceTypes.Of(t).Kind));
        Assert.Equal(["cap", "edxl-de", "edxl-have", "edxl-rm", "edxl-sitrep", "hl7v2", "niem"], DataStandards.All.Select(s => s.Key).Order());
        Assert.False(DataStandards.Find("EDXL-RM")!.Imports);
        Assert.Equal("Operations Section Chief", NimsTerminology.Title(IcsRole.Operations));
    }
}
