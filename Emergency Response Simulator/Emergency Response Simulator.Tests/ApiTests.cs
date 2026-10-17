using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.AspNetCore.SignalR;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 20: API-first. The REST API beside the hub, with API keys acting in a role; every module reachable over the
/// server (GIS, plume and the data standards now included); outside systems feeding data in only when allowed.
/// </summary>
public class ApiTests
{
    private const string ReaderKey = "reader-key-0123456789";
    private const string FeedKey = "cad-bridge-key-0123456789";
    private const string OperationsKey = "ops-tool-key-0123456789";

    private static Task<InProcessServer> StartAsync() => InProcessServer.StartAsync(BarrowStreetScenario.Key, true,
        "--Server:ApiKeys:0:Name=EOC dashboard", $"--Server:ApiKeys:0:Key={ReaderKey}",
        "--Server:ApiKeys:1:Name=County CAD bridge", $"--Server:ApiKeys:1:Key={FeedKey}", "--Server:ApiKeys:1:Feed=true",
        "--Server:ApiKeys:2:Name=Ops tablet", $"--Server:ApiKeys:2:Key={OperationsKey}", "--Server:ApiKeys:2:Role=Operations",
        "--Server:ApiKeys:3:Name=Too short", "--Server:ApiKeys:3:Key=short");

    private static string Session(InProcessServer server, string path = "") => $"/api/v1/sessions/{server.Session.Id}{path}";

