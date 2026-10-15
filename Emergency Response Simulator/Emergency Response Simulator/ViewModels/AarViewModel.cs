using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Aar;
using Emergency_Response_Simulator.Simulation.Engine;
using Microsoft.Win32;

namespace Emergency_Response_Simulator.ViewModels;

public sealed record AarMetricRow(string Category, string Name, string Display, string Standard, Rating Rating, string RatingText, string Explanation);

public sealed record AarDecisionRow(DateTimeOffset At, string Time, string Category, string Decision, string Trigger, string Took,
    string Known, string Truth, Rating Rating, string RatingText, string Note, string Actor);

public sealed record AarBranchRow(string Topic, string Time, string Yours, string Recommended, string Alternative, string Effect, Rating Rating, string RatingText);

public sealed record AarOutcomeRow(string Label, string Hurt, string Dead, string Firefighters, string Fire, string FirstOnScene, string Notes, string Actions);

/// <param name="ClipId">A recorded transmission to play (Phase 11 M7).</param>
public sealed record AarTimelineRow(DateTimeOffset At, string Time, string Category, string Text, bool IsDecision, bool IsTruth, string Actor, Guid? ClipId = null)
{
    public bool HasClip => ClipId is not null;
}

/// <summary>One participant's line in the review (networked sessions).</summary>
public sealed record AarParticipantRow(string Name, string Roles, string Time, int Commands, string Ratings, string Response, string Score, string Focus,
    string Observers = "", string Overall = "");

/// <summary>An observer's note in the review (Phase 11 M6).</summary>
public sealed record AarNoteRow(string Time, string Participant, string Rating, string Note, string About, string By, string Area = "");

/// <summary>Something that would be hard to defend at an inquiry (Phase 14).</summary>
public sealed record AarLiabilityRow(string Time, string Kind, string Text, string Mitigated);

/// <summary>A supply that ran out, or an appliance that broke down (Phase 15).</summary>
public sealed record AarLogisticsRow(string Time, string What, string Where, string Lasted, string Warning);

/// <summary>One participant's observer ratings in one competency area (Phase 12).</summary>
public sealed record AarCompetencyRow(string Participant, string Area, int Ratings, string Score, string Level);

/// <summary>Something the instructor did to run the exercise (Phase 11 M4).</summary>
public sealed record AarInstructorRow(string Time, string Kind, string Text);

/// <summary>A choice in the participant filter: everyone (null) or one person.</summary>
public sealed record ParticipantChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A coloured stretch of a unit's commitment strip, in pixels.</summary>
public sealed record GanttBar(double Left, double Width, Brush Fill, string Tip);

public sealed record GanttRow(string Callsign, string Committed, IReadOnlyList<GanttBar> Bars);

public sealed record HeatCell(double Opacity, string Tip);

public sealed record HeatmapView(string Title, string Subtitle, int Columns, IReadOnlyList<HeatCell> Cells, IReadOnlyList<string> Hotspots);

public sealed record AarDelayRow(string Time, string Callsign, string Lost, string Where, string Cause);

/// <summary>
/// The after-action review window (Design Document §22): summary and findings, the decision timeline (with "show the
/// COP then"), metrics, commitment and delay analytics with heatmaps, branch points and the re-run comparison, the full
/// replayable timeline, and exports for debriefing and certification.
/// </summary>
public sealed partial class AarViewModel(IAarService aar, SimulationSession session, TimelineViewModel timeline, RadioViewModel radio) : ObservableObject
{
    /// <summary>Pixels the commitment chart spreads the session across.</summary>
    public const double GanttWidth = 720;

    private CancellationTokenSource? _branches;

    public AarReport? Report { get; private set; }

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Build the review to analyse the session so far.";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _overall = "";
    [ObservableProperty] private string _score = "";
    [ObservableProperty] private Competence _competence;
    [ObservableProperty] private bool _decisionsOnly = true;
    [ObservableProperty] private bool _includeTruth;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _branchStatus = "";
    [ObservableProperty] private int _branchRuns = 2;

