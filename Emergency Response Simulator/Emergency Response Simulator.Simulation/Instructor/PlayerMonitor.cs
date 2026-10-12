using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Instructor;

/// <summary>One participant as the instructor watches them.</summary>
/// <param name="IdleFor">Simulation time since their last command (or since they joined, if none).</param>
/// <param name="Waiting">What is on their role's desk now (Phase 12); <paramref name="Overdue"/> of it has waited too long or is urgent.</param>
/// <param name="Overloaded">More on their desk than one person keeps up with: time to help them, or to give a function to someone else.</param>
/// <param name="Oldest">The item on their desk that has waited longest, with how long.</param>
public sealed record PlayerActivity(Guid Id, string Name, UserRole Role, bool Present, int Commands, int Recent, DateTimeOffset? LastCommandAt,
    TimeSpan IdleFor, bool Idle, string? LastAction, int Waiting = 0, int Overdue = 0, bool Overloaded = false, string? Oldest = null);

/// <summary>A command someone gave, for the live activity feed.</summary>
public sealed record ActivityItem(DateTimeOffset At, Guid ParticipantId, string Who, string Text);

public sealed record ChannelLoad(string Name, double Utilisation, bool Busy, bool EmergencyTraffic);

public sealed record AlertWaiting(string Title, TimeSpan Age);

public sealed record BookmarkItem(DateTimeOffset At, string Note);

public sealed record MonitorReport(IReadOnlyList<PlayerActivity> Players, IReadOnlyList<ActivityItem> Feed, IReadOnlyList<AlertWaiting> Alerts,
    IReadOnlyList<ChannelLoad> Channels, IReadOnlyList<BookmarkItem> Bookmarks);

/// <summary>
/// What the instructor console's player monitor shows (Phase 11 M4), worked out from the session's events and the COP:
/// each participant's commands, how long since their last one (idle past <see cref="IdleAfter"/>), what waits on their
/// role's desk (overloaded past <see cref="OverloadedAt"/>, Phase 12), the live feed of who
/// did what, alerts nobody has acknowledged, how busy each radio channel is, and the instructor's bookmarks.
/// </summary>
public static class PlayerMonitor
{
    /// <summary>A player who has given no command for this long (simulation time) shows as idle.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    /// <summary>"Recent" commands: within this long.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);

    /// <summary>A desk this full, or with this many items overdue, shows its player as overloaded (Phase 12).</summary>
    public const int OverloadedAt = 6, OverdueAt = 3;

    public static MonitorReport Build(IReadOnlyList<SimEvent> events, ICopService cop, DateTimeOffset now, int feedLength = 50)
    {
        var participants = cop.Participants;
        string Name(Guid id) => participants.FirstOrDefault(p => p.Id == id)?.DisplayName ?? "A participant";
        string? Callsign(Guid id) => cop.Units.FirstOrDefault(u => u.Id == id)?.Callsign;
        string? Incident(Guid id) => cop.Incidents.FirstOrDefault(i => i.Id == id)?.Number;
        string? Agency(Guid id) => cop.Agencies.FirstOrDefault(a => a.Id == id)?.Name;

        // Commands: what a participant did through C2 or the IAP, as recorded with them as the actor.
        var commands = events.Where(e => e.ActorId is not null && e.Visibility == EventVisibility.Perceived
                                         && e.Source is EventSources.C2 or EventSources.Iap).ToList();
        var feed = new List<ActivityItem>();
        foreach (var command in commands)
            if (EventDescriber.Describe(command, Callsign, Incident, Agency, Name) is { } entry)
                feed.Add(new ActivityItem(command.SimTime, command.ActorId!.Value, $"{Name(command.ActorId.Value)} ({Roles.Name(command.ActorRole ?? UserRole.Observer)})", entry.Text));

        var present = participants.Where(p => p.Present).Select(p => p.Role).ToHashSet();
        var players = participants.Where(p => p.Role != UserRole.Instructor).Select(p =>
        {
            var mine = commands.Where(c => c.ActorId == p.Id).ToList();
            var last = mine.LastOrDefault();
            var idleFor = now - (last?.SimTime ?? p.JoinedAt);
            if (idleFor < TimeSpan.Zero) idleFor = TimeSpan.Zero;
            // Watching isn't idling: observers never show as idle; nor does anyone who has left.
            var idle = p.Present && p.Role != UserRole.Observer && idleFor >= IdleAfter;
            // Their desk: what waits for their role now, including whatever falls to an IC from unfilled roles.
            var desk = p.Present && Roles.IsPlayer(p.Role) ? RoleDesk.For(p.Role, cop, present.Contains, p.Id) : [];
            var overdue = desk.Count(d => d.Urgency != DeskUrgency.Routine);
            var oldest = desk.MinBy(d => d.Since);
            return new PlayerActivity(p.Id, p.DisplayName, p.Role, p.Present, mine.Count, mine.Count(c => now - c.SimTime <= RecentWindow),
                last?.SimTime, idleFor, idle, feed.LastOrDefault(f => f.ParticipantId == p.Id)?.Text,
                desk.Count, overdue, desk.Count >= OverloadedAt || overdue >= OverdueAt,
                oldest is null ? null : $"{oldest.Text} ({(int)oldest.Age(now).TotalMinutes} min)");
        }).ToList();

        var alerts = cop.Alerts.Where(a => a.AcknowledgedAt is null).OrderBy(a => a.RaisedAt)
            .Select(a => new AlertWaiting(a.Title, now - a.RaisedAt)).ToList();
        var channels = cop.Channels.Select(c => new ChannelLoad(c.Info.Name, c.Utilisation(now), c.IsBusy(now), c.EmergencyTraffic))
            .OrderByDescending(c => c.Utilisation).ToList();
        var bookmarks = events.Where(e => e.Payload is InstructorBookmarked)
            .Select(e => new BookmarkItem(e.SimTime, ((InstructorBookmarked)e.Payload).Note)).ToList();

        return new MonitorReport(players, [.. feed.AsEnumerable().Reverse().Take(feedLength)], alerts, channels, bookmarks);
    }
}
