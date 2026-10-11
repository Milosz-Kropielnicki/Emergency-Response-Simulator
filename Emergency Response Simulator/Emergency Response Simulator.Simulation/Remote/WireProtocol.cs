using System.Text.Json;
using System.Text.Json.Serialization;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Emergency_Response_Simulator.Simulation.Remote;

// The contract between the simulation server and its clients (Phase 11 M1). Shared by both sides so the shapes can't drift.

/// <summary>Where the hub lives and the groups connections are put in.</summary>
public static class SimulationHubProtocol
{
    public const string Path = "/hubs/simulation";

    /// <summary>Every connection: perceived events, the clock, the reported traffic feed.</summary>
    public const string PerceivedGroup = "perceived";

    /// <summary>Instructor connections only: truth events, world snapshots and live traffic.</summary>
    public const string TruthGroup = "truth";

    /// <summary>Query-string keys a client connects with (until Phase 11 M2 brings sessions and join codes).</summary>
    public const string RoleQuery = "role";
    public const string PinQuery = "pin";
    public const string InstructorRole = "Instructor";

    /// <summary>The services a client can call through <c>Call</c>, by name. Each mirrors its interface 1:1.</summary>
    public static class Services
    {
        public const string C2 = "c2";
        public const string Iap = "iap";
        public const string Control = "control";
        public const string Aar = "aar";
    }
}

/// <summary>What the server pushes to a client (the strongly typed side of the hub).</summary>
public interface ISimulationClient
{
    /// <summary>Events in sequence order, already filtered to what this connection may see.</summary>
    Task Events(IReadOnlyList<SimEvent> events);

    Task Clock(ClockState clock);

    /// <summary>The traffic command's tools see (lagged): to everyone.</summary>
    Task TrafficFeed(TrafficState traffic);

    /// <summary>Ground truth for instructors: the world, about once a second.</summary>
    Task WorldSnapshot(WorldSnapshot snapshot);

    /// <summary>The traffic as it really is: instructors only.</summary>
    Task LiveTraffic(TrafficState traffic);
}

/// <summary>Who the server decided this connection is, and which session it is in.</summary>
public sealed record SessionInfo(Guid SessionId, bool Truth, ClockState Clock, bool CanCompareBranches);

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