    // Sign-off for the exported report.
    [ObservableProperty] private string _trainee = "";
    [ObservableProperty] private string _role = "Incident commander";
    [ObservableProperty] private string _assessor = "";
    [ObservableProperty] private string _comments = "";
    [ObservableProperty] private string _verdict = "Not given";

    public bool CanCompare => aar.CanCompareBranches;

    public IReadOnlyList<string> Verdicts { get; } = ["Not given", "Competent", "Developing", "Not yet competent"];
    public IReadOnlyList<int> RunChoices { get; } = [1, 2, 3, 5];

    public ObservableCollection<AarFinding> Findings { get; } = [];
    public ObservableCollection<AarMetricRow> Metrics { get; } = [];
    public ObservableCollection<AarDecisionRow> Decisions { get; } = [];
    public ObservableCollection<AarBranchRow> Branches { get; } = [];
    public ObservableCollection<AarOutcomeRow> Outcomes { get; } = [];
    public ObservableCollection<GanttRow> Gantt { get; } = [];
    public ObservableCollection<string> GanttScale { get; } = [];
    public ObservableCollection<HeatmapView> Heatmaps { get; } = [];
    public ObservableCollection<AarDelayRow> Delays { get; } = [];
    public ObservableCollection<AarTimelineRow> Timeline { get; } = [];

    /// <summary>Plays a recorded transmission through the headset (Phase 11 M7).</summary>
    [RelayCommand]
    private Task PlayClipAsync(Guid clipId) => radio.PlayClipAsync(clipId);
    public ObservableCollection<AarParticipantRow> Participants { get; } = [];

    /// <summary>How the instructor ran the exercise (Phase 11 M4); scripted injects that went off on time are counted, not listed.</summary>
    public ObservableCollection<AarInstructorRow> Instructor { get; } = [];

    [ObservableProperty] private bool _hasInstructor;

    /// <summary>Observers' notes (Phase 11 M6).</summary>
    public ObservableCollection<AarNoteRow> Notes { get; } = [];

    [ObservableProperty] private bool _hasNotes;

    public ObservableCollection<AarCompetencyRow> Competencies { get; } = [];

    public ObservableCollection<AarLiabilityRow> Liabilities { get; } = [];

    [ObservableProperty] private bool _hasLegal;
    [ObservableProperty] private string _documentationSummary = "";

    public ObservableCollection<AarLogisticsRow> LogisticsEvents { get; } = [];
    public ObservableCollection<CostRow> LogisticsCosts { get; } = [];
    [ObservableProperty] private bool _hasLogistics;
    [ObservableProperty] private string _logisticsSummary = "";

    public ObservableCollection<CostRow> HealthFigures { get; } = [];
    [ObservableProperty] private bool _hasHealth;

    [ObservableProperty] private bool _hasCompetencies;
    [ObservableProperty] private string _instructorNote = "";
    public ObservableCollection<ParticipantChoice> ParticipantChoices { get; } = [];

    /// <summary>Shows only this participant's decisions and commands (networked sessions, Phase 11).</summary>
    [ObservableProperty] private ParticipantChoice? _participantFilter;
    [ObservableProperty] private bool _hasParticipants;

    partial void OnDecisionsOnlyChanged(bool value) => FillTimeline();
    partial void OnIncludeTruthChanged(bool value) => FillTimeline();

    partial void OnParticipantFilterChanged(ParticipantChoice? value)
    {
        FillDecisions();
        FillTimeline();
    }

