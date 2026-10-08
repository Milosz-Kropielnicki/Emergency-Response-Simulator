using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emergency_Response_Simulator.Core.Events;

/// <summary>The single JSON format used for event payloads wherever they are stored or sent.</summary>
public static class EventJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(DomainEvent payload) =>
        JsonSerializer.Serialize(payload, Options);

    public static DomainEvent Deserialize(string json) =>
        JsonSerializer.Deserialize<DomainEvent>(json, Options)
        ?? throw new JsonException("Event payload was null.");
}
