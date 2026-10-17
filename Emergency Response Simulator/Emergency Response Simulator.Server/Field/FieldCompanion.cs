using System.Collections.Concurrent;
using System.Security.Cryptography;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.Server.Field;

/// <summary>Someone in the field speaking for a unit through the companion app.</summary>
public sealed record FieldUser(string Token, Guid Id, string Name, Guid UnitId, string Callsign, Guid SessionId, DateTimeOffset JoinedAt)
{
    /// <summary>How their reports are signed: the unit, then who.</summary>
    public string Signature => $"{Callsign} ({Name}, field app)";
}

/// <summary>What the field app shows: the unit, its incident, its orders and any PAR waiting for it.</summary>
public sealed record FieldBriefing(string Callsign, UnitStatus Status, string? Incident, string? Address, double? Latitude, double? Longitude,
    string? Tasking, IReadOnlyList<FieldOrder> Orders, FieldPar? Par, DateTimeOffset SimTime, bool Running);

public sealed record FieldOrder(Guid Id, string Text, DateTimeOffset IssuedAt, bool Acknowledged);

public sealed record FieldPar(Guid Id, string Reason, int Expected);

/// <summary>A field action's outcome, for the app to show.</summary>
public sealed record FieldResult(bool Ok, string Message)
{
    public static FieldResult Fail(string message) => new(false, message);
}

/// <summary>
/// The mobile field companion (Phase 21): a crew member's phone speaks for their unit. They join with the session's code,
/// their name and the unit's callsign, and from then on send field reports (with the phone's location if they allow it),
/// check in at the incident (ICS 211), change the unit's status, answer a PAR, acknowledge orders and share position.
/// Everything arrives on the COP as perceived information from the unit, signed with who sent it, through the session's
/// command gate like any command. The token is the only credential; it lasts as long as the session.
/// </summary>
public sealed class FieldCompanion
{
    public const int MaxReport = 1000;

    /// <summary>The statuses a crew may set from the field.</summary>
    public static readonly IReadOnlySet<UnitStatus> FieldStatuses = new HashSet<UnitStatus>
    {
        UnitStatus.EnRoute, UnitStatus.OnScene, UnitStatus.Operating, UnitStatus.Transporting, UnitStatus.Available, UnitStatus.OutOfService,
    };

    private readonly ConcurrentDictionary<string, FieldUser> _users = new(StringComparer.Ordinal);

    public IReadOnlyCollection<FieldUser> Users => [.. _users.Values];

    public FieldUser? Find(string? token) => token is { Length: > 0 } && _users.TryGetValue(token, out var user) ? user : null;

