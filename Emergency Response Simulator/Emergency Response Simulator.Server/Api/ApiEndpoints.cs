using System.Text;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Documentation;
using Emergency_Response_Simulator.Simulation.Remote;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server.Api;

/// <summary>
/// The REST API (Phase 20): API-first access for outside tools, beside the SignalR hub the simulator's own clients use.
/// Every module is reachable: the COP, AVL and comms log read directly; C2, IAP, GIS, plume, the review and the data
/// standards through the same dispatcher, authoriser and command gate as the hub, so a call over REST is checked and
/// recorded exactly as the same call from a client. Callers authenticate with an API key (X-Api-Key or a bearer token)
/// and act as the participant that key joins each session as. JSON is the wire format (<see cref="WireJson"/>): enums as
/// names, geometries as WKT.
/// </summary>
public static class ApiEndpoints
{
    public const string Base = "/api/v1";

    public static void MapErsApi(this WebApplication app)
    {
        var api = app.MapGroup(Base);

        // ---- Discovery: no key needed to see what there is ----
        api.MapGet("", (ApiKeys keys) => Json(new
        {
            Name = "Emergency Response Simulator API",
            Version = "1",
            Authentication = $"Send an API key as the {ApiKeys.Header} header or as 'Authorization: Bearer <key>'. Keys are set in Server:ApiKeys.",
            KeysConfigured = keys.Any,
            Modules = ServiceModules.All,
            Standards = DataStandards.All,
            Routes = Routes,
        }));
        api.MapGet("/modules", () => Json(ServiceModules.All));
        api.MapGet("/standards", () => Json(DataStandards.All));
        api.MapGet("/terminology", () => Json(new
        {
            Positions = Enum.GetValues<IcsRole>().Select(r => new { Role = r, Title = NimsTerminology.Title(r), CommandStaff = NimsTerminology.IsCommandStaff(r) }),
            NimsTerminology.Crosswalk,
            ResourceTypes = Enum.GetValues<UnitType>().Select(t => new { Unit = t, Nims = NimsResourceTypes.Of(t) }),
        }));

        // ---- Sessions ----
        api.MapGet("/sessions", (HttpContext http, ApiKeys keys, SessionManager sessions) =>
            keys.Authenticate(http.Request) is not { } client
                ? Unauthorised()
                : Json(sessions.All.Select(s => new
                {
                    s.Id, s.Title, Scenario = s.Scenario.Key, s.State, s.CreatedAt,
                    Code = client.Role == UserRole.Instructor ? s.Code : null,
                })));

        var session = api.MapGroup("/sessions/{sessionId:guid}");
        session.MapGet("", (HttpContext http, Guid sessionId) => In(http, sessionId, (s, p) => Task.FromResult(Json(new
        {
            s.Id, s.Title, Scenario = s.Scenario.Key, s.State, Clock = Broadcaster.ClockOf(s.Engine),
            You = new { p.Id, p.Name, p.Role, p.Truth, Feed = p.Api?.Feed ?? false },
            Participants = s.Participants.Select(x => new { x.Id, x.Name, x.Role, x.Present, External = x.Api is not null }),
        }))));

        // ---- COP: the perceived picture, read directly ----
        session.MapGet("/cop", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => new
        {
            cop.AsOf, cop.LastSequence,
            Incidents = cop.Incidents.Count, Units = cop.Units.Count, Zones = cop.Zones.Count, Alerts = cop.Alerts.Count(a => a.AcknowledgedAt is null),
            Reports = cop.Reports.Count, Hospitals = cop.Hospitals.Count, Patients = cop.Health.Patients.Count, cop.Weather, cop.Eoc.Level,
        }));
        session.MapGet("/cop/incidents", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Incidents.Select(i => ApiDtos.Incident(cop, i))));
        session.MapGet("/cop/units", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Units.Select(u => ApiDtos.Unit(cop, u))));
        session.MapGet("/cop/zones", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Zones.Select(z => ApiDtos.Zone(cop, z))));
        session.MapGet("/cop/hospitals", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Hospitals.Select(h => ApiDtos.Hospital(cop, h))));
        session.MapGet("/cop/alerts", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Alerts.Select(ApiDtos.Alert)));
        session.MapGet("/cop/reports", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Reports.Select(ApiDtos.Report)));
        session.MapGet("/cop/resource-requests", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.ResourceRequests));
        session.MapGet("/cop/patients", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Health.Patients));
        session.MapGet("/cop/weather", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Weather));

        // ---- AVL: latest fixes out, an outside tracker's fixes in ----
        session.MapGet("/avl", (HttpContext http, Guid sessionId) => In(http, sessionId, (s, _) =>
            Task.FromResult(Json(s.Services.GetRequiredService<IAvlService>().LatestFixes))));
        session.MapPost("/avl", (HttpContext http, Guid sessionId) => Body(http, sessionId, SimulationHubProtocol.Services.Integration,
            nameof(IIntegrationService.FeedPositionsAsync)));

        // ---- Comms: the log and the channels; transmissions go through C2 ----
        session.MapGet("/comms/channels", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.Channels.Select(c => new
        {
            c.Info, c.Talker, c.BusyUntil,
        })));
        session.MapGet("/comms/log", (HttpContext http, Guid sessionId, string? channel, DateTimeOffset? after, int? limit) =>
            Read(http, sessionId, cop => cop.CommsLog
                .Where(c => (channel is null || c.ChannelId == channel) && (after is null || c.At > after))
                .TakeLast(Math.Clamp(limit ?? 200, 1, 2000))));

        // ---- IAP: plans and the ICS forms ----
        session.MapGet("/iap/plans", (HttpContext http, Guid sessionId) => Read(http, sessionId, cop => cop.ActionPlans.Select(p => new
        {
            p.Id, Incident = cop.FindIncident(p.IncidentId)?.Number, Period = cop.OperationalPeriods.FirstOrDefault(o => o.Id == p.OperationalPeriodId)?.Number,
            p.Version, p.Status, p.PreparedBy, p.CreatedAt, p.LastSavedAt,
        })));
        session.MapGet("/iap/plans/{planId:guid}", (HttpContext http, Guid sessionId, Guid planId) =>
            Read(http, sessionId, cop => cop.ActionPlans.FirstOrDefault(p => p.Id == planId)?.Content));
        session.MapGet("/iap/forms/{form}", (HttpContext http, Guid sessionId, string form, Guid? incident, Guid? plan, string? format) =>
            In(http, sessionId, async (s, p) =>
            {
                if (!Enum.TryParse<IcsFormKind>(form.Replace(" ", ""), ignoreCase: true, out var kind))
                    return Results.NotFound(new { Error = $"No form '{form}'. Forms: {string.Join(", ", IcsForms.All)}." });
                var events = new List<SimEvent>();
                await foreach (var e in s.Services.GetRequiredService<IEventStore>().ReadAsync(s.Id))
                    if (EventFeed.Visible(e, p.Truth, p.Assessor)) events.Add(e);
                var built = new FormBuilder(s.Services.GetRequiredService<ICopService>(), events).Build(kind, incident, plan);
                return string.Equals(format, "html", StringComparison.OrdinalIgnoreCase)
                    ? Results.Content(IcsForms.ToHtml([built]), "text/html", Encoding.UTF8)
                    : string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
                        ? Json(built)
                        : Results.Text(IcsForms.ToText(built), "text/plain", Encoding.UTF8);
            }));

        // ---- GIS and plume, through the server ----
        session.MapGet("/gis/layers", (HttpContext http, Guid sessionId) => Call(http, sessionId, SimulationHubProtocol.Services.Gis,
            nameof(IGisService.GetLayersAsync), []));
        session.MapGet("/gis/layers/{layer}/features", (HttpContext http, Guid sessionId, string layer, string? bbox) =>
        {
            if (bbox is null)
                return Call(http, sessionId, SimulationHubProtocol.Services.Gis, nameof(IGisService.GetAllFeaturesAsync), [Arg(layer)]);
            try
            {
                return Call(http, sessionId, SimulationHubProtocol.Services.Gis, nameof(IGisService.GetFeaturesAsync), [Arg(layer), Arg(BoundingBox.Parse(bbox))]);
            }
            catch (FormatException ex)
            {
                return Task.FromResult(Results.BadRequest(new { Error = ex.Message }));
            }
        });
        session.MapGet("/plume/substances", (HttpContext http, Guid sessionId) => Call(http, sessionId, SimulationHubProtocol.Services.Plume,
            nameof(IPlumeService.GetSubstancesAsync), []));
        session.MapPost("/plume/predict", (HttpContext http, Guid sessionId) => Body(http, sessionId, SimulationHubProtocol.Services.Plume,
            nameof(IPlumeService.PredictAsync)));

        // ---- The review ----
        session.MapGet("/aar/report", (HttpContext http, Guid sessionId, string? title) => Call(http, sessionId, SimulationHubProtocol.Services.Aar,
            nameof(IAarService.BuildReportAsync), [Arg(sessionId), Arg(title)]));

        // ---- The event stream, for tools that keep their own copy ----
        session.MapGet("/events", (HttpContext http, Guid sessionId, long? after, int? limit) => In(http, sessionId, async (s, p) =>
        {
            var events = new List<SimEvent>();
            var max = Math.Clamp(limit ?? 500, 1, 5000);
            await foreach (var e in s.Services.GetRequiredService<IEventStore>().ReadAsync(s.Id, after ?? 0, http.RequestAborted))
            {
                if (!EventFeed.Visible(e, p.Truth, p.Assessor)) continue;
                events.Add(e);
                if (events.Count >= max) break;
            }
            return Json(new { Events = events, Last = events.LastOrDefault()?.Sequence ?? after ?? 0, More = events.Count >= max });
        }));

        // ---- Data standards and NIMS ----
        session.MapGet("/standards/{standard}", (HttpContext http, Guid sessionId, string standard, Guid? incident) =>
            In(http, sessionId, async (s, p) =>
            {
                var result = await Dispatch(http, s, p, SimulationHubProtocol.Services.Integration, nameof(IIntegrationService.ExportAsync), [Arg(standard), Arg(incident)]);
                if (result.Error is { } error) return error;
                var document = result.Value!.Value.Deserialize<StandardDocument>(WireJson.Options)!;
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{document.FileName}\"";
                return Results.Text(document.Content, document.MediaType, Encoding.UTF8);
            }));
        session.MapPost("/standards/{standard}", (HttpContext http, Guid sessionId, string standard) => In(http, sessionId, async (s, p) =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(http.RequestAborted);
            var result = await Dispatch(http, s, p, SimulationHubProtocol.Services.Integration, nameof(IIntegrationService.ImportAsync), [Arg(standard), Arg(content)]);
            return result.Error ?? Results.Json(result.Value, statusCode: result.Value!.Value.GetProperty("succeeded").GetBoolean() ? 200 : 422);
        }));
        session.MapGet("/nims", (HttpContext http, Guid sessionId, Guid? incident) => Call(http, sessionId, SimulationHubProtocol.Services.Integration,
            nameof(IIntegrationService.CheckNimsAsync), [Arg(incident)]));

        // ---- Any service method, as the hub's Call: body is the JSON array of arguments ----
        session.MapPost("/services/{service}/{method}", (HttpContext http, Guid sessionId, string service, string method) =>
            In(http, sessionId, async (s, p) =>
            {
                JsonElement[] args;
                try
                {
                    args = http.Request.ContentLength is 0 ? [] : await JsonSerializer.DeserializeAsync<JsonElement[]>(http.Request.Body, WireJson.Options, http.RequestAborted) ?? [];
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { Error = $"The body must be a JSON array of the method's arguments: {ex.Message}" });
                }
                var result = await Dispatch(http, s, p, service, method.EndsWith("Async", StringComparison.Ordinal) ? method : method + "Async", args);
                return result.Error ?? (result.Value is { } value ? Results.Json(value) : Results.NoContent());
            }));
    }

    /// <summary>The routes, for the index.</summary>
    private static readonly string[] Routes =
    [
        "GET  /api/v1                              this index (no key needed)",
        "GET  /api/v1/modules | /standards | /terminology",
        "GET  /api/v1/sessions",
        "GET  /api/v1/sessions/{id}                 the session, its clock and participants",
        "GET  /api/v1/sessions/{id}/cop[/incidents|/units|/zones|/hospitals|/alerts|/reports|/resource-requests|/patients|/weather]",
        "GET  /api/v1/sessions/{id}/avl             latest AVL fixes",
        "POST /api/v1/sessions/{id}/avl             [ExternalPosition] from an outside tracker (Feed keys)",
        "GET  /api/v1/sessions/{id}/comms/channels | /comms/log?channel=&after=&limit=",
        "GET  /api/v1/sessions/{id}/iap/plans[/{planId}] | /iap/forms/{Ics201…Ics220|Sitrep|DecisionLog|IapPackage}?incident=&plan=&format=text|html|json",
        "GET  /api/v1/sessions/{id}/gis/layers | /gis/layers/{key}/features?bbox=south,west,north,east",
        "GET  /api/v1/sessions/{id}/plume/substances | POST /plume/predict (PlumeRequest)",
        "GET  /api/v1/sessions/{id}/aar/report",
        "GET  /api/v1/sessions/{id}/events?after=&limit=   the event stream this key may see",
        "GET  /api/v1/sessions/{id}/standards/{cap|edxl-de|edxl-have|edxl-rm|edxl-sitrep|niem|hl7v2}?incident=",
        "POST /api/v1/sessions/{id}/standards/{cap|edxl-de|edxl-have|niem|hl7v2}   a document in (Feed keys)",
        "GET  /api/v1/sessions/{id}/nims?incident=  the NIMS/ICS compliance check",
        "POST /api/v1/sessions/{id}/services/{c2|iap|control|aar|instructor|assessment|gis|plume|integration}/{method}   [arguments]",
    ];

    // ---- Plumbing ----

    private sealed record Outcome(JsonElement? Value, IResult? Error);

    private static IResult Json(object? value) => Results.Json(value, WireJson.Options);

    private static IResult Unauthorised() => Results.Json(new { Error = $"Send a valid API key ({ApiKeys.Header})." }, statusCode: 401);

    private static JsonElement Arg<T>(T value) => JsonSerializer.SerializeToElement(value, WireJson.Options);

    /// <summary>Authenticates, finds the session, and runs the handler as the key's participant in it.</summary>
    private static async Task<IResult> In(HttpContext http, Guid sessionId, Func<SessionRuntime, ParticipantRecord, Task<IResult>> handler)
    {
        var services = http.RequestServices;
        if (services.GetRequiredService<ApiKeys>().Authenticate(http.Request) is not { } client) return Unauthorised();
        if (services.GetRequiredService<SessionManager>().Find(sessionId) is not { } session)
            return Results.NotFound(new { Error = "No such session." });
        var participant = await session.ExternalAsync(client);
        return await handler(session, participant);
    }

    private static Task<IResult> Read(HttpContext http, Guid sessionId, Func<ICopService, object?> read) =>
        In(http, sessionId, (s, _) => Task.FromResult(Json(read(s.Services.GetRequiredService<ICopService>()))));

    private static Task<IResult> Call(HttpContext http, Guid sessionId, string service, string method, JsonElement[] args) =>
        In(http, sessionId, async (s, p) =>
        {
            var result = await Dispatch(http, s, p, service, method, args);
            return result.Error ?? (result.Value is { } value ? Results.Json(value) : Results.NoContent());
        });

    /// <summary>A POST whose body is the method's one argument.</summary>
    private static Task<IResult> Body(HttpContext http, Guid sessionId, string service, string method) =>
        In(http, sessionId, async (s, p) =>
        {
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, WireJson.Options, http.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { Error = $"The body isn't JSON: {ex.Message}" });
            }
            var result = await Dispatch(http, s, p, service, method, [body]);
            return result.Error ?? (result.Value is { } value ? Results.Json(value) : Results.NoContent());
        });

    /// <summary>
    /// Through the authoriser (a refusal is 403) and the dispatcher, exactly as the hub's Call: an unknown call is 404,
    /// anything else the service refuses is 400.
    /// </summary>
    private static async Task<Outcome> Dispatch(HttpContext http, SessionRuntime session, ParticipantRecord participant, string service, string method,
        JsonElement[] args)
    {
        if (!CommandDispatcher.Contracts.ContainsKey(service))
            return new(null, Results.NotFound(new { Error = $"No service '{service}'. Services: {string.Join(", ", CommandDispatcher.Contracts.Keys)}." }));
        if (!CommandDispatcher.Callable.Any(c => c.Service == service && c.Method.Name == method))
            return new(null, Results.NotFound(new { Error = $"{service} has no method '{method}'." }));
        try
        {
            CommandAuthorizer.Authorise(session, participant, service, method);
        }
        catch (HubException ex)
        {
            return new(null, Results.Json(new { Error = ex.Message }, statusCode: 403));
        }
        try
        {
            var dispatcher = http.RequestServices.GetRequiredService<CommandDispatcher>();
            return new(await dispatcher.InvokeAsync(session, participant, service, method, args, http.RequestAborted), null);
        }
        catch (HubException ex) when (ex.Message.StartsWith("Unknown call", StringComparison.Ordinal))
        {
            return new(null, Results.NotFound(new { Error = ex.Message }));
        }
        catch (Exception ex) when (ex is HubException or JsonException or ArgumentException or InvalidOperationException)
        {
            return new(null, Results.BadRequest(new { Error = ex.Message }));
        }
    }
}
