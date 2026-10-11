namespace Emergency_Response_Simulator.Server;

/// <summary>Bound from the "Server" configuration section.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// The PIN an instructor client connects with to see ground truth. Empty: no instructor can connect. Kept in the
    /// server's configuration only, never in a client's.
    /// </summary>
    public string InstructorPin { get; set; } = "";

    /// <summary>
    /// Allow the in-memory event store when no database is configured: for development and tests only. A networked
    /// exercise needs PostgreSQL, so the record survives a server restart (decision D4).
    /// </summary>
    public bool AllowInMemoryStore { get; set; }

    /// <summary>How often instructors get the world snapshot and live traffic.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often every client gets the clock.</summary>
    public TimeSpan ClockInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a participant whose link dropped keeps their place before counting as gone.</summary>
    public TimeSpan DropGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a session's join code lets new people in (participants already in can always come back).</summary>
    public TimeSpan JoinCodeLifetime { get; set; } = TimeSpan.FromHours(12);
}
