using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.Server.Field;

/// <summary>
/// The field companion's API (Phase 21) under /api/v1/field, and the app itself at /field. A phone needs no API key: it
/// joins with the session's code like anyone else, and gets a token (X-Field-Token) that speaks for one unit only.
/// </summary>
public static class FieldEndpoints
{
    public const string Header = "X-Field-Token";
    public const string AppPath = "/field";

    public sealed record JoinRequest(string? Code, string? Name, string? Callsign);
    public sealed record ReportRequest(string? Text, double? Latitude, double? Longitude, double? Accuracy);
    public sealed record StatusRequest(UnitStatus Status);
    public sealed record CheckInRequest(int Crew, double? Latitude, double? Longitude);
    public sealed record ParRequest(int Accounted, IReadOnlyList<string>? Missing);
    public sealed record AcknowledgeRequest(string? ReadBack);
    public sealed record PositionRequest(double Latitude, double Longitude, double SpeedKph = 0, double Heading = 0);

    /// <summary>The app's files, embedded in the server: name → media type.</summary>
    public static readonly IReadOnlyDictionary<string, string> AppFiles = new Dictionary<string, string>
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["field.css"] = "text/css; charset=utf-8",
        ["field.js"] = "text/javascript; charset=utf-8",
        ["sw.js"] = "text/javascript; charset=utf-8",
        ["manifest.webmanifest"] = "application/manifest+json",
        ["icon.svg"] = "image/svg+xml",
    };

    public static void MapFieldApi(this WebApplication app)
    {
        // The app at /field/ and its files. Routing doesn't tell /field from /field/, but the page's relative links need the
        // slash, so the bare path is redirected before routing gets it.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value == AppPath) context.Response.Redirect(AppPath + "/");
            else await next(context);
        });
        app.MapGet(AppPath + "/", () => File("index.html"));
        app.MapGet(AppPath + "/{file}", (string file) => File(file));

        var field = app.MapGroup("/api/v1/field");

        field.MapPost("/join", (JoinRequest request, SessionManager sessions) =>
        {
            var code = request.Code?.Trim().ToUpperInvariant() ?? "";
            if (sessions.All.FirstOrDefault(s => s.Code == code && s.Joinable(DateTimeOffset.UtcNow)) is not { } session)
                return Results.NotFound(new FieldResult(false, "No open session has that code. Check it with your instructor; codes expire."));
            var (user, problem) = session.Field.Join(session, request.Name, request.Callsign);
            return user is null
                ? Results.BadRequest(new FieldResult(false, problem!))
                : Results.Ok(new { user.Token, session.Title, user.Callsign, user.Name, SessionId = session.Id });
        });

        field.MapGet("/me", (HttpContext http, SessionManager sessions) =>
            As(http, sessions, (session, user) => Task.FromResult(Results.Json(session.Field.Brief(session, user), WireJson.Options))));
        field.MapPost("/report", (HttpContext http, SessionManager sessions, ReportRequest r) =>
            Act(http, sessions, (session, user) => session.Field.ReportAsync(session, user, r.Text, r.Latitude, r.Longitude, r.Accuracy)));
        field.MapPost("/status", (HttpContext http, SessionManager sessions, StatusRequest r) =>
            Act(http, sessions, (session, user) => session.Field.StatusAsync(session, user, r.Status)));
        field.MapPost("/checkin", (HttpContext http, SessionManager sessions, CheckInRequest r) =>
            Act(http, sessions, (session, user) => session.Field.CheckInAsync(session, user, r.Crew, r.Latitude, r.Longitude)));
        field.MapPost("/par", (HttpContext http, SessionManager sessions, ParRequest r) =>
            Act(http, sessions, (session, user) => session.Field.ParAsync(session, user, r.Accounted, r.Missing)));
        field.MapPost("/orders/{orderId:guid}/acknowledge", (HttpContext http, SessionManager sessions, Guid orderId, AcknowledgeRequest r) =>
            Act(http, sessions, (session, user) => session.Field.AcknowledgeAsync(session, user, orderId, r.ReadBack)));
        field.MapPost("/position", (HttpContext http, SessionManager sessions, PositionRequest r) =>
            Act(http, sessions, (session, user) => session.Field.PositionAsync(session, user, r.Latitude, r.Longitude, r.SpeedKph, r.Heading)));
    }

    private static IResult File(string name)
    {
        if (!AppFiles.TryGetValue(name, out var type)) return Results.NotFound();
        var stream = typeof(FieldEndpoints).Assembly.GetManifestResourceStream($"FieldApp.{name}");
        return stream is null ? Results.NotFound() : Results.Stream(stream, type);
    }

    private static Task<IResult> As(HttpContext http, SessionManager sessions, Func<SessionRuntime, FieldUser, Task<IResult>> handler)
    {
        var token = http.Request.Headers[Header].FirstOrDefault();
        foreach (var session in sessions.All)
            if (session.Field.Find(token) is { } user)
                return handler(session, user);
        return Task.FromResult(Results.Json(new FieldResult(false, "Join the exercise first (code, name and callsign)."), statusCode: 401));
    }

    private static Task<IResult> Act(HttpContext http, SessionManager sessions, Func<SessionRuntime, FieldUser, Task<FieldResult>> action) =>
        As(http, sessions, async (session, user) =>
        {
            var result = await action(session, user);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });
}
