using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Events;

/// <summary>Who is giving a command: a participant in a networked session, in the role they hold (Phase 11 M2).</summary>
public sealed record CommandActor(Guid ParticipantId, UserRole Role, string Name);

/// <summary>The participant on whose behalf the current command runs, if any.</summary>
public interface ICommandContext
{
    CommandActor? Actor { get; }
}

/// <summary>
/// The ambient command context: set by the server around each call from a connection (from that connection's
/// identity, never from anything the client sends) and read by the engine when it records the resulting events. It
/// flows with async calls, so a service several awaits deep still sees who asked. Unset in a standalone session and
/// on the engine's own loop, where events have no actor.
/// </summary>
public sealed class CommandContext : ICommandContext
{
    private static readonly AsyncLocal<CommandActor?> Current = new();

    public static CommandContext Ambient { get; } = new();

    public CommandActor? Actor => Current.Value;

    /// <summary>Runs what follows (until disposed) on behalf of <paramref name="actor"/>.</summary>
    public static IDisposable As(CommandActor? actor)
    {
        var previous = Current.Value;
        Current.Value = actor;
        return new Restore(previous);
    }

    private sealed class Restore(CommandActor? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
