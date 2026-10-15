using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One patient on the tracking board.</summary>
/// <param name="Alarm">0 fine, 1 needs attention (wrong place, P1 still at the scene), 2 died.</param>
public sealed record PatientRow(string Tag, string Triage, string Need, string Where, int Alarm);

/// <summary>Where one need's patients from the selected incident go.</summary>
public sealed record DestinationRow(string Need, string Hospital);

/// <summary>A hospital's specialist beds, blood, mortuary and walk-ins.</summary>
public sealed record HospitalCareRow(Guid HospitalId, string Name, string Ed, string Specialties, string Blood, string Morgue, bool BloodLow);

public sealed record ThreatRow(Guid ThreatId, string Title, string Figures, string Advice, string Orders, bool CanIsolate, bool CanQuarantine);

public sealed record PodRow(string Name, string Status, bool Alarm);

public sealed record CounsellingRow(string Where, string Status);

/// <summary>
/// The HEALTH tab (Phase 16): the patient tracking board and transport destinations (Operations' medical group),
/// hospitals' specialist beds, blood and mortuaries (Logistics), the dead (Operations and Logistics, with the coroner
/// notified through Liaison), public-health threats, dispensing, isolation and quarantine, and behavioural health. The
/// same COP for everyone; who may act is the permission matrix's to say.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly IC2Service _c2;
    private readonly ICopService _cop;

    public HealthViewModel(MainViewModel owner, IC2Service c2, ICopService cop)
    {
        (_owner, _c2, _cop) = (owner, c2, cop);
        _selectedNeed = Needs[0];
        _selectedSetting = Settings[0];
    }

    [ObservableProperty] private string _tabHeader = "HEALTH";
    [ObservableProperty] private string _incidentLabel = "";

    private Guid? Incident =>
        _owner.SelectedIncidentId is { } id && _cop.FindIncident(id) is not null ? id
        : _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).OrderByDescending(i => i.Priority).Select(i => (Guid?)i.Id).FirstOrDefault();

    private GeoPoint? IncidentLocation => Incident is { } id && _cop.FindIncident(id) is { } incident ? GeoPoint.FromPoint(incident.Location) : null;

    // ---- Patients and destinations ----

    public ObservableCollection<PatientRow> Patients { get; } = [];
    public ObservableCollection<DestinationRow> Destinations { get; } = [];
    [ObservableProperty] private string _patientSummary = "";

    public IReadOnlyList<Choice<Specialty?>> Needs { get; } =
        [.. new Specialty?[] { Specialty.Trauma, Specialty.Burns, Specialty.Paediatric, null }.Select(n => new Choice<Specialty?>(n, Health.Need(n)))];

    public ObservableCollection<Choice<Guid?>> HospitalChoices { get; } = [];
    [ObservableProperty] private Choice<Specialty?> _selectedNeed;
    [ObservableProperty] private Choice<Guid?>? _selectedHospital;

    private bool CanDistribute => _owner.Permissions.C2(nameof(IC2Service.SetTransportDestinationAsync));

    [RelayCommand(CanExecute = nameof(CanDistribute))]
    private async Task SetDestinationAsync()
    {
        if (Incident is not { } incident)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select the incident the patients are from."));
            return;
        }
        var result = await _owner.RunCommandAsync(() => _c2.SetTransportDestinationAsync(incident, SelectedNeed.Value, SelectedHospital?.Value), "Destination set");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    // ---- Hospitals ----

    public ObservableCollection<HospitalCareRow> Hospitals { get; } = [];
    [ObservableProperty] private string _bloodUnits = "40";

    private bool CanBlood => _owner.Permissions.C2(nameof(IC2Service.RequestBloodAsync));

    [RelayCommand(CanExecute = nameof(CanBlood))]
    private Task RequestBloodAsync(Guid hospitalId)
    {
        if (int.TryParse(BloodUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
            return _owner.RunCommandAsync(() => _c2.RequestBloodAsync(hospitalId, units), "Blood requested from the IBTS");
        _owner.ShowCommandResult(CommandResult.Fail("Units of blood must be a whole number."));
        return Task.CompletedTask;
    }

    // ---- The dead ----

    [ObservableProperty] private string _fatalitySummary = "";
    [ObservableProperty] private string _mortuaryName = "Irishtown Stadium";
    [ObservableProperty] private bool _mortuaryOpen;

    private bool CanMortuary => _owner.Permissions.C2(nameof(IC2Service.OpenTemporaryMortuaryAsync));

    /// <summary>Opens the temporary mortuary a kilometre and a half upwind of the selected incident, away from the scene and the public.</summary>
    [RelayCommand(CanExecute = nameof(CanMortuary))]
    private Task OpenMortuaryAsync()
    {
        if (IncidentLocation is not { } at)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select the incident the mortuary is for."));
            return Task.CompletedTask;
        }
        var site = GeoMath.Destination(at, _cop.Weather?.WindFromDegrees ?? 270, 1500);
        return _owner.RunCommandAsync(() => _c2.OpenTemporaryMortuaryAsync(MortuaryName, site, Incident), "Temporary mortuary open");
    }

    private bool CanNotify => _owner.Permissions.C2(nameof(IC2Service.NotifyAsync));

    [RelayCommand(CanExecute = nameof(CanNotify))]
    private Task NotifyCoronerAsync() =>
        _owner.RunCommandAsync(() => _c2.NotifyAsync("Dublin District Coroner",
            "Fatalities at the scene. Requesting the coroner's authority for recovery to the mortuary and identification.", Incident), "Coroner notified");

    // ---- Public health ----

    public ObservableCollection<ThreatRow> Threats { get; } = [];
    public ObservableCollection<PodRow> Pods { get; } = [];
    [ObservableProperty] private string _podName = "Ringsend College";

    private bool CanOrders => _owner.Permissions.C2(nameof(IC2Service.OrderIsolationAsync));

    [RelayCommand(CanExecute = nameof(CanOrders))]
    private Task IsolateAsync(Guid threatId) => _owner.RunCommandAsync(() => _c2.OrderIsolationAsync(threatId), "Isolation ordered");

    [RelayCommand(CanExecute = nameof(CanOrders))]
    private Task QuarantineAsync(Guid threatId) => _owner.RunCommandAsync(() => _c2.OrderQuarantineAsync(threatId), "Quarantine ordered");

    private bool CanDispense => _owner.Permissions.C2(nameof(IC2Service.OpenDispensingPointAsync));

    /// <summary>A point of dispensing a kilometre upwind of the threat, outside the area it affects.</summary>
    [RelayCommand(CanExecute = nameof(CanDispense))]
    private async Task OpenPodAsync(Guid threatId)
    {
        if (_cop.Health.Threats.FirstOrDefault(t => t.Id == threatId) is not { } threat) return;
        var site = GeoMath.Destination(threat.Location, (_cop.Weather?.WindFromDegrees ?? 270) + _cop.Health.Dispensing.Count(d => d.ThreatId == threatId) * 40, 1200);
        var result = await _owner.RunCommandAsync(() => _c2.OpenDispensingPointAsync(threatId, PodName, site), "Point of dispensing opening");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    // ---- Behavioural health ----

    public ObservableCollection<CounsellingRow> Counselling { get; } = [];
    [ObservableProperty] private string _distressSummary = "";

    public IReadOnlyList<Choice<CounsellingSetting>> Settings { get; } =
        [.. Enum.GetValues<CounsellingSetting>().Select(s => new Choice<CounsellingSetting>(s, Health.Name(s) is var n ? char.ToUpperInvariant(n[0]) + n[1..] : ""))];

    [ObservableProperty] private Choice<CounsellingSetting> _selectedSetting;

    private bool CanCounsel => _owner.Permissions.C2(nameof(IC2Service.DeployCrisisCounsellingAsync));

    [RelayCommand(CanExecute = nameof(CanCounsel))]
    private Task DeployCounsellingAsync() =>
        _owner.RunCommandAsync(() => _c2.DeployCrisisCounsellingAsync(SelectedSetting.Value == CounsellingSetting.Helpline ? null : Incident, SelectedSetting.Value),
            SelectedSetting.Value == CounsellingSetting.Helpline ? "Helpline opening" : "Counselling team on the way");

    // ---- Refresh ----

    public void Refresh()
    {
        var health = _cop.Health;
        var incidentId = Incident;
        var incident = incidentId is { } id ? _cop.FindIncident(id) : null;
        IncidentLabel = incident is null ? "No incident" : $"{incident.Number} · {incident.Address ?? EventDescriber.Humanize(incident.Type)}";
        string HospitalName(Guid? h) => h is { } hid ? _cop.Hospitals.FirstOrDefault(x => x.Id == hid)?.Name ?? "a hospital" : "—";

        // Patients.
        var patients = health.Patients.Where(p => incidentId is null || p.IncidentId == incidentId).ToList();
        PatientSummary = patients.Count == 0 ? "No patients tagged yet: crews tag each casualty as they start treating them."
            : $"{patients.Count} tagged · {patients.Count(p => p.State == CasualtyState.TreatedOnScene)} at the scene · " +
              $"{patients.Count(p => p.State == CasualtyState.Transporting)} on the way · {patients.Count(p => p.ArrivedAt is not null && p.DiedAt is null)} in hospital" +
              (patients.Count(p => p.RightPlace == false) is > 0 and var wrong ? $" · {wrong} in the wrong place" : "") +
              (patients.Count(p => p.DiedAt is not null) is > 0 and var died ? $" · {died} died" : "");
        Sync(Patients, patients.OrderBy(p => p.DiedAt is not null).ThenBy(p => p.State == CasualtyState.AtHospital).ThenBy(p => p.Triage).ThenBy(p => p.Tag)
            .Select(p => new PatientRow(p.Tag, EventDescriber.TriageLabel(p.Triage), Health.Need(p.Need),
                p.DiedAt is { } died ? $"died {MainViewModel.Time(died)}"
                : p.State switch
                {
                    CasualtyState.Transporting => $"→ {HospitalName(p.HospitalId)}",
                    CasualtyState.AtHospital => $"{HospitalName(p.HospitalId)}{(p.RightPlace == false ? " (WRONG PLACE: transfer due)" : p.Transfers > 0 ? " (transferred)" : "")}",
                    _ => $"at the scene since {MainViewModel.Time(p.TaggedAt)}",
                },
                p.DiedAt is not null ? 2 : p.RightPlace == false || p.Triage == Triage.Immediate && p.State == CasualtyState.TreatedOnScene ? 1 : 0)).ToList());

        var hospitalChoices = new List<Choice<Guid?>> { new(null, "Nearest (clear)") };
        hospitalChoices.AddRange(_cop.Hospitals.OrderBy(h => h.Name).Select(h => new Choice<Guid?>(h.Id, h.Name)));
        if (!HospitalChoices.Select(c => c.Value).SequenceEqual(hospitalChoices.Select(c => c.Value)))
        {
            var keep = SelectedHospital?.Value;
            Sync(HospitalChoices, hospitalChoices);
            SelectedHospital = HospitalChoices.FirstOrDefault(c => c.Value == keep) ?? HospitalChoices.ElementAtOrDefault(1);
        }
        Sync(Destinations, health.Destinations.Where(d => d.IncidentId == incidentId)
            .Select(d => new DestinationRow(Health.Need(d.Need), HospitalName(d.HospitalId))).ToList());

        // Hospitals.
        Sync(Hospitals, _cop.Hospitals.OrderBy(h => h.Name).Select(h =>
        {
            var care = health.Hospitals.GetValueOrDefault(h.Id);
            var worried = health.WorriedWell.GetValueOrDefault(h.Id);
            return new HospitalCareRow(h.Id, h.Name + (care?.ChildrenOnly == true ? " (children)" : ""),
                $"ED {h.Occupied}/{h.Capacity}{(h.OnDiversion ? " DIVERTING" : "")}{(worried > 0 ? $" · {worried} worried well" : "")}",
                care is null || care.Specialties.Count == 0 ? "no specialist beds" : string.Join(" · ", care.Specialties.Select(s => $"{Health.Name(s.Specialty)} {s.Free}/{s.Beds} free")),
                care is null ? "" : $"blood {care.BloodUnits} u",
                care is null ? "" : $"mortuary {care.MorgueOccupied}/{care.MorgueSpaces}",
                care?.BloodLowSince is not null);
        }).ToList());

        // The dead.
        var fatalities = health.Fatalities;
        var atScene = fatalities.AtScene.Values.Sum();
        MortuaryOpen = fatalities.MortuaryOpenedAt is not null;
        FatalitySummary = atScene + fatalities.Recovered == 0 ? "No dead reported."
            : $"{atScene} dead at the scene · {fatalities.Recovered} recovered · {fatalities.Identified} identified · " +
              (fatalities.CoronerNotifiedAt is { } coroner ? $"coroner notified {MainViewModel.Time(coroner)}" : "CORONER NOT NOTIFIED: nobody can be moved") +
              (fatalities.MortuaryName is { } mortuary ? $" · temporary mortuary: {mortuary} ({fatalities.InMortuary} there)" : " · no temporary mortuary");

        // Public health.
        Sync(Threats, health.Threats.Select(t => new ThreatRow(t.Id, $"{t.Agent} ({EventDescriber.Humanize(t.Kind).ToLowerInvariant()}) {t.Area}",
            $"~{t.EstimatedExposed} exposed · {t.Protected} given {Health.Countermeasure(t.Kind)} · {t.Ill} ill · {t.Hospitalised} in hospital · {t.Deaths} died",
            t.Advice,
            string.Join(" · ", new[]
            {
                t.IsolationOrderedAt is { } i ? $"isolation {MainViewModel.Time(i)}" : null,
                t.QuarantineOrderedAt is { } q ? $"quarantine {MainViewModel.Time(q)}" : null,
            }.OfType<string>()),
            Health.Contagious(t.Kind) && t.IsolationOrderedAt is null, Health.Contagious(t.Kind) && t.QuarantineOrderedAt is null)).ToList());
        Sync(Pods, health.Dispensing.Select(d => new PodRow(d.Name,
            (d.Ready ? $"open · {d.Dispensed} dispensed · {d.Queue} queuing" : $"setting up since {MainViewModel.Time(d.OpenedAt)}") +
            (d.OutOfStockSince is not null ? " · OUT OF DOSES" : ""), d.OutOfStockSince is not null)).ToList());

        // Behavioural health.
        var distressed = incidentId is { } forIncident ? health.Distressed.GetValueOrDefault(forIncident) : health.Distressed.Values.Sum();
        DistressSummary = distressed == 0 ? "No distress reported." : $"About {distressed} people in distress (survivors, witnesses, evacuees, families).";
        Sync(Counselling, health.Counselling.Where(c => c.IncidentId is null || c.IncidentId == incidentId).Select(c => new CounsellingRow(
            c.Setting == CounsellingSetting.Helpline ? "Helpline" : $"Team at {Health.Name(c.Setting)}",
            c.Seen == 0 ? $"on the way since {MainViewModel.Time(c.DeployedAt)}"
                : $"{c.Seen} seen: {c.Low} low, {c.Moderate} moderate, {c.High} high risk ({c.Admitted} to a psychiatric bed)")).ToList());

        var pressing = patients.Count(p => p.RightPlace == false && p.DiedAt is null)
                       + health.Hospitals.Values.Count(h => h.BloodLowSince is not null)
                       + (atScene > 0 && fatalities.CoronerNotifiedAt is null ? 1 : 0)
                       + health.Threats.Count(t => !health.Dispensing.Any(d => d.ThreatId == t.Id));
        TabHeader = pressing > 0 ? $"HEALTH ({pressing})" : "HEALTH";
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }
}
