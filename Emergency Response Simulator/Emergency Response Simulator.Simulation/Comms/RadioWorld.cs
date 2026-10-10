using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Simulation.Comms;

public enum TransmissionKind
{
    /// <summary>Half-duplex radio: takes airtime, can queue, collide, garble or be lost.</summary>
    Voice,

    /// <summary>Phone or message between control rooms and organisations: a short delay, nothing else.</summary>
    Chat,
}

/// <summary>Something someone is trying to say, waiting for (or getting) its turn on the air.</summary>
public sealed class Transmission
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TransmissionKind Kind { get; init; } = TransmissionKind.Voice;
    public required string Channel { get; set; }
    public required string From { get; init; }
    public Guid? UnitId { get; init; }
    public string? To { get; init; }
    public required string Text { get; init; }

    /// <summary>What reaches the COP if the message gets through (adjusted when it is broken or garbled).</summary>
    public DomainEvent? Payload { get; init; }

    public bool FromControl { get; init; }

    /// <summary>Command's push-to-talk hold time; shorter than the message needs and the end is cut off.</summary>
    public double? HeldSeconds { get; init; }

    /// <summary>Command keyed up itself (a radio call), rather than waiting for a gap like queued traffic.</summary>
    public bool Keyed { get; init; }

    /// <summary>The order this transmission carries, so the recipient's hearing it can be recorded.</summary>
    public Guid? OrderId { get; init; }

    /// <summary>The PAR or evacuation signal this transmission calls, so crews hearing it can be recorded.</summary>
    public Guid? ParId { get; init; }

    /// <summary>Higher goes first: emergency traffic over routine.</summary>
    public int Priority { get; init; }

    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>Not before this time (replies after a pause, retries, chat typing time).</summary>
    public DateTimeOffset NotBefore { get; set; }

    public int Attempts { get; set; }
}

/// <summary>A 999 caller waiting for a call-taker.</summary>
public sealed class PendingCall
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Caller { get; init; }
    public required string Summary { get; init; }
    public GeoPoint? Location { get; init; }
    public double? AccuracyMeters { get; init; }

    /// <summary>The caller's language when they have little English, else null.</summary>
    public string? Language { get; init; }

    public bool Mobile { get; init; } = true;
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>How long they will hold before hanging up.</summary>
    public TimeSpan Patience { get; init; } = TimeSpan.FromSeconds(90);
}

public sealed record DeadZone(Guid Id, GeoPoint Centre, double RadiusMeters, string Description)
{
    public bool Covers(GeoPoint point) => GeoMath.DistanceMeters(Centre, point) <= RadiusMeters;
}

public sealed class MastState
{
    public required Guid SiteId { get; init; }
    public DateTimeOffset? OnBatterySince { get; set; }
    public bool Down { get; set; }
    public string? DownCause { get; set; }
}

public sealed class PatchState
{
    public required Guid Id { get; init; }
    public required string ChannelA { get; init; }
    public required string ChannelB { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public bool Active { get; set; }
}

/// <summary>
/// The true state of communications (Design Document §13): what is waiting to be said, who is on air, which
/// channels are patched, where coverage is missing, which masts are down and who is holding on the 999 line.
/// Advanced by <c>CommsSystem</c>; part of <see cref="State.WorldState"/>.
/// </summary>
public sealed class RadioWorld
{
    /// <summary>
    /// True once a comms system is running. Without one (tests of other systems, the scripted-only world),
    /// messages go straight to the COP as they did before Phase 7.
    /// </summary>
    public bool Active { get; set; }

    /// <summary>New transmissions from this tick, picked up by the comms system.</summary>
    public List<Transmission> Outbox { get; } = [];

    /// <summary>Transmissions waiting for their turn, per airtime group (patched channels share one).</summary>
    public List<Transmission> Queue { get; } = [];

    /// <summary>When each channel is next free, by channel id.</summary>
    public Dictionary<string, DateTimeOffset> FreeAt { get; } = [];

    /// <summary>Who is on air on each channel and until when (for collisions with command keying up).</summary>
    public Dictionary<string, (string Talker, DateTimeOffset Until)> OnAir { get; } = [];

    /// <summary>Airtime used recently per channel, for congestion effects.</summary>
    public Dictionary<string, List<(DateTimeOffset Start, TimeSpan Duration)>> Airtime { get; } = [];

    /// <summary>Messages sent, by id, so a "say again" can resend them.</summary>
    public Dictionary<Guid, Transmission> Sent { get; } = [];

    public HashSet<Guid> RepeatRequests { get; } = [];

    public Dictionary<Guid, PatchState> Patches { get; } = [];
    public List<DeadZone> DeadZones { get; } = [];
    public Dictionary<Guid, MastState> Masts { get; } = [];

    public List<PendingCall> CallQueue { get; } = [];

    /// <summary>Missed callers, by call id, for callbacks.</summary>
    public Dictionary<Guid, PendingCall> Missed { get; } = [];

    public Dictionary<Guid, DateTimeOffset> Callbacks { get; } = [];

    /// <summary>Recent transmissions nobody heard, newest last (for the instructor).</summary>
    public List<(DateTimeOffset At, string From, string Text, string Reason)> RecentLost { get; } = [];

    /// <summary>Channels cleared for emergency traffic: only priority traffic goes out (Phase 8).</summary>
    public HashSet<string> EmergencyTraffic { get; } = [];

    /// <summary>Whether the channel, or one patched to it, is cleared for emergency traffic.</summary>
    public bool UnderEmergencyTraffic(string channel) =>
        EmergencyTraffic.Count > 0 && Linked(channel).Any(EmergencyTraffic.Contains);

    /// <summary>Patched-together channels share airtime: this is the group's key (the lowest channel id in it).</summary>
    public string AirtimeGroup(string channel)
    {
        var group = Linked(channel);
        return group.Min(StringComparer.Ordinal)!;
    }

    /// <summary>Every channel joined to this one through working patches, including itself.</summary>
    public IReadOnlyCollection<string> Linked(string channel)
    {
        var seen = new HashSet<string> { channel };
        var frontier = new Queue<string>([channel]);
        while (frontier.TryDequeue(out var current))
        {
            foreach (var patch in Patches.Values.Where(p => p.Active))
            {
                var other = patch.ChannelA == current ? patch.ChannelB : patch.ChannelB == current ? patch.ChannelA : null;
                if (other is not null && seen.Add(other)) frontier.Enqueue(other);
            }
        }
        return seen;
    }

    public double Utilisation(string channel, DateTimeOffset now)
    {
        if (!Airtime.TryGetValue(AirtimeGroup(channel), out var used)) return 0;
        var window = TimeSpan.FromMinutes(2);
        var seconds = used.Sum(a =>
        {
            var start = a.Start < now - window ? now - window : a.Start;
            var end = a.Start + a.Duration > now ? now : a.Start + a.Duration;
            return Math.Max(0, (end - start).TotalSeconds);
        });
        return Math.Clamp(seconds / window.TotalSeconds, 0, 1);
    }
}
