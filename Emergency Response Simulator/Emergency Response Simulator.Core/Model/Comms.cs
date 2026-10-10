using System.Text.RegularExpressions;
using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>How a message came through (Design Document §13).</summary>
public enum CommsQuality
{
    Clear,

    /// <summary>Parts missing: a weak signal, a clipped start or end.</summary>
    Broken,

    /// <summary>Unintelligible: stepped on by another transmission, or at the edge of coverage.</summary>
    Garbled,
}

public enum ChannelKind
{
    Radio,

    /// <summary>The 999 line: calls from the public, answered by call-takers.</summary>
    EmergencyCalls,

    /// <summary>Messages and phone calls with other agencies, utilities and hospitals.</summary>
    Chat,
}

/// <param name="Agency">Whose crews work on it by default (null for shared channels).</param>
/// <param name="Monitored">Command listens to it without a patch.</param>
public sealed record ChannelInfo(string Id, string Name, ChannelKind Kind, AgencyType? Agency, bool Monitored, string Purpose);

/// <summary>
/// The radio plan (Design Document §13; ICS 205). Names match the IAP communications plan. Other services' radio
/// systems are not monitored: talking to their crews needs an interoperability patch.
/// </summary>
public static class RadioPlan
{
    public const string FireCommand = "FIRE CMD 1";
    public const string FireTac2 = "FIRE TAC 2";
    public const string FireTac3 = "FIRE TAC 3";
    public const string Ambulance = "AMB OPS 1";
    public const string Garda = "GARDA OPS";
    public const string InterAgency = "INTER-AGENCY 1";
    public const string Calls = "999";
    public const string Chat = "AGENCY CHAT";

    public static IReadOnlyList<ChannelInfo> Standard { get; } =
    [
        new(FireCommand, "Fire command", ChannelKind.Radio, AgencyType.Fire, true, "Fire control, incident commander and crews"),
        new(FireTac2, "Fire tactical 2", ChannelKind.Radio, AgencyType.Fire, true, "Fireground working channel"),
        new(FireTac3, "Fire tactical 3", ChannelKind.Radio, AgencyType.Fire, true, "Spare tactical channel"),
        new(Ambulance, "Ambulance ops", ChannelKind.Radio, AgencyType.Ems, true, "Ambulance control and crews"),
        new(Garda, "Garda ops", ChannelKind.Radio, AgencyType.Police, true, "Garda control and patrols"),
        new(InterAgency, "Inter-agency", ChannelKind.Radio, null, true, "Agency commanders at a major emergency"),
        new(Calls, "999 calls", ChannelKind.EmergencyCalls, null, true, "Calls from the public"),
        new(Chat, "Agency chat", ChannelKind.Chat, null, true, "Other agencies, utilities and hospitals"),
    ];

    public static ChannelInfo? Find(string id) => Standard.FirstOrDefault(c => c.Id == id);

    /// <summary>Where a unit's crew talks unless told otherwise: its agency's own system, or our ops channel.</summary>
    public static string DefaultChannel(UnitType type, string? agencyChannel) =>
        agencyChannel ?? ResourceGroups.AgencyFor(type) switch
        {
            AgencyType.Fire => FireCommand,
            AgencyType.Ems => Ambulance,
            AgencyType.Police => Garda,
            _ => InterAgency,
        };

    /// <summary>Speaking rate for working out airtime (about 150 words a minute, plus keying up).</summary>
    public static TimeSpan Airtime(string text) =>
        TimeSpan.FromSeconds(1.5 + Words(text) / 2.5);

    public static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// Plain-language radio procedure (Design Document §13): who you are calling, who you are, short and clear.
/// Command's own transmissions get these notes; they cost something real too (long messages hold the channel,
/// a call with no call sign gets no answer).
/// </summary>
public static partial class RadioDiscipline
{
    public const int MaxWords = 30;

