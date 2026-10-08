using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels.Iap;

/// <summary>
/// The IAP Builder (Design Document §8.4): structured forms for one plan version of the selected incident,
/// from operational period and objectives to the ICS 203–208 sections, with a live compliance check, an
/// org chart and the approval workflow. Edits are local until saved; everything else goes through
/// <see cref="IIapService"/> and so into the event stream.
/// </summary>
public sealed partial class IapBuilderViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IIapService _iap;
    private readonly CopView _cop;
    private readonly ISimulationControl _clock;
    private readonly DispatcherTimer _checkTimer;

    private Guid? _incidentId;
    private string _signature = "";
    private Guid? _loadedPlanId;
    private DateTimeOffset _loadedSavedAt;
    private IncidentActionPlan? _plan;
    private bool _syncing;

    // Where to land after a command creates a period or version.
    private Guid? _wantPeriodId;
    private Guid? _wantPlanId;

    public IapBuilderViewModel(MainViewModel main, IIapService iap, CopView cop, ISimulationControl clock)
    {
        _main = main;
        _iap = iap;
        _cop = cop;
        _clock = clock;
        _checkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            RunCheck();
        };

        foreach (var role in Enum.GetValues<IcsRole>())
            Positions.Add(new PositionEntry(this, role, ""));
        Objectives.CollectionChanged += (_, _) => ObjectivesChanged();
        Groups.CollectionChanged += (_, _) => GroupsChanged();
        Assignments.CollectionChanged += (_, _) => MarkDirty();
        Channels.CollectionChanged += (_, _) => MarkDirty();
        Hospitals.CollectionChanged += (_, _) => MarkDirty();
        Hazards.CollectionChanged += (_, _) => MarkDirty();
        Ppe.CollectionChanged += (_, _) => MarkDirty();
        _newPeriodLength = PeriodLengths[1];
    }

    /// <summary>True while the builder window is showing; the builder does no work while closed.</summary>
    public bool IsOpen { get; private set; }

    public void Opened()
    {
        IsOpen = true;
        _signature = "";
        Load(_main.SelectedIncidentId is { } id ? _cop.FindIncident(id) : null);
    }

    public void Closed() => IsOpen = false;

    // ---- Header ----

    [ObservableProperty] private bool _hasIncident;

    /// <summary>Showing a replayed COP: plans are read-only and no new periods can be started.</summary>
    [ObservableProperty] private bool _isReplay;
    [ObservableProperty] private string _incidentLabel = "No incident selected";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageFailed;

    public IReadOnlyList<SectionItem> Sections { get; } =
    [
        new("Period & situation", IapSection.Period),
        new("Objectives · ICS 202", IapSection.Objectives),
        new("Organisation · ICS 203", IapSection.Organisation),
        new("Assignments · ICS 204", IapSection.Assignments),
        new("Communications · ICS 205", IapSection.Communications),
        new("Medical · ICS 206", IapSection.Medical),
        new("Safety · ICS 208", IapSection.Safety),
        new("Review & approval", null),
    ];

    [ObservableProperty] private int _selectedSection;

    private static int SectionIndex(IapSection section) => (int)section; // Period..Safety map to 0..6

    // ---- Operational periods ----

    public ObservableCollection<PeriodRow> Periods { get; } = [];
    [ObservableProperty] private PeriodRow? _selectedPeriod;
    [ObservableProperty] private string _periodDetail = "";

    public IReadOnlyList<PeriodLength> PeriodLengths { get; } =
    [
        new(TimeSpan.FromMinutes(30), "30 min"),
        new(TimeSpan.FromHours(1), "1 hour"),
        new(TimeSpan.FromHours(2), "2 hours"),
        new(TimeSpan.FromHours(4), "4 hours"),
        new(TimeSpan.FromHours(8), "8 hours"),
        new(TimeSpan.FromHours(12), "12 hours"),
    ];

    [ObservableProperty] private string _newPeriodStart = "";
    [ObservableProperty] private PeriodLength _newPeriodLength;
    [ObservableProperty] private string _newPeriodFocus = "";
    [ObservableProperty] private string _newPeriodTitle = "START OPERATIONAL PERIOD 1";

    partial void OnSelectedPeriodChanged(PeriodRow? value)
    {
        if (_syncing) return;
        SyncVersions();
        SyncPlan();
    }

    // ---- Versions ----

    public ObservableCollection<VersionRow> Versions { get; } = [];
    [ObservableProperty] private VersionRow? _selectedVersion;

    partial void OnSelectedVersionChanged(VersionRow? value)
    {
        if (_syncing) return;
        SyncPlan();
    }

    // ---- The plan being shown ----

    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _planTitle = "";
    [ObservableProperty] private string _planStatus = "";
    [ObservableProperty] private IapStatus _status;
    [ObservableProperty] private string? _returnNote;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canSubmit;
    [ObservableProperty] private bool _canDecide;
    [ObservableProperty] private bool _canBrief;
    [ObservableProperty] private bool _canNewVersion;

    public bool IsReadOnly => !IsEditable;

    partial void OnIsEditableChanged(bool value) => OnPropertyChanged(nameof(IsReadOnly));

    /// <summary>True while rows are being filled from a plan, so the fill doesn't count as an edit.</summary>
    public bool IsLoading { get; private set; }

    [ObservableProperty] private string _commandersIntent = "";
    [ObservableProperty] private string _situationSummary = "";

    partial void OnCommandersIntentChanged(string value) => MarkDirty();
    partial void OnSituationSummaryChanged(string value) => MarkDirty();

    // ICS 202
    public ObservableCollection<StrategicEntry> Objectives { get; } = [];
    public IReadOnlyList<string> StrategicSuggestions => IapTemplates.StrategicObjectives;
    public IReadOnlyList<ObjectiveStatus> ProgressOptions { get; } = Enum.GetValues<ObjectiveStatus>();
    [ObservableProperty] private string _newStrategic = "";

    // ICS 203
    public ObservableCollection<PositionEntry> Positions { get; }  = [];
    public ObservableCollection<GroupEntry> Groups { get; } = [];
    public IReadOnlyList<IcsGroupKind> GroupKinds { get; } = Enum.GetValues<IcsGroupKind>();
    [ObservableProperty] private OrgChart _chart = OrgChart.Empty;

    // ICS 204
    public ObservableCollection<AssignmentEntry> Assignments { get; } = [];
    public ObservableCollection<ObjectiveOption> ObjectiveOptions { get; } = [new(null, "—")];
    public ObservableCollection<string> GroupNames { get; } = [""];
    public ObservableCollection<string> TacticSuggestions { get; } = [];
    public ObservableCollection<UnitOption> AddableUnits { get; } = [];
    [ObservableProperty] private UnitOption? _unitToAdd;

    // ICS 205
    public ObservableCollection<ChannelEntry> Channels { get; } = [];
    [ObservableProperty] private string _commsNotes = "";
    partial void OnCommsNotesChanged(string value) => MarkDirty();

    // ICS 206
    public ObservableCollection<HospitalEntry> Hospitals { get; } = [];
    [ObservableProperty] private string _casualtyClearingStation = "";
    [ObservableProperty] private string _ambulanceLoadingPoint = "";
    [ObservableProperty] private string _medicalLead = "";
    [ObservableProperty] private string _emergencyProcedures = "";
    partial void OnCasualtyClearingStationChanged(string value) => MarkDirty();
    partial void OnAmbulanceLoadingPointChanged(string value) => MarkDirty();
    partial void OnMedicalLeadChanged(string value) => MarkDirty();
    partial void OnEmergencyProceduresChanged(string value) => MarkDirty();

    // ICS 208
    public ObservableCollection<HazardEntry> Hazards { get; } = [];
    public ObservableCollection<TextEntry> Ppe { get; } = [];
    public IReadOnlyList<string> PpeSuggestions => IapTemplates.Ppe;
    [ObservableProperty] private string _newPpe = "";
    [ObservableProperty] private string _safetyMessage = "";
    [ObservableProperty] private string _accountability = "";
    partial void OnSafetyMessageChanged(string value) => MarkDirty();
    partial void OnAccountabilityChanged(string value) => MarkDirty();

    // Review
    public ObservableCollection<IssueRow> Issues { get; } = [];
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private string _documentText = "";
    [ObservableProperty] private string _returnComments = "";

    /// <summary>End-of-period reassessment of the plan in force for the selected period (§8.6–8.7).</summary>
    public ObservableCollection<string> Findings { get; } = [];
    [ObservableProperty] private string _findingsTitle = "";

    // ---- Following the COP ----

    /// <summary>Called on every COP refresh and when the selected incident changes.</summary>
    public void Load(Incident? incident)
    {
        if (incident?.Id != _incidentId)
        {
            _incidentId = incident?.Id;
            _signature = "";
            _loadedPlanId = null;
            _wantPeriodId = _wantPlanId = null;
            IsDirty = false;
        }

        HasIncident = incident is not null;
        IsReplay = _cop.IsReplay;
        IncidentLabel = incident is null ? "No incident selected"
            : $"{incident.Number} · {incident.Name ?? EventDescriber.Humanize(incident.Type)}" + (incident.Address is { } a ? $" · {a}" : "");
        if (!IsOpen) return;

        if (incident is null)
        {
            WithoutSelectionEvents(() =>
            {
                Periods.Clear();
                Versions.Clear();
                SelectedPeriod = null;
                SelectedVersion = null;
            });
            ShowPlan(null);
            return;
        }

        var signature = Signature(incident.Id);
        if (signature != _signature)
        {
            _signature = signature;
            SyncPeriods(incident);
            SyncVersions();
            SyncPlan();
        }
        UpdateLive(incident);
    }

    /// <summary>Changes in planning state; COP refreshes that don't touch it (AVL fixes) skip the resync.</summary>
    private string Signature(Guid incidentId)
    {
        var periods = _cop.PeriodsOf(incidentId).Select(p => $"{p.Id}:{p.End.Ticks}");
        var plans = _cop.ActionPlans.Where(p => p.IncidentId == incidentId)
            .Select(p => $"{p.Id}:{p.Status}:{p.LastSavedAt.Ticks}:{p.BriefedAt?.Ticks}:{p.ReturnComments}");
        var progress = _cop.ObjectiveProgress.Select(p => $"{p.Key}:{p.Value}");
        return string.Join("|", periods.Concat(plans).Concat(progress)) + $"|{_cop.IsReplay}";
    }

    private void WithoutSelectionEvents(Action action)
    {
        _syncing = true;
        try { action(); }
        finally { _syncing = false; }
    }

    private void SyncPeriods(Incident incident)
    {
        var periods = _cop.PeriodsOf(incident.Id);
        var now = _clock.SimTime;
        var keep = _wantPeriodId ?? SelectedPeriod?.Id ?? _cop.CurrentPeriod(incident.Id, now)?.Id;
        _wantPeriodId = null;

        WithoutSelectionEvents(() =>
        {
            Periods.Clear();
            foreach (var period in periods)
                Periods.Add(new PeriodRow(period.Id, period.Number,
                    $"Period {period.Number} · {period.Window}" + (period.Contains(now) ? " (current)" : period.End <= now ? " (ended)" : "")));
            SelectedPeriod = Periods.FirstOrDefault(p => p.Id == keep) ?? Periods.LastOrDefault();
        });

        var last = periods.LastOrDefault();
        NewPeriodTitle = $"START OPERATIONAL PERIOD {periods.Count + 1}";
        NewPeriodStart = (last is null ? now : last.End > now ? last.End : now).ToLocalTime().ToString("HH:mm");
        if (last is not null && PeriodLengths.FirstOrDefault(l => l.Length == last.End - last.Start) is { } same)
            NewPeriodLength = same;
    }

    private void SyncVersions()
    {
        var versions = SelectedPeriod is { } period ? _cop.VersionsOf(period.Id) : [];
        var keep = _wantPlanId ?? SelectedVersion?.Id;
        _wantPlanId = null;

        WithoutSelectionEvents(() =>
        {
            Versions.Clear();
            foreach (var plan in versions.Reverse())
                Versions.Add(new VersionRow(plan.Id, plan.Version, $"v{plan.Version} · {Describe(plan.Status)}", plan.Status));
            SelectedVersion = Versions.FirstOrDefault(v => v.Id == keep) ?? Versions.FirstOrDefault();
        });

        if (SelectedPeriod is { } selected && _cop.OperationalPeriods.FirstOrDefault(p => p.Id == selected.Id) is { } model)
            PeriodDetail = $"{model.Start.ToLocalTime():ddd HH:mm} to {model.End.ToLocalTime():HH:mm}" +
                           (model.Focus is { } focus ? $" · {focus}" : "");
        else
            PeriodDetail = "";
        CanNewVersion = SelectedPeriod is not null && !_cop.IsReplay
                        && versions.All(v => v.Status is IapStatus.Approved or IapStatus.Superseded);
    }

    private static string Describe(IapStatus status) => status switch
    {
        IapStatus.PendingApproval => "awaiting approval",
        _ => EventDescriber.Humanize(status).ToLowerInvariant(),
    };

    private void SyncPlan()
    {
        var plan = SelectedVersion is { } version ? _cop.ActionPlans.FirstOrDefault(p => p.Id == version.Id) : null;
        ShowPlan(plan);
    }

    private void ShowPlan(IncidentActionPlan? plan)
    {
        _plan = plan;
        HasPlan = plan is not null;
        if (plan is null)
        {
            _loadedPlanId = null;
            IsEditable = CanSubmit = CanDecide = CanBrief = false;
            PlanTitle = PlanStatus = "";
            ReturnNote = null;
            LoadContent(new IapContent());
            IsDirty = false;
            Findings.Clear();
            FindingsTitle = "";
            return;
        }

        IsEditable = plan.IsEditable && !_cop.IsReplay;
        Status = plan.Status;
        PlanTitle = $"Period {plan.OperationalPeriod?.Number} · version {plan.Version}";
        PlanStatus = IapDocument.StatusLine(plan);
        ReturnNote = plan.Status == IapStatus.Draft && plan.ReturnComments is { } comments ? $"Returned by {plan.ReturnedBy}: {comments}" : null;
        CanSubmit = IsEditable;
        CanDecide = plan.Status == IapStatus.PendingApproval && !_cop.IsReplay;
        CanBrief = plan.Status == IapStatus.Approved && !_cop.IsReplay;

        var reload = plan.Id != _loadedPlanId
                     || (!IsDirty && plan.LastSavedAt != _loadedSavedAt)
                     || (IsDirty && !IsEditable);
        if (reload)
        {
            _loadedPlanId = plan.Id;
            _loadedSavedAt = plan.LastSavedAt;
            LoadContent(plan.Content.Clone());
            IsDirty = false;
        }
        RunCheck();
    }

    /// <summary>Fills every section from a plan document.</summary>
    private void LoadContent(IapContent content)
    {
        IsLoading = true;
        try
        {
            CommandersIntent = content.CommandersIntent ?? "";
            SituationSummary = content.SituationSummary ?? "";

            Objectives.Clear();
            foreach (var strategic in content.Objectives)
                Objectives.Add(new StrategicEntry(this, Objectives, strategic));

            foreach (var position in Positions)
                position.Name = content.NameFor(position.Role) ?? "";
            Groups.Clear();
            foreach (var group in content.Groups)
                Groups.Add(new GroupEntry(this, Groups, group));

            Assignments.Clear();
            foreach (var assignment in content.Assignments)
                Assignments.Add(new AssignmentEntry(this, Assignments, assignment));

            Channels.Clear();
            foreach (var channel in content.Communications.Channels)
                Channels.Add(new ChannelEntry(this, Channels, channel));
            CommsNotes = content.Communications.Notes ?? "";

            Hospitals.Clear();
            foreach (var hospital in content.Medical.Hospitals)
                Hospitals.Add(new HospitalEntry(this, Hospitals, hospital));
            CasualtyClearingStation = content.Medical.CasualtyClearingStation ?? "";
            AmbulanceLoadingPoint = content.Medical.AmbulanceLoadingPoint ?? "";
            MedicalLead = content.Medical.MedicalLead ?? "";
            EmergencyProcedures = content.Medical.EmergencyProcedures ?? "";

            Hazards.Clear();
            foreach (var hazard in content.Safety.Hazards)
                Hazards.Add(new HazardEntry(this, Hazards, hazard));
            Ppe.Clear();
            foreach (var ppe in content.Safety.Ppe)
                Ppe.Add(new TextEntry(this, Ppe, ppe));
            SafetyMessage = content.Safety.Message ?? "";
            Accountability = content.Safety.Accountability ?? "";
        }
        finally
        {
            IsLoading = false;
        }
        ObjectivesChanged();
        GroupsChanged();
        if (_incidentId is { } id && _cop.FindIncident(id) is { } incident)
            UpdateLive(incident);
    }

    /// <summary>The document as currently edited.</summary>
    public IapContent ToContent() => new()
    {
        CommandersIntent = CommandersIntent,
        SituationSummary = SituationSummary,
        Objectives = Objectives.Select(o => o.ToModel()).ToList(),
        Organization = Positions.Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new IapPosition { Role = p.Role, Name = p.Name.Trim() }).ToList(),
        Groups = Groups.Select(g => g.ToModel()).ToList(),
        Assignments = Assignments.Select(a => a.ToModel()).ToList(),
        Communications = new CommunicationsPlan { Channels = Channels.Select(c => c.ToModel()).ToList(), Notes = CommsNotes },
        Medical = new MedicalPlan
        {
            CasualtyClearingStation = CasualtyClearingStation,
            AmbulanceLoadingPoint = AmbulanceLoadingPoint,
            MedicalLead = MedicalLead,
            EmergencyProcedures = EmergencyProcedures,
            Hospitals = Hospitals.Select(h => h.ToModel()).ToList(),
        },
        Safety = new SafetyPlan
        {
            Hazards = Hazards.Select(h => h.ToModel()).ToList(),
            Ppe = Ppe.Select(p => p.Text).ToList(),
            Message = SafetyMessage,
            Accountability = Accountability,
        },
    };

    /// <summary>What the COP says now: unit status against each assignment, who holds each position, objective progress.</summary>
    private void UpdateLive(Incident incident)
    {
        var briefed = _plan?.BriefedAt is not null;
        foreach (var assignment in Assignments)
        {
            var unit = _cop.FindUnit(assignment.UnitId);
            assignment.UpdateLive(unit, IapCompliance.ViabilityProblem(assignment.ToModel(), incident, unit, expectCommitted: briefed));
        }
        foreach (var position in Positions)
        {
            var holder = incident.Command.Positions.GetValueOrDefault(position.Role);
            position.CopHolder = holder is not null && holder != position.Name.Trim() ? $"On the COP: {holder}" : null;
        }

        IsLoading = true;
        try
        {
            var progress = _cop.ObjectiveProgress;
            foreach (var objective in Objectives.SelectMany(o => o.Operational))
                objective.Progress = progress.GetValueOrDefault(objective.Id);
        }
        finally
        {
            IsLoading = false;
        }

        var planned = Assignments.Select(a => a.UnitId).ToHashSet();
        var addable = _cop.Units
            .Where(u => !planned.Contains(u.Id) && (u.AssignedIncidentId == incident.Id || u.Status == UnitStatus.Available))
            .OrderBy(u => u.AssignedIncidentId == incident.Id ? 0 : 1).ThenBy(u => u.Callsign, StringComparer.Ordinal)
            .Select(u => new UnitOption(u.Id, $"{u.Callsign} · {(u.AssignedIncidentId == incident.Id ? "at incident" : "available")}"))
            .ToList();
        if (!addable.Select(a => a.Label).SequenceEqual(AddableUnits.Select(a => a.Label)))
        {
            AddableUnits.Clear();
            foreach (var option in addable)
                AddableUnits.Add(option);
        }
    }

    // ---- Edits ----

    public void MarkDirty()
    {
        if (IsLoading) return;
        if (IsEditable) IsDirty = true;
        _checkTimer.Stop();
        _checkTimer.Start();
    }

    /// <summary>Renumbers objectives and refreshes the lists that depend on them.</summary>
    public void ObjectivesChanged()
    {
        if (IsLoading) return;
        var options = new List<(Guid Id, string Label)>();
        var tactics = new List<string>();
        for (var s = 0; s < Objectives.Count; s++)
        {
            Objectives[s].Number = $"{s + 1}.";
            for (var o = 0; o < Objectives[s].Operational.Count; o++)
            {
                var operational = Objectives[s].Operational[o];
                operational.Number = $"{s + 1}.{o + 1}";
                options.Add((operational.Id, $"{s + 1}.{o + 1} {Short(operational.Statement)}"));
                tactics.AddRange(operational.Tactics.Select(t => t.Description));
            }
        }

        // Update in place: rebuilding the list would clear each assignment's selection.
        foreach (var stale in ObjectiveOptions.Where(o => o.Id is { } id && options.All(n => n.Id != id)).ToList())
            ObjectiveOptions.Remove(stale);
        foreach (var (id, label) in options)
        {
            if (ObjectiveOptions.FirstOrDefault(o => o.Id == id) is { } existing)
                existing.Label = label;
            else
                ObjectiveOptions.Add(new ObjectiveOption(id, label));
        }

        var suggestions = tactics.Where(t => !string.IsNullOrWhiteSpace(t)).Concat(IapTemplates.Tactics)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!suggestions.SequenceEqual(TacticSuggestions))
        {
            TacticSuggestions.Clear();
            foreach (var suggestion in suggestions)
                TacticSuggestions.Add(suggestion);
        }
        MarkDirty();
    }

    public void GroupsChanged()
    {
        if (IsLoading) return;
        var names = Groups.Select(g => g.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Prepend("").ToList();
        foreach (var stale in GroupNames.Where(n => !names.Contains(n)).ToList())
            GroupNames.Remove(stale);
        foreach (var name in names.Where(n => !GroupNames.Contains(n)))
            GroupNames.Add(name);
        MarkDirty();
    }

    public ObjectiveStatus ProgressOf(Guid objectiveId) => _cop.ObjectiveProgress.GetValueOrDefault(objectiveId);

    /// <summary>"16:00" on the selected period's day (or the next day if that would be before the period).</summary>
    public DateTimeOffset? ParseTime(string text)
    {
        if (!TryParseClock(text, out var time)) return null;
        var period = SelectedPeriod is { } row ? _cop.OperationalPeriods.FirstOrDefault(p => p.Id == row.Id) : null;
        var start = (period?.Start ?? _clock.SimTime).ToLocalTime();
        var at = new DateTimeOffset(start.Date + time, start.Offset);
        if (at < start.AddHours(-1)) at = at.AddDays(1);
        return at.ToUniversalTime();
    }

    private static bool TryParseClock(string text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm", "hhmm"], CultureInfo.InvariantCulture, out time)
        && time < TimeSpan.FromDays(1);

    private static string Short(string text) => text.Length <= 48 ? text : text[..45] + "…";

    /// <summary>Compliance check, org chart, document preview and reassessment for the current edits.</summary>
    private void RunCheck()
    {
        Issues.Clear();
        foreach (var section in Sections)
            section.Errors = section.Warnings = 0;
        var incident = _incidentId is { } id ? _cop.FindIncident(id) : null;
        if (_plan is null || incident is null)
        {
            ErrorCount = WarningCount = 0;
            Chart = OrgChart.Empty;
            DocumentText = "";
            return;
        }

        var content = ToContent();
        var period = _plan.OperationalPeriod;
        var issues = IapCompliance.Check(content, period, incident, _cop.FindUnit);
        foreach (var issue in issues.OrderBy(i => i.Severity).ThenBy(i => i.Section))
        {
            var index = SectionIndex(issue.Section);
            Issues.Add(new IssueRow(issue.Severity == IssueSeverity.Error, Sections[index].Title, issue.Message, index));
            if (issue.Severity == IssueSeverity.Error) Sections[index].Errors++;
            else Sections[index].Warnings++;
        }
        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
        WarningCount = issues.Count - ErrorCount;
        Sections[^1].Errors = ErrorCount;

        Chart = OrgChartLayout.Build(content, _cop.FindUnit);

        var preview = new IncidentActionPlan
        {
            Id = _plan.Id, Version = _plan.Version, Status = _plan.Status, OperationalPeriod = period, Content = content,
            PreparedBy = _plan.PreparedBy, SubmittedBy = _plan.SubmittedBy, SubmittedAt = _plan.SubmittedAt,
            ApprovedBy = _plan.ApprovedBy, ApprovedAt = _plan.ApprovedAt, BriefedAt = _plan.BriefedAt,
            ReturnedBy = _plan.ReturnedBy, ReturnComments = _plan.ReturnComments,
        };
        DocumentText = IapDocument.Render(preview, incident, _cop.ObjectiveProgress);

        Findings.Clear();
        if (period is not null && _cop.ApprovedPlan(period.Id) is { } inForce)
        {
            FindingsTitle = $"REASSESSMENT · plan in force: version {inForce.Version}";
            foreach (var finding in IapReassessment.Findings(inForce, incident, _cop.ObjectiveProgress, _cop.Zones, _clock.SimTime, _cop.FindUnit))
                Findings.Add(finding);
        }
        else
        {
            FindingsTitle = "";
        }
    }

    // ---- Commands ----

    private string Preparer => Positions.First(p => p.Role == IcsRole.Planning).Name is { Length: > 0 } planning
        ? planning
        : "Planning Section";

    private string Approver =>
        Positions.First(p => p.Role == IcsRole.IncidentCommander).Name is { Length: > 0 } ic ? ic
        : (_incidentId is { } id ? _cop.FindIncident(id)?.IncidentCommanderName : null) ?? "Incident Commander";

    private async Task<CommandResult?> RunAsync(Func<Task<CommandResult>> command, string success, bool quietIfUnchanged = false)
    {
        var result = await _main.RunCommandAsync(command, success, quietIfUnchanged);
        if (result?.Detail is { } detail)
            _main.ShowCommandResult(result, detail);
        if (!(quietIfUnchanged && result is { Succeeded: false, Error: "Nothing changed." }))
        {
            Message = _main.CommandMessage;
            MessageFailed = _main.CommandFailed;
        }
        return result;
    }

    /// <summary>Re-reads the COP straight away, e.g. to land on a version just created.</summary>
    private void Reload()
    {
        _signature = "";
        Load(_incidentId is { } id ? _cop.FindIncident(id) : null);
    }

    [RelayCommand]
    private async Task StartPeriodAsync()
    {
        if (_incidentId is not { } incidentId) return;
        var now = _clock.SimTime;
        if (!TryParseClock(NewPeriodStart, out var time))
        {
            _main.ShowCommandResult(CommandResult.Fail("Start time must be HH:mm."));
            Message = _main.CommandMessage;
            MessageFailed = true;
            return;
        }
        var local = now.ToLocalTime();
        var start = new DateTimeOffset(local.Date + time, local.Offset);
        if (start < local.AddHours(-12)) start = start.AddDays(1);
        if (start > local.AddHours(12)) start = start.AddDays(-1);
        start = start.ToUniversalTime();
        if (Math.Abs((start - now).TotalMinutes) < 1) start = now;

        var period = await RunAsync(() => _iap.StartOperationalPeriodAsync(incidentId, start, start + NewPeriodLength.Length, NewPeriodFocus),
            "Operational period started");
        if (period?.EntityId is not { } periodId) return;
        NewPeriodFocus = "";

        // A new period opens its first draft: carried forward from the last plan, refreshed from the COP.
        var draft = await RunAsync(() => _iap.CreateDraftAsync(periodId, Preparer), "Operational period started; draft IAP ready");
        _wantPeriodId = periodId;
        _wantPlanId = draft?.EntityId;
        Reload();
        SelectedSection = 1;
    }

    [RelayCommand]
    private async Task NewVersionAsync()
    {
        if (SelectedPeriod is not { } period) return;
        var result = await RunAsync(() => _iap.CreateDraftAsync(period.Id, Preparer), "New draft version created");
        _wantPlanId = result?.EntityId;
        Reload();
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveIfDirtyAsync();

    private async Task<bool> SaveIfDirtyAsync()
    {
        if (_plan is not { } plan || !IsDirty) return true;
        var result = await RunAsync(() => _iap.SaveDraftAsync(plan.Id, ToContent()), "Draft saved", quietIfUnchanged: true);
        if (result is null) return false;
        if (result.Succeeded || result.Error == "Nothing changed.")
        {
            IsDirty = false;
            Reload();
            return true;
        }
        return false;
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        _loadedPlanId = null;
        IsDirty = false;
        SyncPlan();
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (_plan is not { } plan || !await SaveIfDirtyAsync()) return;
        await RunAsync(() => _iap.SubmitAsync(plan.Id, Preparer), $"Version {plan.Version} submitted to {Approver}");
        Reload();
    }

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (_plan is not { } plan) return;
        var approver = Approver;
        await RunAsync(() => _iap.ApproveAsync(plan.Id, approver), $"Approved by {approver}: version {plan.Version} is in force");
        Reload();
    }

    [RelayCommand]
    private async Task ReturnPlanAsync()
    {
        if (_plan is not { } plan) return;
        var result = await RunAsync(() => _iap.ReturnAsync(plan.Id, Approver, ReturnComments), "Returned to the planner");
        if (result?.Succeeded == true) ReturnComments = "";
        Reload();
    }

    [RelayCommand]
    private async Task BriefAsync()
    {
        if (_plan is not { } plan) return;
        await RunAsync(() => _iap.BriefAsync(plan.Id), "Plan briefed");
        Reload();
    }

    /// <summary>Fills a section from the current COP. The result is an unsaved edit.</summary>
    [RelayCommand]
    private async Task PullAsync(string part)
    {
        if (_incidentId is not { } incidentId || !IsEditable || !Enum.TryParse<IapPullParts>(part, out var parts)) return;
        var content = await _iap.PullFromCopAsync(incidentId, ToContent(), parts);
        LoadContent(content);
        IsDirty = true;
        RunCheck();
        Message = "Pulled from the COP: review, then save.";
        MessageFailed = false;
    }

    public async Task SetProgressAsync(Guid objectiveId, ObjectiveStatus status)
    {
        if (_incidentId is not { } incidentId) return;
        await RunAsync(() => _iap.SetObjectiveStatusAsync(incidentId, objectiveId, status),
            $"Objective marked {EventDescriber.Humanize(status).ToLowerInvariant()}", quietIfUnchanged: true);
    }

    [RelayCommand]
    private void ShowIssue(IssueRow? issue)
    {
        if (issue is not null) SelectedSection = issue.SectionIndex;
    }

    [RelayCommand]
    private void AddStrategic()
    {
        Objectives.Add(new StrategicEntry(this, Objectives, new StrategicObjective { Statement = NewStrategic.Trim() }));
        NewStrategic = "";
    }

    [RelayCommand]
    private void AddGroup() => Groups.Add(new GroupEntry(this, Groups, new IapGroup { Name = $"Group {Groups.Count + 1}" }));

    [RelayCommand]
    private void AddAssignment()
    {
        if (UnitToAdd is not { } option || _cop.FindUnit(option.Id) is not { } unit) return;
        Assignments.Add(new AssignmentEntry(this, Assignments, new IapAssignment { UnitId = unit.Id, Callsign = unit.Callsign }));
        if (_incidentId is { } id && _cop.FindIncident(id) is { } incident)
            UpdateLive(incident);
        UnitToAdd = null;
    }

    [RelayCommand]
    private void AddChannel() => Channels.Add(new ChannelEntry(this, Channels, new RadioChannel { Function = "Tactical" }));

    [RelayCommand]
    private void AddHospital() => Hospitals.Add(new HospitalEntry(this, Hospitals, new ReceivingHospital()));

    [RelayCommand]
    private void AddHazard() => Hazards.Add(new HazardEntry(this, Hazards, new SafetyHazard()));

    [RelayCommand]
    private void AddPpe()
    {
        Ppe.Add(new TextEntry(this, Ppe, NewPpe.Trim()));
        NewPpe = "";
    }
}
