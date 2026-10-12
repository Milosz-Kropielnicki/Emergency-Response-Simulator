using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

public sealed record RatingChoice(AssessmentRating? Rating, string Label)
{
    public override string ToString() => Label;
}

public sealed record AreaChoice(Competency? Area, string Label)
{
    public override string ToString() => Label;
}

public sealed record NoteRow(string Time, string Participant, string Rating, AssessmentRating? Value, string Note, string About, string By, string Area = "");

/// <summary>
/// Observing and live assessment (Phase 11 M6), for observers and the instructor in a networked session:
/// <list type="bullet">
/// <item><b>Follow a participant</b>: the view switches to their role's workspace and goes wherever they act (the incident
/// or unit of each command they give).</item>
/// <item><b>Notes and ratings</b> on the rubric, tagged to the participant and, by default, their latest action. Every
/// assessor sees every note; trainees never do.</item>
/// </list>
/// </summary>
public partial class AssessmentViewModel : ObservableObject
{
    private readonly ICopService _cop;
    private readonly IEventStore _store;
    private readonly IAssessmentService _assessment;
    private readonly SimulationConnection? _connection;
    private readonly Dictionary<Guid, (long Sequence, string Text)> _latest = [];
    private Action<Guid?, Guid?>? _goTo;
    private Action<UserRole>? _layout;
    private Action<CommandResult, string?>? _report;

    public AssessmentViewModel(ICopService cop, IEventStore store, IAssessmentService assessment, SimulationConnection? connection)
    {
        _cop = cop;
        _store = store;
        _assessment = assessment;
        _connection = connection;
        store.Appended += (_, simEvent) => Application.Current?.Dispatcher.BeginInvoke(() => Seen(simEvent));
    }

    public IReadOnlyList<RatingChoice> Ratings { get; } =
    [
        new(null, "No rating"),
        .. Enum.GetValues<AssessmentRating>().Select(r => new RatingChoice(r, Rubric.Name(r))),
    ];

    /// <summary>The competency areas a note can assess (Phase 12), or none for an overall impression.</summary>
    public IReadOnlyList<AreaChoice> Areas { get; } =
    [
        new(null, "Overall (no area)"),
        .. Enum.GetValues<Competency>().Select(a => new AreaChoice(a, Rubric.Name(a))),
    ];

    /// <summary>Kept between notes: an observer often watches one area for a while.</summary>
    [ObservableProperty] private AreaChoice? _area;

    public ObservableCollection<ParticipantChoice> Players { get; } = [];
    public ObservableCollection<ParticipantChoice> FollowChoices { get; } = [];
    public ObservableCollection<NoteRow> Notes { get; } = [];

    [ObservableProperty] private bool _isAssessor;
    [ObservableProperty] private ParticipantChoice? _following;
    [ObservableProperty] private string _followStatus = "";
    [ObservableProperty] private ParticipantChoice? _noteFor;
    [ObservableProperty] private RatingChoice? _rating;
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _tagLatest = true;
    [ObservableProperty] private string _latestAction = "";
    [ObservableProperty] private string _tabHeader = "ASSESS";

    /// <summary>Where the main window goes (incident, unit), and which role's layout it shows, when following someone.</summary>
    public void Attach(Action<Guid?, Guid?> goTo, Action<UserRole> layout, Action<CommandResult, string?> report) =>
        (_goTo, _layout, _report) = (goTo, layout, report);

    partial void OnFollowingChanged(ParticipantChoice? value)
    {
        if (value?.Id is { } id)
        {
            NoteFor = Players.FirstOrDefault(p => p.Id == id) ?? NoteFor;
            if (_cop.Participants.FirstOrDefault(p => p.Id == id) is { } person) _layout?.Invoke(person.Role);
        }
        Describe();
    }

    partial void OnNoteForChanged(ParticipantChoice? value) => Describe();

    [RelayCommand]
    private async Task RecordAsync()
    {
        if (NoteFor?.Id is not { } who)
        {
            _report?.Invoke(CommandResult.Fail("Choose who the note is about."), null);
            return;
        }
        long? about = TagLatest && _latest.TryGetValue(who, out var latest) ? latest.Sequence : null;
        try
        {
            var result = await _assessment.RecordNoteAsync(who, NoteText, Rating?.Rating, about, Area?.Area);
            _report?.Invoke(result, "Note recorded");
            if (result.Succeeded)
            {
                NoteText = "";
                Rating = Ratings[0];
            }
        }
        catch (Exception ex)
        {
            _report?.Invoke(CommandResult.Fail(RemoteErrors.Message(ex)), null);
        }
    }