    public static IReadOnlyList<string> Review(string text, string? to)
    {
        var notes = new List<string>();
        var words = RadioPlan.Words(text);
        if (to is not null && !Mentions(text, to))
            notes.Add($"No call sign: start with \"{to}, Control\".");
        if (words > MaxWords)
            notes.Add($"Long transmission ({words} words) ties up the channel: keep under {MaxWords}.");
        if (TenCode().IsMatch(text))
            notes.Add("Codes are not plain language: say what you mean.");
        if (Filler().IsMatch(text))
            notes.Add("Filler words: drop the \"um\" and \"please be advised\".");
        return notes;
    }

    /// <summary>Whether a transmission names a call sign ("Engine 4", "engine four" is not recognised).</summary>
    public static bool Mentions(string text, string callsign) =>
        text.Contains(callsign, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b10-\d{1,2}\b|\bcode \d\b", RegexOptions.IgnoreCase)]
    private static partial Regex TenCode();

    [GeneratedRegex(@"\b(um+|uh+|er+m?|please be advised)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();
}

/// <summary>Languages callers may speak (Design Document §13: limited English proficiency, interpreters).</summary>
public static class CallerLanguages
{
    /// <summary>Most common first languages other than English in Dublin, roughly by frequency.</summary>
    public static IReadOnlyList<string> Other { get; } = ["Polish", "Portuguese", "Romanian", "Lithuanian", "Mandarin", "Arabic"];
}

// ---- COP side: what command has heard and knows about its own communications ----

/// <summary>One message as command heard it on a channel, the 999 line or the agency chat.</summary>
public sealed class CommsEntry
{
    public Guid Id { get; init; }
    public required string ChannelId { get; init; }
    public required string From { get; init; }
    public string? To { get; init; }
    public required string Text { get; init; }
    public CommsQuality Quality { get; init; }
    public DateTimeOffset At { get; init; }
    public TimeSpan Duration { get; init; }
    public Guid? UnitId { get; init; }
    public bool FromControl { get; init; }

    /// <summary>Radio-discipline notes on command's own transmissions.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool RepeatRequested { get; set; }
}

/// <summary>A channel as heard from the control room: who is talking, how busy it is, what it is patched to.</summary>
public sealed class ChannelState
{
    public required ChannelInfo Info { get; init; }

    /// <summary>Airtime used in the recent past, for the congestion gauge.</summary>
    public List<(DateTimeOffset Start, TimeSpan Duration)> RecentAirtime { get; } = [];

    public DateTimeOffset BusyUntil { get; set; }
    public string? Talker { get; set; }

    /// <summary>Cleared for emergency traffic: routine traffic must wait (Phase 8: Mayday).</summary>
    public bool EmergencyTraffic { get; set; }

    /// <summary>Share of the last two minutes someone was transmitting.</summary>
    public double Utilisation(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(2);
        var used = RecentAirtime.Sum(a =>
        {
            var start = a.Start < now - window ? now - window : a.Start;
            var end = a.Start + a.Duration > now ? now : a.Start + a.Duration;
            return Math.Max(0, (end - start).TotalSeconds);
        });
        return Math.Clamp(used / window.TotalSeconds, 0, 1);
    }

    public bool IsBusy(DateTimeOffset now) => BusyUntil > now;
}

public sealed class ChannelPatch
{
    public Guid Id { get; init; }
    public required string ChannelA { get; init; }
    public required string ChannelB { get; init; }
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>Null while the technician is still setting it up.</summary>
    public DateTimeOffset? ActiveFrom { get; set; }
}

/// <summary>A 999 caller who gave up before a call-taker answered: their number is on screen for a callback.</summary>
public sealed class MissedCall
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    public TimeSpan Waited { get; init; }

    /// <summary>Where the network locates the phone, roughly.</summary>
    public GeoPoint? Location { get; init; }
    public double? AccuracyMeters { get; init; }
    public DateTimeOffset? CalledBackAt { get; set; }
}
