using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// Everything the AVL feed and COP know about one unit (Design Document §7.4):
/// "ENGINE 12 · Location · Status · Speed · Heading · Crew · Assignment · ETA · Last update".
/// </summary>
public sealed partial class UnitDetailViewModel(IRoutingService routing) : ObservableObject
{
    [ObservableProperty] private bool _hasUnit;
    [ObservableProperty] private string _callsign = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private UnitStatus _status;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Label/value rows shown in the panel.</summary>
    public ObservableCollection<DetailRow> Rows { get; } = [];

    public void Load(Unit? unit, DateTimeOffset now)
    {
        HasUnit = unit is not null;
        Rows.Clear();
        if (unit is null) return;

        Callsign = unit.Callsign.ToUpperInvariant();
        Subtitle = $"{ResourceGroups.Label(unit.Type)} · {unit.Agency?.Name ?? "No agency"} · {unit.HomeStation ?? "—"}";
        Status = unit.Status;
        StatusText = EventDescriber.Humanize(unit.Status);

        var location = unit.Location is { } p ? GeoPoint.FromPoint(p) : (GeoPoint?)null;
        var road = location is { } l ? routing.NearestRoad(l) : null;

        Add("Location", location is { } here
            ? (road is { DistanceMeters: < 60 } r ? $"{r.Name}  " : "") + $"({here.Latitude:F5}, {here.Longitude:F5})"
            : "Unknown");
        Add("Speed", unit.SpeedKph < 1 ? "Stationary" : $"{unit.SpeedKph:F0} km/h");
        Add("Heading", unit.SpeedKph < 1 ? "—" : $"{GeoMath.CompassPoint(unit.Heading)} ({unit.Heading:F0}°)");
        Add("Assignment", unit.AssignedIncident is { } incident
            ? $"{incident.Number} {incident.Name ?? EventDescriber.Humanize(incident.Type)}"
            : "None");

        if (unit.AssignedIncident is { } target && location is { } from)
        {
            var straight = GeoMath.DistanceMeters(from, GeoPoint.FromPoint(target.Location));
            Add("Distance to incident", $"{Km(straight)} straight line" +
                (unit.RouteDistanceMeters is { } byRoad && unit.Status == UnitStatus.EnRoute ? $", {Km(byRoad)} planned route" : ""));
        }

        Add("ETA", unit.Eta is { } eta && unit.Status == UnitStatus.EnRoute ? $"{(int)eta.TotalMinutes}:{eta.Seconds:D2}" : "—");
        if (unit.Crew is { } crew)
        {
            Add("Crew", $"{crew.OnDuty} on duty: " + string.Join(", ", crew.Members.Where(m => m.Available).Select(m => m.Name)) +
                        (crew.OnDuty < crew.Members.Count ? $" ({crew.Members.Count - crew.OnDuty} off: " +
                            string.Join(", ", crew.Members.Where(m => !m.Available).Select(m => $"{m.Name}, {EventDescriber.Humanize(m.Status).ToLowerInvariant()}")) + ")" : ""));
            Add("Qualifications", string.Join(", ", Qualifications.All.Select(q => (q.Code, Count: crew.Holding(q.Code))).Where(q => q.Count > 0)
                .Select(q => $"{q.Code} ×{q.Count}")) is { Length: > 0 } held ? held : "None");
            Add("Shift", crew.PastShiftEnd(now)
                ? $"Ended {MainViewModel.Time(crew.ShiftEnd)}: on overtime ({(now - crew.ShiftEnd).TotalMinutes:F0} min)"
                : $"{MainViewModel.Time(crew.ShiftStart)}–{MainViewModel.Time(crew.ShiftEnd)} ({crew.OnShift(now).TotalHours:F1} h on duty)");
            Add("Crew condition", crew.InRehab ? $"In rehab since {MainViewModel.Time(crew.RehabSince!.Value)}"
                : crew.ConditionReportedAt is { } said ? $"{crew.Condition} at {MainViewModel.Time(said)}: \"{crew.ConditionNote}\"" : "Nothing reported");
        }
        else
        {
            Add("Crew", unit.CrewSize.ToString());
        }
        Add("Equipment", unit.Capabilities.Count == 0 ? "—" : string.Join(", ", unit.Capabilities));
        Add("Last AVL update", unit.LastAvlUpdate is { } avl ? $"{MainViewModel.Time(avl, seconds: true)} ({Age(now - avl)} ago)" : "—");
        Add("Comms", unit.CommsConnected
            ? "Connected"
            : $"NO CONTACT since {(unit.LastContactAt is { } c ? MainViewModel.Time(c, seconds: true) : "—")}");
    }

    private void Add(string label, string value) => Rows.Add(new DetailRow(label, value));

    private static string Km(double meters) => meters < 1000 ? $"{meters:F0} m" : $"{meters / 1000:F1} km";

    private static string Age(TimeSpan t) => t.TotalSeconds < 90 ? $"{Math.Max(0, t.TotalSeconds):F0} s" : $"{t.TotalMinutes:F0} min";
}

public sealed record DetailRow(string Label, string Value);

/// <summary>An available unit ranked by road ETA to the selected incident.</summary>
public sealed record UnitCandidate(Guid UnitId, string Callsign, string Type, string Eta, string Distance, double Seconds);
