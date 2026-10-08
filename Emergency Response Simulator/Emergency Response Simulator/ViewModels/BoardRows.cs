using CommunityToolkit.Mvvm.ComponentModel;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>Incident status board row (Design Document §6.5).</summary>
public sealed record IncidentRow(
    Guid Id,
    string Number,
    string Type,
    IncidentPriority Priority,
    string Status,
    string Location,
    string Casualties,
    int Units,
    string Commander,
    string Updated);

/// <summary>A status the unit may move to next, offered in the resource board.</summary>
public sealed record StatusOption(UnitStatus Status, string Label);

/// <summary>Resource board row (Design Document §6.3–6.4).</summary>
public sealed partial class UnitRow(MainViewModel owner, Unit unit, DateTimeOffset now) : ObservableObject
{
    public Guid Id { get; } = unit.Id;
    public string Callsign { get; } = unit.Callsign;
    public string Type { get; } = ResourceGroups.Label(unit.Type);
    public string Agency { get; } = unit.Agency?.ShortName ?? "—";
    public UnitStatus Status { get; } = unit.Status;
    public string StatusText { get; } = EventDescriber.Humanize(unit.Status);
    public string Assignment { get; } = unit.AssignedIncident?.Number ?? "—";
    public string Location { get; } = unit.Status == UnitStatus.Available ? unit.HomeStation ?? "Station" :
        unit.Location is { } p ? $"{p.Y:F4}, {p.X:F4}" : "Unknown";
    public int Crew { get; } = unit.CrewSize;
    public string Equipment { get; } = unit.Capabilities.Count == 0 ? "—" : string.Join(", ", unit.Capabilities);
    public string Eta { get; } = unit.Eta is { } eta && unit.Status == UnitStatus.EnRoute ? $"{(int)eta.TotalMinutes:D2}:{eta.Seconds:D2}" : "—";
    public bool CommsOk { get; } = unit.CommsConnected;
    public string Comms { get; } = unit.CommsConnected
        ? "OK"
        : $"No contact {(now - (unit.LastContactAt ?? now)).TotalMinutes:F0} min";
    public string LastContact { get; } = unit.LastContactAt is { } at ? MainViewModel.Time(at) : "—";

    public bool CanDispatch => Status == UnitStatus.Available && owner.SelectedIncidentId is not null;
    public bool CanCancel => Status is UnitStatus.Dispatched or UnitStatus.EnRoute;

    public IReadOnlyList<StatusOption> NextStatuses { get; } =
        UnitStatusRules.NextStatuses(unit.Status)
            // Dispatch and cancellation go through their own commands so the assignment is recorded.
            .Where(s => s is not (UnitStatus.Dispatched or UnitStatus.Cancelled))
            .Select(s => new StatusOption(s, EventDescriber.Humanize(s)))
            .ToList();

    /// <summary>Choosing a status in the board's drop-down issues the C2 command.</summary>
    public StatusOption? RequestedStatus
    {
        get => null;
        set
        {
            if (value is not null)
                _ = owner.RequestUnitStatusAsync(Id, value.Status);
        }
    }
}

/// <summary>Alert feed row with acknowledgement (Design Document §6.9).</summary>
public sealed record AlertRow(
    Guid Id,
    string Time,
    string Category,
    string Severity,
    string Title,
    string Message,
    bool Acknowledged);

/// <summary>A verification grade offered when assessing a report.</summary>
public sealed record VerificationOption(VerificationStatus Status, string Label);

/// <summary>Intelligence feed row: a report with its provenance (Design Document §6.7, §11.1).</summary>
public sealed class ReportRow(MainViewModel owner, Report report)
{
    public static IReadOnlyList<VerificationOption> Grades { get; } =
        Enum.GetValues<VerificationStatus>().Select(v => new VerificationOption(v, v.ToString())).ToList();

    public Guid Id { get; } = report.Id;
    public string Time { get; } = MainViewModel.Time(report.ReceivedAt);
    public string Source { get; } = report.SourceName;
    public string Claim { get; } = report.Claim;
    public string Confidence { get; } = report.Confidence.ToString();
    public string Verification { get; } = report.Verification.ToString();
    public string Accuracy { get; } = report.LocationAccuracyMeters is { } m ? $"±{m:F0} m" : "no location";
    public string Incident { get; } = report.Incident?.Number ?? "Unattributed";
    public bool IsUnattributed { get; } = report.IncidentId is null;
    public bool HasLocation { get; } = report.Location is not null;
    public bool CanLink => owner.SelectedIncidentId is { } id && id != report.IncidentId;

    public VerificationOption? RequestedVerification
    {
        get => null;
        set
        {
            if (value is not null)
                _ = owner.AssessReportAsync(Id, value.Status);
        }
    }
}

/// <summary>One line of the history log.</summary>
public sealed record TimelineRow(
    long Sequence,
    DateTimeOffset SimTime,
    string Time,
    string Category,
    string Text,
    bool Important,
    bool IsTruth);
