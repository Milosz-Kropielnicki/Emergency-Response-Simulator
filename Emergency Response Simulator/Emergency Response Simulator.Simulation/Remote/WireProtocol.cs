using System.Text.Json;
using System.Text.Json.Serialization;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Emergency_Response_Simulator.Simulation.Remote;

// The contract between the simulation server and its clients (Phase 11). Shared by both sides so the shapes can't drift.

/// <summary>Where the hub lives and the groups connections are put in.</summary>
public static class SimulationHubProtocol
{
    public const string Path = "/hubs/simulation";

    /// <summary>Every participant of a session: perceived events, the clock, the reported traffic feed.</summary>
    public static string PerceivedGroup(Guid sessionId) => $"{sessionId}:perceived";

    /// <summary>A session's instructors only: truth events, world snapshots and live traffic.</summary>
    public static string TruthGroup(Guid sessionId) => $"{sessionId}:truth";

    /// <summary>The services a client can call through <c>Call</c>, by name. Each mirrors its interface 1:1.</summary>
    public static class Services
    {
        public const string C2 = "c2";
        public const string Iap = "iap";
        public const string Control = "control";
        public const string Aar = "aar";

        /// <summary>The instructor console (Phase 11 M4): the instructor only.</summary>
        public const string Instructor = "instructor";

        /// <summary>Live assessment (Phase 11 M6): observers and the instructor.</summary>
        public const string Assessment = "assessment";
    }
}

/// <summary>What the server pushes to a client (the strongly typed side of the hub).</summary>
public interface ISimulationClient
{
    /// <summary>Events in sequence order, already filtered to what this connection may see.</summary>
    Task Events(IReadOnlyList<SimEvent> events);

    /// <summary>
    /// Earlier events this connection may now see (Phase 11 M6: an observer just granted ground truth), to merge into the
    /// stream it already holds. They are older than its last event.
    /// </summary>
    Task Backfill(IReadOnlyList<SimEvent> events);

    Task Clock(ClockState clock);

    /// <summary>The traffic command's tools see (lagged): to everyone.</summary>
    Task TrafficFeed(TrafficState traffic);

    /// <summary>Ground truth for instructors: the world, about once a second.</summary>
    Task WorldSnapshot(WorldSnapshot snapshot);

    /// <summary>The traffic as it really is: instructors only.</summary>
    Task LiveTraffic(TrafficState traffic);

    /// <summary>The session moved on (started, ended), or this participant's view of it changed (truth granted).</summary>
    Task SessionChanged(SessionInfo session);
}

/// <summary>Where a networked session is in its life (Phase 11 M2).</summary>
public enum SessionState
{
    /// <summary>Open for joining; the clock hasn't started and commands wait for the instructor.</summary>
    Lobby,

    Running,

    /// <summary>Over: no commands or joins; the record stays for the review.</summary>
    Ended,
}

/// <summary>A scenario a session can be opened on.</summary>
public sealed record ScenarioInfo(string Key, string Name, string Description);

/// <summary>An instructor opens a session. The PIN is the server's, never kept in a client's configuration.</summary>
public sealed record CreateSessionRequest(string Scenario, string Title, string DisplayName, string Pin);

/// <summary>Someone joins a session by its code, under a name, in a role. Instructors also give the PIN.</summary>
public sealed record JoinSessionRequest(string Code, string DisplayName, UserRole Role, string? Pin = null);

/// <summary>
/// What a participant gets on joining: who they are in the session, and a token to come back as the same person after
/// the link drops (kept in memory by the client only).
/// </summary>
public sealed record JoinTicket(Guid SessionId, string Code, Guid ParticipantId, string ReconnectToken, string DisplayName, UserRole Role, bool Truth);

/// <summary>The session this connection is in, what it may see, and the clock.</summary>
public sealed record SessionInfo(Guid SessionId, string Title, string Code, SessionState State, bool Truth, ClockState Clock, bool CanCompareBranches);

/// <summary>The engine clock at a moment of real time; clients extrapolate from it while it runs.</summary>
public sealed record ClockState(DateTimeOffset SimTime, bool IsRunning, double TimeScale, DateTimeOffset SentAt);

/// <summary>A <see cref="TrafficSnapshot"/> on the wire.</summary>
public sealed record TrafficState(DateTimeOffset At, Dictionary<int, double> Factors, int[] DarkSignals)
{
    public static TrafficState From(TrafficSnapshot snapshot) =>
        new(snapshot.At, new Dictionary<int, double>(snapshot.Factors), [.. snapshot.DarkSignals]);

    public TrafficSnapshot ToSnapshot() => new(At, Factors, DarkSignals.ToHashSet());
}

/// <summary>
/// The wire format: <see cref="EventJson"/> (polymorphic events, readable enums) plus tuple fields and NTS geometries
/// (as WKT), which world snapshots and reports carry.
/// </summary>
public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(EventJson.Options));

    /// <summary>Applies the wire settings to options someone else owns (SignalR's JSON protocol).</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.IncludeFields = true; // value tuples in snapshots
        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
            options.Converters.Add(new JsonStringEnumConverter());
        if (!options.Converters.Any(c => c is GeometryWktConverterFactory))
            options.Converters.Add(new GeometryWktConverterFactory());
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals; // NaN and infinity in metrics
        return options;
    }
}

/// <summary>Writes NTS geometries as WKT strings in WGS 84 and reads them back.</summary>
public sealed class GeometryWktConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(Geometry).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : Geometry
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var geometry = new WKTReader(Wgs84.Factory.GeometryServices).Read(reader.GetString()!);
            geometry.SRID = Wgs84.Srid;
            return (T)geometry;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.AsText());
    }
}