    [Fact]
    public async Task The_index_is_open_and_everything_else_needs_a_key()
    {
        await using var server = await StartAsync();
        using var anonymous = server.Http();

        var index = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1");
        Assert.True(index.GetProperty("keysConfigured").GetBoolean());
        var modules = index.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToList();
        Assert.Superset(new HashSet<string?> { "cop", "iap", "c2", "gis", "avl", "plume", "comms", "aar", "integration" }, modules.ToHashSet());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Session(server, "/cop"))).StatusCode);
        using var tooShort = server.Http("short");
        Assert.Equal(HttpStatusCode.Unauthorized, (await tooShort.GetAsync("/api/v1/sessions")).StatusCode);

        using var reader = server.Http(ReaderKey);
        var sessions = await reader.GetFromJsonAsync<JsonElement>("/api/v1/sessions");
        var session = Assert.Single(sessions.EnumerateArray().ToList());
        Assert.Equal(server.Session.Id, session.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("code").ValueKind); // the join code is the instructor's
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/sessions/{Guid.NewGuid()}/cop")).StatusCode);
    }

    [Fact]
    public async Task A_key_reads_the_COP_AVL_comms_and_events_as_a_participant_without_ground_truth()
    {
        await using var server = await StartAsync();
        await server.StepAsync(TimeSpan.FromMinutes(2));
        using var reader = server.Http(ReaderKey);

        var units = await reader.GetFromJsonAsync<JsonElement>(Session(server, "/cop/units"));
        var engine = units.EnumerateArray().First(u => u.GetProperty("type").GetString() == "Engine");
        Assert.Equal("Engine, Fire, Type 1", engine.GetProperty("nimsType").GetString());
        Assert.Equal(JsonValueKind.Array, (await reader.GetFromJsonAsync<JsonElement>(Session(server, "/avl"))).ValueKind);
        Assert.Equal(JsonValueKind.Array, (await reader.GetFromJsonAsync<JsonElement>(Session(server, "/comms/log"))).ValueKind);

        var events = await reader.GetFromJsonAsync<JsonElement>(Session(server, "/events?limit=5000"));
        Assert.All(events.GetProperty("events").EnumerateArray(), e => Assert.Equal("Perceived", e.GetProperty("visibility").GetString()));

        // The key joined the session as a participant of its own, in its role.
        var me = (await reader.GetFromJsonAsync<JsonElement>(Session(server))).GetProperty("you");
        Assert.Equal("EOC dashboard (API)", me.GetProperty("name").GetString());
        Assert.Equal("Observer", me.GetProperty("role").GetString());
        await Until(() => server.Cop.Participants.Any(p => p.DisplayName == "EOC dashboard (API)"), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Standards_export_to_any_key_but_only_a_feed_key_brings_data_in()
    {
        await using var server = await StartAsync();
        using var reader = server.Http(ReaderKey);
        using var feed = server.Http(FeedKey);

        var niem = await reader.GetAsync(Session(server, "/standards/niem"));
        Assert.Equal(HttpStatusCode.OK, niem.StatusCode);
        Assert.Equal("application/xml", niem.Content.Headers.ContentType!.MediaType);
        Assert.Contains("IncidentExchange", await niem.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(Session(server, "/standards/fax"))).StatusCode);

        const string warning = """
            <alert xmlns="urn:oasis:names:tc:emergency:cap:1.2"><identifier>W-1</identifier><sender>met.ie</sender>
            <sent>2026-10-08T13:30:00+00:00</sent><status>Exercise</status><msgType>Alert</msgType><scope>Public</scope>
            <info><category>Met</category><event>Rain warning</event><urgency>Expected</urgency><severity>Severe</severity>
            <certainty>Likely</certainty><senderName>Met Éireann</senderName><headline>Status Red rain warning</headline></info></alert>
            """;
        var refused = await reader.PostAsync(Session(server, "/standards/cap"), new StringContent(warning, Encoding.UTF8, "application/cap+xml"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var taken = await feed.PostAsync(Session(server, "/standards/cap"), new StringContent(warning, Encoding.UTF8, "application/cap+xml"));
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal(1, (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        var report = server.Cop.Reports.Single(r => r.SourceName == "Met Éireann");
        var stored = server.Store.All().Single(e => e.Payload is Core.Events.ReportReceived r && r.ReportId == report.Id);
        Assert.Equal("County CAD bridge (API)", server.Cop.Participants.Single(p => p.Id == stored.ActorId).DisplayName); // attributed

        var nonsense = await feed.PostAsync(Session(server, "/standards/cap"), new StringContent("<x/>", Encoding.UTF8, "application/xml"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nonsense.StatusCode);
    }

    [Fact]
    public async Task A_feed_key_moves_units_from_an_outside_tracker()
    {
        await using var server = await StartAsync();
        var engine = server.Cop.Units.First(u => u.Type == UnitType.Engine && u.Agency?.AiControlled != true);
        var to = GeoMath.Destination(GeoPoint.FromPoint(engine.Location!), 0, 500);
        using var feed = server.Http(FeedKey);

        var response = await feed.PostAsJsonAsync(Session(server, "/avl"), new[] { new ExternalPosition(engine.Callsign, to.Latitude, to.Longitude, 30, 0) },
            WireJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(to.Latitude, server.Cop.FindUnit(engine.Id)!.Location!.Y, 6);
        var fixes = await feed.GetFromJsonAsync<JsonElement>(Session(server, "/avl"));
        Assert.Contains(fixes.EnumerateArray(), f => f.GetProperty("unitId").GetGuid() == engine.Id);
    }

    [Fact]
    public async Task Service_calls_go_through_the_same_authoriser_and_permission_matrix_as_the_hub()
    {
        await using var server = await StartAsync();
        using var reader = server.Http(ReaderKey);
        using var ops = server.Http(OperationsKey);

        var observer = await reader.PostAsync(Session(server, "/services/c2/IssuePublicStatement"),
            new StringContent("[\"All clear\"]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, observer.StatusCode); // observers watch

        var nims = await reader.PostAsync(Session(server, "/services/integration/CheckNims"), new StringContent("[]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, nims.StatusCode);
        Assert.True((await nims.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("findings", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsync(Session(server, "/services/nope/Anything"), new StringContent("[]"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsync(Session(server, "/services/c2/Nothing"), new StringContent("[]"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsync(Session(server, "/services/c2/SubmitReimbursementClaim"), new StringContent("[]"))).StatusCode);

        // A key acting as Operations may give Operations' commands; it doesn't hold the role against a person.
        var ring = Wgs84.Circle(BarrowStreetScenario.FireLocation, 50, 8).Select(p => new { p.Latitude, p.Longitude });
        var incident = (await ops.PostAsync(Session(server, "/services/c2/DeclareZone"), new StringContent(
            JsonSerializer.Serialize(new object[] { "HotZone", "Barrow Street hot zone", ring }), Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, incident.StatusCode);
        var created = await incident.Content.ReadAsStringAsync();
        Assert.True(created.Contains("\"succeeded\":true"), created);
        Assert.True((await incident.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("succeeded").GetBoolean());
        var person = await server.JoinAsync("Bea", UserRole.Operations, feed: false);
        Assert.Equal(UserRole.Operations, person.Ticket!.Role);
    }

    [Fact]
    public async Task Forms_and_the_NIMS_check_are_served_for_tools_and_the_review_waits_for_the_end()
    {
        await using var server = await StartAsync();
        using var reader = server.Http(ReaderKey);

        var form = await reader.GetAsync(Session(server, "/iap/forms/ics201"));
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains("INCIDENT BRIEFING", await form.Content.ReadAsStringAsync());
        Assert.Equal("text/html", (await reader.GetAsync(Session(server, "/iap/forms/Ics209?format=html"))).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(Session(server, "/iap/forms/ics999"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Session(server, "/nims"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Session(server, "/aar/report"))).StatusCode);
    }

    [Fact]
    public async Task GIS_plume_and_the_standards_are_hub_modules_and_a_trainee_cannot_import()
    {
        await using var server = await StartAsync();
        var callable = CommandDispatcher.Callable.Select(c => $"{c.Service}.{c.Method.Name}").ToList();
        Assert.Contains("gis.GetLayersAsync", callable);
        Assert.Contains("plume.PredictAsync", callable);
        Assert.Contains("integration.ImportAsync", callable);
        Assert.Contains("integration.CheckNimsAsync", callable);

        var trainee = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var integration = RemoteProxy.Create<IIntegrationService>(trainee, SimulationHubProtocol.Services.Integration);
        var exported = await integration.ExportAsync(DataStandards.Niem);
        Assert.Contains("IncidentExchange", exported.Content);
        Assert.NotEmpty((await integration.GetStandardsAsync()));
        await Assert.ThrowsAnyAsync<Exception>(() => integration.ImportAsync(DataStandards.Cap, "<alert/>"));

        // No GIS database on this server: the call says so instead of failing obscurely.
        var gis = RemoteProxy.Create<IGisService>(trainee, SimulationHubProtocol.Services.Gis);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => gis.GetLayersAsync());
        Assert.Contains("GIS", error.Message);
    }
}
