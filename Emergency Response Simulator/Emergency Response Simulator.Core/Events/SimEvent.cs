namespace Emergency_Response_Simulator.Core.Events;

/// <summary>
/// Which picture of the world an event belongs to (Design Document §10.1).
/// </summary>
public enum EventVisibility
{
    /// <summary>
    /// Ground truth maintained by the simulation engine: the fire actually spread, the wind actually shifted.
    /// Never shown on the COP; visible to the engine, the instructor and the AAR.
    /// </summary>
    Truth,

    /// <summary>
    /// Information the organisation has received or acted on: calls, reports, AVL fixes, dispatch orders.
    /// The COP is built only from these.
    /// </summary>
    Perceived,
}

/// <summary>
/// One entry in the append-only event stream (Design Document §5.3, §10.2).
/// The COP, replays and the AAR are all derived from the ordered sequence of these.
/// </summary>
public sealed record SimEvent
{
    /// <summary>Position in the stream; assigned by the event store and strictly increasing per session.</summary>
    public long Sequence { get; init; }

    public Guid SessionId { get; init; }

    /// <summary>When it happened in simulation time.</summary>
    public DateTimeOffset SimTime { get; init; }

    /// <summary>Real time it was recorded, for auditing trainee input latency.</summary>
    public DateTimeOffset WallTime { get; init; }

    public EventVisibility Visibility { get; init; }

    /// <summary>Module that produced it, see <see cref="EventSources"/>.</summary>
    public required string Source { get; init; }

    public required DomainEvent Payload { get; init; }

    /// <summary>
    /// The participant whose command produced the event (Phase 11 M2), stamped by the server from the connection's
    /// identity, never from what a client sends. Null for the engine, scenarios, AI parties and standalone sessions.
    /// </summary>
    public Guid? ActorId { get; init; }

    /// <summary>The role that participant held when they gave the command.</summary>
    public Model.UserRole? ActorRole { get; init; }

    public string Type => Payload.GetType().Name;
}

/// <summary>An event that has not been given a sequence number yet.</summary>
public sealed record SimEventDraft(
    Guid SessionId,
    DateTimeOffset SimTime,
    EventVisibility Visibility,
    string Source,
    DomainEvent Payload,
    Guid? ActorId = null,
    Model.UserRole? ActorRole = null);

public static class EventSources
{
    public const string Engine = "engine";
    public const string Scenario = "scenario";
    public const string Cop = "cop";
    public const string C2 = "c2";
    public const string Iap = "iap";
    public const string Gis = "gis";
    public const string Avl = "avl";
    public const string Plume = "plume";
    public const string Comms = "comms";
    public const string Instructor = "instructor";

    /// <summary>Session lifecycle and participants (Phase 11).</summary>
    public const string Session = "session";

    /// <summary>Observers' and the instructor's assessment notes (Phase 11 M6).</summary>
    public const string Assessment = "assessment";

    /// <summary>Data from outside systems in a standard format: CAP, EDXL, NIEM, HL7, AVL feeds (Phase 20).</summary>
    public const string Integration = "integration";

    /// <summary>A crew's phone speaking for its unit through the mobile field companion (Phase 21).</summary>
    public const string Field = "field";
}