    /// <summary>Joins someone to a session for one of its units; null with the reason when it can't.</summary>
    public (FieldUser? User, string? Problem) Join(SessionRuntime session, string? name, string? callsign)
    {
        var who = name?.Trim() ?? "";
        if (who.Length is 0 or > 40) return (null, "Give your name (up to 40 characters).");
        if (session.State == SessionState.Ended) return (null, "That session has ended.");
        var cop = session.Services.GetRequiredService<ICopService>();
        var unit = cop.Units.FirstOrDefault(u => string.Equals(u.Callsign, callsign?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (unit is null) return (null, $"No unit called \"{callsign}\" in this exercise. Check the callsign with control.");
        if (unit.Agency is { AiControlled: true } agency && !unit.AttachedToCommand)
            return (null, $"{unit.Callsign} is {agency.ShortName}'s, run by their own control room in this exercise.");

        var user = new FieldUser(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='),
            Guid.NewGuid(), who, unit.Id, unit.Callsign, session.Id, DateTimeOffset.UtcNow);
        _users[user.Token] = user;
        return (user, null);
    }

    public FieldBriefing Brief(SessionRuntime session, FieldUser user)
    {
        var cop = session.Services.GetRequiredService<ICopService>();
        var unit = cop.FindUnit(user.UnitId);
        var incident = unit?.AssignedIncidentId is { } id ? cop.FindIncident(id) : null;
        var orders = cop.Orders.Where(o => o.TargetId == user.UnitId && o.Status is OrderStatus.Issued or OrderStatus.Acknowledged)
            .OrderByDescending(o => o.IssuedAt).Take(10)
            .Select(o => new FieldOrder(o.Id, o.Text, o.IssuedAt, o.AcknowledgedAt is not null)).ToList();
        var par = OpenPar(cop, user.UnitId);
        return new FieldBriefing(user.Callsign, unit?.Status ?? UnitStatus.OutOfService, incident is null ? null : $"{incident.Number} {incident.Name ?? incident.Type.ToString()}",
            incident?.Address, incident?.Location.Y, incident?.Location.X, unit?.Tasking, orders,
            par is null ? null : new FieldPar(par.Id, par.Reason, unit?.Crew?.Members.Count ?? unit?.CrewSize ?? 0),
            session.Engine.SimTime, session.State == SessionState.Running);
    }

    private static ParCheck? OpenPar(ICopService cop, Guid unitId) =>
        cop.ParChecks.Where(p => p.Expected.ContainsKey(unitId) && !p.Responses.ContainsKey(unitId)).MaxBy(p => p.RequestedAt);

    // ---- What the field sends ----

    public Task<FieldResult> ReportAsync(SessionRuntime session, FieldUser user, string? text, double? latitude, double? longitude, double? accuracy) =>
        Run(session, async (cop, publish) =>
        {
            var claim = text?.Trim() ?? "";
            if (claim.Length == 0) return FieldResult.Fail("Write what you see.");
            if (claim.Length > MaxReport) return FieldResult.Fail($"Keep it under {MaxReport} characters.");
            var at = Location(latitude, longitude);
            await publish(new ReportReceived(Guid.NewGuid(), cop.FindUnit(user.UnitId)?.AssignedIncidentId, ReportSource.FieldUnit, user.Signature, claim,
                Confidence.Medium, VerificationStatus.Reported, at, at is null ? null : Math.Clamp(accuracy ?? 30, 1, 5000), user.UnitId));
            return new FieldResult(true, "Report sent to control.");
        });

    public Task<FieldResult> StatusAsync(SessionRuntime session, FieldUser user, UnitStatus status) =>
        Run(session, async (cop, publish) =>
        {
            if (!FieldStatuses.Contains(status)) return FieldResult.Fail($"{status} can't be set from the field.");
            if (cop.FindUnit(user.UnitId)?.Status == status) return new FieldResult(true, $"Already {status}.");
            await publish(new UnitStatusChanged(user.UnitId, status));
            return new FieldResult(true, $"{user.Callsign} {status}.");
        });

    /// <summary>
    /// ICS 211 check-in: the unit is on scene at its incident with this many crew. Command sees the status change and a
    /// check-in report; the position too, if the phone gave one.
    /// </summary>
    public Task<FieldResult> CheckInAsync(SessionRuntime session, FieldUser user, int crew, double? latitude, double? longitude) =>
        Run(session, async (cop, publish) =>
        {
            var unit = cop.FindUnit(user.UnitId);
            if (unit?.AssignedIncidentId is not { } incidentId || cop.FindIncident(incidentId) is not { } incident)
                return FieldResult.Fail($"{user.Callsign} isn't assigned to an incident: check with control before checking in.");
            if (crew is < 1 or > 60) return FieldResult.Fail("Give the number of crew with you.");
            if (unit.Status is not (UnitStatus.OnScene or UnitStatus.Operating)) await publish(new UnitStatusChanged(user.UnitId, UnitStatus.OnScene));
            var at = Location(latitude, longitude);
            if (at is { } here) await publish(new UnitPositionReported(user.UnitId, here, 0, 0, null));
            await publish(new ReportReceived(Guid.NewGuid(), incidentId, ReportSource.FieldUnit, user.Signature,
                $"Checked in at {incident.Number}: {user.Callsign} with a crew of {crew}, {user.Name} reporting.", Confidence.High, VerificationStatus.Confirmed,
                at, at is null ? null : 30, user.UnitId));
            return new FieldResult(true, $"Checked in at {incident.Number}.");
        });

    public Task<FieldResult> ParAsync(SessionRuntime session, FieldUser user, int accounted, IReadOnlyList<string>? missing) =>
        Run(session, async (cop, publish) =>
        {
            if (OpenPar(cop, user.UnitId) is not { } par) return FieldResult.Fail("No PAR is waiting for your unit.");
            var unit = cop.FindUnit(user.UnitId);
            var expected = unit?.Crew?.Members.Count ?? unit?.CrewSize ?? accounted;
            var names = (missing ?? []).Select(m => m.Trim()).Where(m => m.Length is > 0 and <= 60).Take(20).ToList();
            await publish(new ParReported(par.Id, user.UnitId, Math.Clamp(accounted, 0, 60), expected, names));
            return new FieldResult(true, names.Count == 0 && accounted >= expected ? "PAR: all accounted for." : "PAR sent: control knows who is missing.");
        });

    public Task<FieldResult> AcknowledgeAsync(SessionRuntime session, FieldUser user, Guid orderId, string? readBack) =>
        Run(session, async (cop, publish) =>
        {
            if (cop.Orders.FirstOrDefault(o => o.Id == orderId && o.TargetId == user.UnitId) is not { } order)
                return FieldResult.Fail("That order isn't for your unit.");
            if (order.AcknowledgedAt is not null) return new FieldResult(true, "Already acknowledged.");
            var text = string.IsNullOrWhiteSpace(readBack) ? $"Received: {order.Text}" : readBack.Trim()[..Math.Min(readBack.Trim().Length, 300)];
            await publish(new OrderAcknowledged(order.Id, text));
            return new FieldResult(true, "Order acknowledged.");
        });

    public Task<FieldResult> PositionAsync(SessionRuntime session, FieldUser user, double latitude, double longitude, double speedKph, double heading) =>
        Run(session, async (_, publish) =>
        {
            if (Location(latitude, longitude) is not { } at) return FieldResult.Fail("That isn't a position.");
            await publish(new UnitPositionReported(user.UnitId, at, Math.Clamp(speedKph, 0, 250), heading, null));
            return new FieldResult(true, "Position shared.");
        });

    private static GeoPoint? Location(double? latitude, double? longitude) =>
        latitude is { } lat && longitude is { } lon && double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180
            ? new GeoPoint(lat, lon)
            : null;

    /// <summary>Through the command gate, while the exercise runs, as perceived events from the field.</summary>
    private static async Task<FieldResult> Run(SessionRuntime session, Func<ICopService, Func<DomainEvent, Task>, Task<FieldResult>> action)
    {
        if (session.State != SessionState.Running)
            return FieldResult.Fail(session.State == SessionState.Lobby ? "The exercise hasn't started yet." : "The session has ended.");
        var cop = session.Services.GetRequiredService<ICopService>();
        return await session.Gate.RunAsync(() => action(cop, payload => session.Engine.PublishAsync(payload, EventVisibility.Perceived, EventSources.Field)));
    }
}
