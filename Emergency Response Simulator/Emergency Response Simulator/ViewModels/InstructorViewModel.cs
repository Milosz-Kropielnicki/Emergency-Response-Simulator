using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One line of the truth-against-COP comparison.</summary>
public sealed record DivergenceRow(string Topic, string Truth, string Cop, DivergenceSeverity Severity);

/// <summary>One link in a chain of knock-on effects.</summary>
public sealed record CascadeRow(string Time, string Cause, string Effect);

/// <summary>A labelled figure in the world summary, e.g. "Casualties" → "14 (3 P1, 5 P2, 6 P3), 1 dead".</summary>
public sealed record WorldFact(string Label, string Value);

/// <summary>
/// The instructor's window on ground truth (Design Document §10.1, §21): where the COP and the world disagree, the
/// chain of knock-on effects, and what the simulation is really doing. Reads the engine's snapshot once a second,
/// so it never touches the engine's state directly. Not part of the trainee's picture.
/// </summary>
public partial class InstructorViewModel : ObservableObject
{
    private readonly SimulationEngine _engine;
    private readonly ICopService _cop;
    private readonly LiveTraffic _traffic;

    public InstructorViewModel(SimulationEngine engine, ICopService liveCop, LiveTraffic traffic)
    {
        _engine = engine;
        _cop = liveCop;
        _traffic = traffic;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(),
            Application.Current.Dispatcher);
        timer.Start();
    }

    /// <summary>Raised after each refresh, so the map can redraw its ground-truth layers.</summary>
    public event EventHandler? Refreshed;

    public WorldSnapshot Snapshot { get; private set; } = WorldSnapshot.Empty;

    public ObservableCollection<DivergenceRow> Divergences { get; } = [];
    public ObservableCollection<CascadeRow> Cascades { get; } = [];
    public ObservableCollection<WorldFact> Facts { get; } = [];

    /// <summary>"3 things command doesn't know about" for the tab header.</summary>
    [ObservableProperty] private string _summary = "";

    private void Refresh()
    {
        var snapshot = _engine.Snapshot;
        if (ReferenceEquals(snapshot, Snapshot)) return;
        Snapshot = snapshot;

        var gaps = TruthComparison.Compare(snapshot, _cop);
        Replace(Divergences, gaps.Where(g => g.Severity != DivergenceSeverity.Aligned)
            .Select(g => new DivergenceRow(g.Topic, g.Truth, g.Cop, g.Severity)));
        var unknown = gaps.Count(g => g.Severity == DivergenceSeverity.Unknown);
        Summary = unknown == 0 ? "" : $" · {unknown} unknown to command";

        if (Cascades.Count != snapshot.Cascades.Count)
            Replace(Cascades, snapshot.Cascades.AsEnumerable().Reverse()
                .Select(c => new CascadeRow(MainViewModel.Time(c.At, seconds: true), c.Cause, c.Effect)));

        Replace(Facts, BuildFacts(snapshot));
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<WorldFact> BuildFacts(WorldSnapshot snapshot)
    {
        yield return new WorldFact("Wind (true)", $"From {snapshot.Weather.WindFromDegrees:F0}° at {snapshot.Weather.WindSpeedMps:F1} m/s");

        foreach (var hazard in snapshot.Hazards)
        {
            var size = hazard.Kind switch
            {
                HazardKind.Fire => $"{hazard.Intensity:N0} m² burning, {hazard.AreaSquareMeters:N0} m² affected",
                HazardKind.Flood => $"up to {hazard.Intensity * 100:F0} cm, {hazard.AreaSquareMeters / 10_000:0.#} ha",
                _ => $"{hazard.Rate:0.0#} kg/s, {hazard.AreaSquareMeters / 10_000:0.#} ha above the lowest threshold",
            };
            yield return new WorldFact(EventDescriber.Humanize(hazard.Kind), $"{hazard.Description}: {size}");
        }

        var people = snapshot.Civilians;
        if (people.Count > 0)
        {
            yield return new WorldFact("People in the area", $"{people.Count}: " + string.Join(", ",
                people.GroupBy(p => p.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {EventDescriber.Humanize(g.Key).ToLowerInvariant()}")));
        }

        var casualties = snapshot.Casualties;
        if (casualties.Count > 0)
        {
            var alive = casualties.Where(c => c.Triage != Triage.Deceased).ToList();
            yield return new WorldFact("Casualties", $"{alive.Count} hurt (" + string.Join(", ",
                alive.GroupBy(c => c.Triage).OrderBy(g => g.Key).Select(g => $"{g.Count()} {EventDescriber.TriageLabel(g.Key)}")) +
                $"), {casualties.Count - alive.Count} dead");
            yield return new WorldFact("Casualty care", string.Join(", ",
                casualties.GroupBy(c => c.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {EventDescriber.Humanize(g.Key).ToLowerInvariant()}")));
        }

        foreach (var hospital in snapshot.Hospitals)
        {
            yield return new WorldFact(hospital.Name, $"ED {hospital.Occupied}/{hospital.Capacity}" +
                (hospital.OnDiversion ? ", on diversion" : "") + (hospital.OnGenerator ? ", on generators" : ""));
        }

        foreach (var outage in snapshot.Outages)
            yield return new WorldFact("Power outage", $"{outage.Cause}; restoring {MainViewModel.Time(outage.RestoreAt)}");

        if (snapshot.Comms.CallsWaiting > 0)
            yield return new WorldFact("999 line", $"{snapshot.Comms.CallsWaiting} caller(s) waiting for a call-taker");
        if (snapshot.Comms.RecentLost.Count > 0)
            yield return new WorldFact("Last unheard transmission", snapshot.Comms.RecentLost[^1] is var last
                ? $"{MainViewModel.Time(last.At, seconds: true)} {last.From}: \"{last.Text}\" ({last.Reason})" : "");

        var traffic = _traffic.Snapshot;
        var jammed = traffic.Factors.Count(f => f.Value < LiveTraffic.GridlockFactor);
        var slow = traffic.Factors.Count(f => f.Value < 0.6);
        if (slow > 0 || traffic.DarkSignals.Count > 0)
            yield return new WorldFact("Roads (true)", $"{jammed} road segments gridlocked, {slow} heavily congested, " +
                                                       $"{traffic.DarkSignals.Count} approaches to dark junctions");
        if (snapshot.Obstructions.Count > 0)
            yield return new WorldFact("Roads blocked", string.Join("; ", snapshot.Obstructions.Select(o => o.Description).Distinct().Take(4)) +
                                                        (snapshot.Obstructions.Count > 4 ? $" (+{snapshot.Obstructions.Count - 4})" : ""));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
