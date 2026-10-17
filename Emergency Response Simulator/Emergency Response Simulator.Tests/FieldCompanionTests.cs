using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server.Field;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 21: the mobile field companion. A crew member's phone joins with the session code and their unit's callsign, and
/// its reports, check-ins, status changes, PAR answers and order acknowledgements reach the COP as perceived information
/// from that unit, signed with who sent it.
/// </summary>
public class FieldCompanionTests
{
    private static async Task<(HttpClient Phone, Unit Engine)> JoinAsync(InProcessServer server, string name = "Ciara Byrne")
    {
        var engine = server.Cop.Units.First(u => u.Type == UnitType.Engine && u.Agency?.AiControlled != true);
        var phone = server.Http();
        var joined = await phone.PostAsJsonAsync("/api/v1/field/join", new { code = server.Session.Code.ToLowerInvariant(), name, callsign = engine.Callsign.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var token = (await joined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        phone.DefaultRequestHeaders.Add(FieldEndpoints.Header, token);
        return (phone, engine);
    }

    private static async Task<JsonElement> PostAsync(HttpClient phone, string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await phone.PostAsJsonAsync("/api/v1/field" + path, body);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(expected == response.StatusCode, $"{path}: {response.StatusCode} {result}");
        return result;
    }

    [Fact]
    public async Task The_app_is_served_and_joining_needs_a_real_code_name_and_callsign()
    {
        await using var server = await StartAsync();
        using var browser = server.Http();

        var page = await browser.GetAsync("/field/");
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Field companion", html);
        Assert.Contains("manifest.webmanifest", html);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/field/field.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/field/sw.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/field/secrets.txt")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync("/api/v1/field/join", new { code = "ZZZZZZ", name = "A", callsign = "Engine 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/v1/field/join", new { code = server.Session.Code, name = "A", callsign = "Starship 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/v1/field/join", new { code = server.Session.Code, name = "", callsign = "Engine 1" })).StatusCode);
        var aiUnit = server.Cop.Units.FirstOrDefault(u => u.Agency?.AiControlled == true && !u.AttachedToCommand);
        if (aiUnit is not null)
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/v1/field/join", new { code = server.Session.Code, name = "A", callsign = aiUnit.Callsign })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/field/me")).StatusCode);
        browser.DefaultRequestHeaders.Add(FieldEndpoints.Header, "made-up");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/api/v1/field/report", new { text = "hello" })).StatusCode);
    }