    /// <summary>A participant's command: remember it as their latest action, and follow them there if we're following.</summary>
    private void Seen(SimEvent simEvent)
    {
        if (simEvent.Payload is AssessmentNoteRecorded && _loaded && _noteEvents.All(n => n.Sequence != simEvent.Sequence))
        {
            _noteEvents.Add(simEvent);
            Refresh();
        }
        if (simEvent.ActorId is not { } actor || simEvent.Source is not (EventSources.C2 or EventSources.Iap)) return;
        if (EventDescriber.Describe(simEvent, Unit, Incident, Agency, Name) is not { } entry) return;
        _latest[actor] = (simEvent.Sequence, entry.Text);
        if (Following?.Id == actor)
        {
            FollowStatus = $"{Name(actor)} at {MainViewModel.Time(simEvent.SimTime)}: {entry.Text}";
            if (entry.IncidentId is not null || entry.UnitId is not null) _goTo?.Invoke(entry.IncidentId, entry.UnitId);
        }
        Describe();
    }

    private string? Unit(Guid id) => _cop.FindUnit(id)?.Callsign;
    private string? Incident(Guid id) => _cop.FindIncident(id)?.Number;
    private string? Agency(Guid id) => _cop.Agencies.FirstOrDefault(a => a.Id == id)?.Name;
    private string Name(Guid id) => _cop.Participants.FirstOrDefault(p => p.Id == id)?.DisplayName ?? "A participant";

    private void Describe() => LatestAction = NoteFor?.Id is { } who && _latest.TryGetValue(who, out var latest)
        ? $"Their latest action (#{latest.Sequence}): {latest.Text}"
        : "No action of theirs yet: the note will be general.";

    public void Refresh()
    {
        var me = _connection?.Ticket?.ParticipantId;
        var role = _cop.Participants.FirstOrDefault(p => p.Id == me)?.Role ?? _connection?.Ticket?.Role;
        IsAssessor = _connection is not null && role is { } r && Roles.IsAssessor(r);
        if (!IsAssessor) return;

        var players = _cop.Participants.Where(p => p.Present && Roles.IsPlayer(p.Role))
            .Select(p => new ParticipantChoice(p.Id, $"{p.DisplayName} ({Roles.Name(p.Role)})")).ToList();
        Sync(Players, players, () => NoteFor, value => NoteFor = value);
        Sync(FollowChoices, [new ParticipantChoice(null, "Nobody (free view)"), .. players], () => Following, value => Following = value);
        Rating ??= Ratings[0];

        // A followed player's role may change: keep showing their layout.
        if (Following?.Id is { } followed && _cop.Participants.FirstOrDefault(p => p.Id == followed) is { } person && person.Role != _followedRole)
        {
            _followedRole = person.Role;
            _layout?.Invoke(person.Role);
        }

        // The notes so far are read once; after that they arrive with the stream (assessors are sent every note).
        if (!_loaded)
        {
            _loaded = true;
            _noteEvents.AddRange(_store.ReadAsync(SessionId).ToBlockingEnumerable().Where(e => e.Payload is AssessmentNoteRecorded));
        }
        var notes = _noteEvents.AsEnumerable().Reverse().Take(100)
            .Select(e =>
            {
                var note = (AssessmentNoteRecorded)e.Payload;
                return new NoteRow(MainViewModel.Time(e.SimTime), Name(note.ParticipantId), note.Rating is { } rating ? Rubric.Name(rating) : "—", note.Rating,
                    note.Note, note.AboutSequence is { } about ? $"#{about}" : "", e.ActorId is { } by ? Name(by) : "",
                    note.Area is { } area ? Rubric.Name(area) : "");
            }).ToList();
        if (!notes.SequenceEqual(Notes))
        {
            Notes.Clear();
            foreach (var note in notes) Notes.Add(note);
        }
        TabHeader = notes.Count > 0 ? $"ASSESS · {notes.Count}" : "ASSESS";
    }

    private UserRole? _followedRole;
    private readonly List<SimEvent> _noteEvents = [];
    private bool _loaded;

    private Guid SessionId => _connection?.Session?.SessionId ?? Guid.Empty;

    private static void Sync(ObservableCollection<ParticipantChoice> target, IReadOnlyList<ParticipantChoice> rows, Func<ParticipantChoice?> get,
        Action<ParticipantChoice?> set)
    {
        if (target.SequenceEqual(rows)) return;
        var chosen = get()?.Id;
        target.Clear();
        foreach (var row in rows) target.Add(row);
        set(target.FirstOrDefault(r => r.Id == chosen) ?? target.FirstOrDefault());
    }
}
