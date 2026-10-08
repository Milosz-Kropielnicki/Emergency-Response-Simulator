using Emergency_Response_Simulator.Core.Events;

namespace Emergency_Response_Simulator.Data;

/// <summary>
/// Row in the <c>events</c> table. A database trigger rejects UPDATE, DELETE and TRUNCATE,
/// so the stream is append-only at the storage level, not just by convention.
/// </summary>
public class EventRecord
{
    public long Sequence { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset SimTime { get; set; }
    public DateTimeOffset WallTime { get; set; }
    public EventVisibility Visibility { get; set; }
    public required string Source { get; set; }

    /// <summary>Payload type name, duplicated out of the JSON so the AAR can filter without parsing it.</summary>
    public required string Type { get; set; }

    /// <summary>The <see cref="DomainEvent"/> as JSON (jsonb).</summary>
    public required string Payload { get; set; }

    public SimEvent ToSimEvent() => new()
    {
        Sequence = Sequence,
        SessionId = SessionId,
        SimTime = SimTime,
        WallTime = WallTime,
        Visibility = Visibility,
        Source = Source,
        Payload = EventJson.Deserialize(Payload),
    };
}