    private static string Clock(DateTimeOffset at) => MainViewModel.Time(at, seconds: true);

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Analysing the session…";
        try
        {
            var previous = Report?.Outcomes.Skip(1).ToList() ?? [];
            Report = await aar.BuildReportAsync(session.Id);
            Report.Outcomes.AddRange(previous); // keep a comparison already run
            Fill(Report);
            Status = $"Reviewed {Report.Timeline.Count} events and {Report.Decisions.Count} decisions, as of {Clock(Report.To)}.";
        }
        catch (Exception ex)
        {
            Status = $"The review could not be built: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (IsComparing)
        {
            _branches?.Cancel();
            return;
        }
        if (Report is null) await BuildAsync();
        if (Report is null) return;

        IsComparing = true;
        _branches = new CancellationTokenSource();
        var progress = new Progress<string>(message => BranchStatus = message);
        try
        {
            var outcomes = await aar.CompareBranchesAsync(session.Id, BranchRuns, progress, _branches.Token);
            Report.Outcomes.RemoveRange(1, Report.Outcomes.Count - 1);
            Report.Outcomes.AddRange(outcomes);
            FillOutcomes(Report);
            BranchStatus = $"Re-ran the scenario {BranchRuns} time(s) under each policy for as long as your session ran.";
        }
        catch (OperationCanceledException)
        {
            BranchStatus = "Comparison cancelled.";
        }
        catch (Exception ex)
        {
            BranchStatus = $"The comparison failed: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
        }
    }

    /// <summary>Shows the COP as it was when the decision was made (the main window goes into replay).</summary>
    [RelayCommand]
    private Task ShowCopAsync(DateTimeOffset at) => timeline.ReplayAtAsync(at);

    [RelayCommand]
    private void ExportHtml()
    {
        if (Report is null) return;
        var path = Ask("HTML report|*.html", "html");
        if (path is null) return;
        File.WriteAllText(path, AarExport.Html(Report, SignOff()));
        Status = $"Report saved to {path}";
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Report is null) return;
        var path = Ask("CSV (spreadsheet)|*.csv", "csv");
        if (path is null) return;
        File.WriteAllText(path, AarExport.Csv(Report), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Status = $"Data saved to {path}";
    }