    [Fact]
    public async Task A_field_report_reaches_the_COP_from_the_unit_signed_with_who_sent_it()
    {
        await using var server = await StartAsync();
        var (phone, engine) = await JoinAsync(server);

        var me = await phone.GetFromJsonAsync<JsonElement>("/api/v1/field/me");
        Assert.Equal(engine.Callsign, me.GetProperty("callsign").GetString());
        Assert.True(me.GetProperty("running").GetBoolean());

        await PostAsync(phone, "/report", new { text = "Smoke showing from the second floor, rear of the building", latitude = 53.3404, longitude = -6.2371, accuracy = 12 });
        var report = server.Cop.Reports.Single(r => r.SourceName == $"{engine.Callsign} (Ciara Byrne, field app)");
        Assert.Equal(ReportSource.FieldUnit, report.Source);
        Assert.Contains("second floor", report.Claim);
        Assert.Equal(53.3404, report.Location!.Y, 4);
        var stored = server.Store.All().Single(e => e.Payload is ReportReceived r && r.ReportId == report.Id);
        Assert.Equal(EventSources.Field, stored.Source);
        Assert.Equal(EventVisibility.Perceived, stored.Visibility);
        Assert.Equal(engine.Id, ((ReportReceived)stored.Payload).FromUnitId);

        await PostAsync(phone, "/report", new { text = "" }, HttpStatusCode.BadRequest);
        await PostAsync(phone, "/report", new { text = new string('x', FieldCompanion.MaxReport + 1) }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Status_check_in_PAR_and_orders_from_the_field()
    {
        await using var server = await StartAsync();
        var (phone, engine) = await JoinAsync(server);
        var c2 = RemoteProxy.Create<IC2Service>(server.Instructor, SimulationHubProtocol.Services.C2);

        await PostAsync(phone, "/status", new { status = "OutOfService" });
        Assert.Equal(UnitStatus.OutOfService, server.Cop.FindUnit(engine.Id)!.Status);
        await PostAsync(phone, "/status", new { status = "Dispatched" }, HttpStatusCode.BadRequest); // control's to set, not the crew's
        await PostAsync(phone, "/status", new { status = "Available" });

        // Not assigned yet: no incident to check in at.
        await PostAsync(phone, "/checkin", new { crew = 4 }, HttpStatusCode.BadRequest);
        var incident = (await c2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation, "Barrow Street")).EntityId!.Value;
        Assert.True((await c2.DispatchAsync(engine.Id, incident)).Succeeded);
        await PostAsync(phone, "/checkin", new { crew = 4, latitude = 53.3405, longitude = -6.2370 });
        Assert.Equal(UnitStatus.OnScene, server.Cop.FindUnit(engine.Id)!.Status);
        Assert.Contains(server.Cop.Reports, r => r.Claim.StartsWith($"Checked in at {server.Cop.FindIncident(incident)!.Number}") && r.Claim.Contains("crew of 4"));

        // An order to the unit: the phone shows it and acknowledges it.
        Assert.True((await c2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine.Id, "Search the ground floor")).Succeeded);
        var me = await phone.GetFromJsonAsync<JsonElement>("/api/v1/field/me");
        var order = me.GetProperty("orders").EnumerateArray().First(o => o.GetProperty("text").GetString() == "Search the ground floor");
        await PostAsync(phone, $"/orders/{order.GetProperty("id").GetGuid()}/acknowledge", new { readBack = "Searching the ground floor" });
        Assert.Equal("Searching the ground floor", server.Cop.Orders.Single(o => o.Id == order.GetProperty("id").GetGuid()).ReadBack);
        await PostAsync(phone, $"/orders/{Guid.NewGuid()}/acknowledge", new { }, HttpStatusCode.BadRequest);

        // A PAR: nothing to answer until command calls one.
        await PostAsync(phone, "/par", new { accounted = 4 }, HttpStatusCode.BadRequest);
        Assert.True((await c2.RequestParAsync(incident)).Succeeded);
        await Until(() => server.Cop.ParChecks.Any(p => p.Expected.ContainsKey(engine.Id)), TimeSpan.FromSeconds(5));
        me = await phone.GetFromJsonAsync<JsonElement>("/api/v1/field/me");
        Assert.Equal(JsonValueKind.Object, me.GetProperty("par").ValueKind);
        await PostAsync(phone, "/par", new { accounted = 3, missing = new[] { "FF Walsh" } });
        var answer = server.Cop.ParChecks.Last().Responses[engine.Id];
        Assert.Equal(3, answer.Accounted);
        Assert.Contains("FF Walsh", answer.Missing);

        await PostAsync(phone, "/position", new { latitude = 53.341, longitude = -6.238, speedKph = 0, heading = 90 });
        Assert.Equal(53.341, server.Cop.FindUnit(engine.Id)!.Location!.Y, 3);
        await PostAsync(phone, "/position", new { latitude = 200.0, longitude = 0.0 }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Before_the_exercise_starts_the_field_can_join_but_not_report()
    {
        await using var server = await StartAsync(BarrowStreetScenario.Key, start: false);
        var (phone, _) = await JoinAsync(server);
        var me = await phone.GetFromJsonAsync<JsonElement>("/api/v1/field/me");
        Assert.False(me.GetProperty("running").GetBoolean());
        var refused = await PostAsync(phone, "/report", new { text = "Arrived at the station" }, HttpStatusCode.BadRequest);
        Assert.Contains("hasn't started", refused.GetProperty("message").GetString());
    }
}
