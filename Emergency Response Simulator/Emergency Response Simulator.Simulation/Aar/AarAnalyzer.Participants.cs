using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Who took part in a networked session (Phase 11 M2) and how each one's own decisions rated. Attribution comes from the
/// actor the server stamped on each event, so a command is credited to whoever really gave it.
/// </summary>
public sealed partial class AarAnalyzer
{
    private sealed class Person(string name, DateTimeOffset joinedAt)
    {
        public string Name { get; set; } = name;
        public DateTimeOffset JoinedAt { get; } = joinedAt;
        public DateTimeOffset? LeftAt { get; set; }
        public DateTimeOffset? PresentSince { get; set; } = joinedAt;
        public TimeSpan Present { get; set; }
        public List<UserRole> Roles { get; } = [];
    }

    private readonly Dictionary<Guid, Person> _people = [];
    private readonly List<(SimEvent Event, RoleHandedOver Handover)> _roleHandovers = [];

    /// <summary>
    /// Role handovers during the exercise (Phase 11 M3), judged as crew handovers are (Phase 8): a full briefing is a
    /// strength; a quick changeover that left the new holder without the role's open business is not.
    /// </summary>
    private IEnumerable<AarFinding> HandoverFindings() => _roleHandovers.Select(h =>
    {
        var (from, to) = (Name(h.Handover.FromParticipantId), Name(h.Handover.ToParticipantId));
        var at = h.Event.SimTime.ToString("HH:mm");
        var items = h.Handover.Items ?? [];
        return h.Handover.Briefed
            ? new AarFinding(true, $"{at} {from} briefed {to} fully on taking over {Roles.Name(h.Handover.Role)}.")
            : new AarFinding(items.Count == 0, $"{at} {from} handed {Roles.Name(h.Handover.Role)} to {to} in a quick changeover" +
                (items.Count == 0 ? ", with nothing open to pass on." : $"; {to} was never told: {string.Join("; ", items)}."));
    });

    private string Name(Guid participantId) => _people.GetValueOrDefault(participantId)?.Name ?? "A participant";

    private void TrackParticipant(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        switch (simEvent.Payload)
        {
            case ParticipantJoined e:
                if (!_people.TryGetValue(e.ParticipantId, out var person))
                    _people[e.ParticipantId] = person = new Person(e.DisplayName, at);
                person.Name = e.DisplayName;
                person.LeftAt = null;
                person.PresentSince ??= at;
                AddRole(person, e.Role);
                break;
            case ParticipantLeft e when _people.TryGetValue(e.ParticipantId, out var leaving):
                leaving.LeftAt = at;
                if (leaving.PresentSince is { } since) leaving.Present += at - since;
                leaving.PresentSince = null;
                break;
            case RoleAssigned e when _people.TryGetValue(e.ParticipantId, out var assigned):
                AddRole(assigned, e.Role);
                break;
            case RoleHandedOver e:
                if (_people.TryGetValue(e.ToParticipantId, out var taking)) AddRole(taking, e.Role);
                if (_people.TryGetValue(e.FromParticipantId, out var giving)) AddRole(giving, e.FromNewRole);
                if (e.HandoverId is not null) _roleHandovers.Add((simEvent, e));
                break;
        }

        static void AddRole(Person person, UserRole role)
        {
            if (person.Roles.LastOrDefault() != role || person.Roles.Count == 0) person.Roles.Add(role);
        }
    }

    /// <summary>"Pat Byrne (Incident Commander)" for an event someone's command produced, else null.</summary>
    private string? ActorLabel(SimEvent simEvent) => simEvent.ActorId is { } id
        ? $"{_people.GetValueOrDefault(id)?.Name ?? "Unknown participant"}" + (simEvent.ActorRole is { } role ? $" ({Roles.Name(role)})" : "")
        : null;

    private List<AarParticipant> Participants() => [.. _people.Select(p =>
    {
        var (id, person) = (p.Key, p.Value);
        var mine = _decisions.Where(d => d.ActorId == id).ToList();
        var rated = mine.Where(d => d.Rating != Rating.NotApplicable).ToList();
        var timed = mine.Where(d => d.ResponseTime is not null).Select(d => d.ResponseTime!.Value.TotalSeconds).ToList();
        var present = person.Present + (person.PresentSince is { } since ? End - since : TimeSpan.Zero);
        var focus = string.Join(", ", mine.GroupBy(d => d.Category).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} {g.Count()}"));
        return new AarParticipant(id, person.Name, string.Join(" → ", person.Roles.Select(Roles.Name)), person.JoinedAt, person.LeftAt, present,
            mine.Count, rated.Count(d => d.Rating == Rating.Good), rated.Count(d => d.Rating == Rating.Fair), rated.Count(d => d.Rating == Rating.Poor),
            timed.Count == 0 ? null : TimeSpan.FromSeconds(timed.Average()),
            rated.Count == 0 ? null : (int)Math.Round(100 * rated.Average(d => d.Rating switch { Rating.Good => 1.0, Rating.Fair => 0.5, _ => 0.0 })),
            focus.Length == 0 ? "No commands" : focus);
    }).OrderBy(p => p.JoinedAt)];
}