    private string? Ask(string filter, string extension)
    {
        var name = string.Concat($"AAR {Report!.Title} {Report.From.ToLocalTime():yyyy-MM-dd HHmm}".Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog { Filter = filter, FileName = $"{name}.{extension}", AddExtension = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private AarSignOff SignOff() => new(
        string.IsNullOrWhiteSpace(Trainee) ? "(trainee)" : Trainee.Trim(), Role.Trim(),
        string.IsNullOrWhiteSpace(Assessor) ? "(assessor)" : Assessor.Trim(), string.IsNullOrWhiteSpace(Comments) ? null : Comments.Trim(),
        Verdict switch { "Competent" => Core.Model.Competence.Competent, "Developing" => Core.Model.Competence.Developing,
            "Not yet competent" => Core.Model.Competence.NotYetCompetent, _ => null });

    // ---- Filling the views ----

    private void Fill(AarReport report)
    {
        HasReport = true;
        Title = report.Title;
        Subtitle = $"{report.From.ToLocalTime():dddd d MMMM, HH:mm}–{report.To.ToLocalTime():HH:mm} · {AarAnalyzer.Duration(report.Duration)} of simulated time";
        Competence = report.Overall;
        Overall = AarExport.Label(report.Overall);
        Score = $"{report.Score} / 100";

        Replace(Findings, report.Findings);
        Replace(Metrics, report.Metrics.Select(m => new AarMetricRow(m.Category, m.Name, m.Display, m.Standard, m.Rating, AarExport.Label(m.Rating), m.Explanation)));
        FillParticipants(report);
        FillInstructor(report);
        FillDecisions();
        Replace(Branches, report.Branches.Select(b => new AarBranchRow(b.Topic, b.At is { } at ? Clock(at) : "", b.Yours, b.Recommended, b.Alternative,
            b.Effect, b.Rating, AarExport.Label(b.Rating))));
        FillOutcomes(report);
        FillGantt(report);
        Replace(Heatmaps, report.Heatmaps.Select(Heatmap));
        Replace(Delays, report.Delays.Take(40).Select(d => new AarDelayRow(Clock(d.At), d.Callsign, AarAnalyzer.Duration(d.Lost), d.Where, d.Cause)));
        FillTimeline();
    }

    private void FillOutcomes(AarReport report) =>
        Replace(Outcomes, report.Outcomes.Select(o => new AarOutcomeRow(o.Label, $"{o.Injured:0.#}", $"{o.Deaths:0.#}", $"{o.FirefightersInDistress:0.#}",
            $"{o.PeakFireSquareMeters:N0} m²", o.FirstOnSceneMinutes is { } m ? $"{m:0.0} min" : "—", o.Notes,
            string.Join(Environment.NewLine, (o.KeyActions ?? []).Take(25)))));

    private static readonly Dictionary<string, Brush> Colours = [];

    private static Brush Colour(UnitStatus status)
    {
        var hex = AarExport.StatusColour(status);
        if (Colours.TryGetValue(hex, out var brush)) return brush;
        brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return Colours[hex] = brush;
    }

    private void FillGantt(AarReport report)
    {
        var total = Math.Max(1, report.Duration.TotalSeconds);
        Replace(Gantt, report.Units.Select(u => new GanttRow(u.Callsign, $"{u.Committed:P0}", u.Spans.Select(s => new GanttBar(
            (s.From - report.From).TotalSeconds / total * GanttWidth, Math.Max(1, (s.To - s.From).TotalSeconds / total * GanttWidth),
            Colour(s.Status), $"{Core.Events.EventDescriber.Humanize(s.Status)} {Clock(s.From)}–{Clock(s.To)}")).ToList())));
        var step = report.Duration.TotalMinutes > 60 ? 15 : report.Duration.TotalMinutes > 20 ? 10 : 5;
        Replace(GanttScale, Enumerable.Range(0, (int)(report.Duration.TotalMinutes / step) + 1).Select(i => $"+{i * step} min"));
    }

    private static HeatmapView Heatmap(AarHeatmap map)
    {
        var max = Math.Max(map.Max, 0.001);
        var cells = new List<HeatCell>();
        for (var row = map.Rows - 1; row >= 0; row--) // north at the top
        {
            for (var column = 0; column < map.Columns; column++)
            {
                var value = map.Values[row * map.Columns + column];
                cells.Add(new HeatCell(value <= 0 ? 0 : 0.15 + 0.85 * Math.Sqrt(value / max), value <= 0 ? "" : $"{value:0.#} {map.Unit}"));
            }
        }
        return new HeatmapView(map.Title, $"{map.Columns * map.CellMeters / 1000:0.#} km square around the incident (ring); darkest {map.Max:0.#} {map.Unit}",
            map.Columns, cells, map.Hotspots);
    }

    private void FillTimeline()
    {
        if (Report is null) return;
        // With a participant chosen, only what their commands produced.
        var person = ParticipantFilter?.Id is not null;
        Replace(Timeline, Report.TimelineOf(ParticipantFilter?.Id).Where(t => (!DecisionsOnly || t.IsDecision || t.Important || person) && (IncludeTruth || !t.IsTruth))
            .Select(t => new AarTimelineRow(t.At, Clock(t.At), t.Category, t.Text, t.IsDecision, t.IsTruth, t.Actor ?? "", t.ClipId)));
    }

    private void FillDecisions()
    {
        if (Report is null) return;
        Replace(Decisions, Report.DecisionsOf(ParticipantFilter?.Id).Select(d => new AarDecisionRow(d.At, Clock(d.At), d.Category, d.Decision,
            d.Trigger ?? "—", d.ResponseTime is { } t ? AarAnalyzer.Duration(t) : "—", d.Known, d.Truth, d.Rating, AarExport.Label(d.Rating), d.Note ?? "",
            d.Actor ?? "")));
    }

    private void FillInstructor(AarReport report)
    {
        var steered = report.Instructor.Where(a => a.Kind != "Inject (on time)").ToList();
        HasInstructor = steered.Count > 0;
        Replace(Instructor, steered.Select(a => new AarInstructorRow(Clock(a.At), a.Kind, a.Text)));
        InstructorNote = $"{report.Instructor.Count(a => a.Kind == "Inject (on time)")} scripted inject(s) went off on time. Timeline entries marked as role-play were the instructor speaking or deciding as someone in the world.";
    }

    private void FillParticipants(AarReport report)
    {
        HasParticipants = report.Participants.Count > 0;
        Replace(Participants, report.Participants.Select(p => new AarParticipantRow(p.Name, p.Roles, AarAnalyzer.Duration(p.Present), p.Commands,
            $"{p.Good} good · {p.Fair} fair · {p.Poor} poor", p.MeanResponse is { } mean ? AarAnalyzer.Duration(mean) : "—",
            p.Score is { } score ? $"{score} / 100" : "—", p.Focus, AarExport.ObserverRatings(p),
            p.Combined is { } combined ? $"{combined} / 100 · {Rubric.Name(p.Level!.Value)}" : "—")));
        HasNotes = report.Notes.Count > 0;
        Replace(Notes, report.Notes.Select(n => new AarNoteRow(Clock(n.At), n.Participant, n.Rating is { } rating ? Rubric.Name(rating) : "—", n.Note,
            n.About ?? "", n.By ?? "", n.Area is { } area ? Rubric.Name(area) : "—")));
        HasLegal = report.Liabilities.Count > 0 || report.Documentation.Significant > 0;
        var log = report.Documentation;
        DocumentationSummary = log.Significant == 0 ? "No significant decisions needed logging."
            : $"Decision log: {log.Documented} of {log.Significant} significant decisions documented with their reasons ({log.Percent}%)." +
              (log.Undocumented.Count > 0 ? $" Not logged: {string.Join("; ", log.Undocumented)}." : "");
        Replace(Liabilities, report.Liabilities.Select(l => new AarLiabilityRow(Clock(l.At), l.Kind, l.Text, l.Mitigated ? "mitigated" : "")));
        var logistics = report.Logistics;
        HasLogistics = logistics.Any;
        LogisticsSummary = $"{logistics.Orders} supply order(s) · {logistics.Outages.Count} supply outage(s) · {logistics.RanDry} ran out of fuel · " +
                           $"{logistics.Breakdowns.Count} breakdown(s), {logistics.Serviced} serviced in time · total cost {CostRates.Money(logistics.Total)}" +
                           (logistics.Claimed > 0 ? $" · claimed {CostRates.Money(logistics.Claimed)}, {CostRates.Money(logistics.Approved)} approved" : "");
        Replace(LogisticsCosts, logistics.Costs.Select(c => new CostRow(CostRates.Name(c.Category), CostRates.Money(c.Amount))));
        HasHealth = report.Health.Any;
        Replace(HealthFigures, AarExport.HealthLines(report.Health).Select(l => new CostRow(l.Label, l.Value)));
        Replace(LogisticsEvents, logistics.Outages
            .Select(o => (o.From, Row: new AarLogisticsRow(Clock(o.From), $"{Supplies.Name(o.Kind)} ran out", o.Incident,
                o.To is { } to ? AarAnalyzer.Duration(to - o.From) : "to the end",
                (o.WarnedAt is { } w ? $"warned {Clock(w)}" : "no warning") + (o.OrderedAt is { } ord ? $", ordered {Clock(ord)}" : ", not ordered"))))
            .Concat(logistics.Breakdowns.Select(b => (From: b.At, Row: new AarLogisticsRow(Clock(b.At), $"Broke down: {b.Fault}", b.Unit,
                b.BackAt is { } back ? AarAnalyzer.Duration(back - b.At) : "to the end",
                (b.WarnedAt is { } w ? $"crew warned {Clock(w)}" : "no warning") + (b.RepairRequestedAt is { } r ? $", repair asked {Clock(r)}" : ", no repair asked")))))
            .OrderBy(x => x.From).Select(x => x.Row));
        HasCompetencies = report.Competencies.Count > 0;
        Replace(Competencies, report.Competencies.Select(c => new AarCompetencyRow(c.Participant, Rubric.Name(c.Area), c.Ratings, $"{c.Score} / 100",
            Rubric.Name(c.Level))));
        var chosen = ParticipantFilter?.Id;
        Replace(ParticipantChoices, [new ParticipantChoice(null, "Everyone"), .. report.Participants.Select(p => new ParticipantChoice(p.Id, $"{p.Name} ({p.Roles})"))]);
        ParticipantFilter = ParticipantChoices.FirstOrDefault(c => c.Id == chosen) ?? ParticipantChoices[0];
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
