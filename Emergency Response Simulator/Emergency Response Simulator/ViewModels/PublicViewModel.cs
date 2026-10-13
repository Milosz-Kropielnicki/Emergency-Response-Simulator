using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>A rumour going round, with the rebuttal being typed for it.</summary>
public sealed partial class RumourRow(Guid id) : ObservableObject
{
    public Guid Id { get; } = id;
    [ObservableProperty] private string _claim = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _spreading;
    [ObservableProperty] private bool _contradicted;
    [ObservableProperty] private string _rebuttal = "";
}

/// <summary>What is known about the vulnerable people in one protective zone.</summary>
public sealed record VulnerableRow(Guid ZoneId, string Zone, string Counts, string Source, string Assistance, bool CanRequest);

public sealed record VolunteerRow(Guid IncidentId, string Incident, string Count, string Status, bool Waiting);

public sealed record MissingPersonRow(string Missing, string Caller, string LastKnown, string Status, bool Waiting, bool Alarm);

public sealed record LogRow(string Time, string Decision, string Rationale, string By);

/// <summary>
/// The PUBLIC tab (Phase 14): everything from outside the response that command has to manage. Rumours and the press
/// conference (the PIO); the vulnerable people known to be in each protective zone and assisted evacuation
/// (Operations, Logistics); volunteers at the cordon (Logistics); relatives' enquiries and the family assistance
/// centre (Liaison, Logistics); and the decision log (everyone). The same COP for everyone; who may act is the
/// permission matrix's to say.
/// </summary>
public sealed partial class PublicViewModel(MainViewModel owner, IC2Service c2, ICopService cop) : ObservableObject
{
    [ObservableProperty] private string _tabHeader = "PUBLIC";

    // ---- Press and rumours ----

    public ObservableCollection<RumourRow> Rumours { get; } = [];
    [ObservableProperty] private string _pressStatement = "";
    [ObservableProperty] private string _pressSummary = "";

    private bool CanPress => owner.Permissions.C2(nameof(IC2Service.HoldPressConferenceAsync));

    [RelayCommand(CanExecute = nameof(CanPress))]
    private async Task HoldPressConferenceAsync()
    {
        var result = await owner.RunCommandAsync(() => c2.HoldPressConferenceAsync(PressStatement, owner.SelectedIncidentId), "Press conference held");
        if (result?.Succeeded == true) PressStatement = "";
    }

    private bool CanRebut => owner.Permissions.C2(nameof(IC2Service.RebutRumourAsync));

    [RelayCommand(CanExecute = nameof(CanRebut))]
    private Task RebutAsync(RumourRow? row) => row is null ? Task.CompletedTask
        : owner.RunCommandAsync(() => c2.RebutRumourAsync(row.Id, row.Rebuttal), "Rumour rebutted");

    // ---- Vulnerable people ----

    public ObservableCollection<VulnerableRow> Vulnerable { get; } = [];

    private bool CanAssist => owner.Permissions.C2(nameof(IC2Service.RequestAssistedEvacuationAsync));

    [RelayCommand(CanExecute = nameof(CanAssist))]
    private Task AssistAsync(Guid zoneId) =>
        owner.RunCommandAsync(() => c2.RequestAssistedEvacuationAsync(zoneId), "Assisted evacuation requested");

    // ---- Volunteers ----

    public ObservableCollection<VolunteerRow> Volunteers { get; } = [];

    private bool CanVolunteers => owner.Permissions.C2(nameof(IC2Service.ManageVolunteersAsync));

    [RelayCommand(CanExecute = nameof(CanVolunteers))]
    private Task RegisterVolunteersAsync(Guid incidentId) =>
        owner.RunCommandAsync(() => c2.ManageVolunteersAsync(incidentId, VolunteerDecision.Register), "Volunteers registered");

    [RelayCommand(CanExecute = nameof(CanVolunteers))]
    private Task TurnAwayVolunteersAsync(Guid incidentId) =>
        owner.RunCommandAsync(() => c2.ManageVolunteersAsync(incidentId, VolunteerDecision.TurnAway), "Volunteers turned away");

    // ---- Families ----

    public ObservableCollection<MissingPersonRow> Enquiries { get; } = [];
    [ObservableProperty] private string _centreName = "Ringsend Community Centre";
    [ObservableProperty] private string _centreStatus = "";
    [ObservableProperty] private bool _centreOpen;

    private bool CanCentre => owner.Permissions.C2(nameof(IC2Service.OpenFamilyAssistanceCentreAsync));

    /// <summary>Opens the centre near the selected incident (outside the hazard: a kilometre upwind).</summary>
    [RelayCommand(CanExecute = nameof(CanCentre))]
    private Task OpenCentreAsync()
    {
        var incident = owner.SelectedIncidentId is { } id ? cop.FindIncident(id) : cop.Incidents.FirstOrDefault(i => i.Status != IncidentStatus.Closed);
        if (incident is null)
        {
            owner.ShowCommandResult(CommandResult.Fail("Select the incident the centre is for."));
            return Task.CompletedTask;
        }
        var upwind = cop.Weather?.WindFromDegrees ?? 270;
        var at = GeoMath.Destination(GeoPoint.FromPoint(incident.Location), upwind, 1000);
        return owner.RunCommandAsync(() => c2.OpenFamilyAssistanceCentreAsync(CentreName, at, incident.Id), "Family assistance centre open");
    }

    // ---- Decision log ----

    public ObservableCollection<LogRow> Log { get; } = [];
    [ObservableProperty] private string _decision = "";
    [ObservableProperty] private string _rationale = "";

    private bool CanLog => owner.Permissions.C2(nameof(IC2Service.RecordDecisionAsync));

    [RelayCommand(CanExecute = nameof(CanLog))]
    private async Task RecordDecisionAsync()
    {
        var result = await owner.RunCommandAsync(() => c2.RecordDecisionAsync(Decision, Rationale), "Decision logged");
        if (result?.Succeeded != true) return;
        Decision = "";
        Rationale = "";
    }

    // ---- Refresh ----

    public void Refresh()
    {
        var pub = cop.Public;
        var now = cop.AsOf;

        // Rumours: keep each row (and the rebuttal being typed) while the rumour lasts.
        foreach (var gone in Rumours.Where(r => pub.Rumours.All(x => x.Id != r.Id)).ToList()) Rumours.Remove(gone);
        foreach (var rumour in pub.Rumours.OrderByDescending(r => r.Spreading).ThenByDescending(r => r.ReachThousands))
        {
            var row = Rumours.FirstOrDefault(r => r.Id == rumour.Id);
            if (row is null) Rumours.Add(row = new RumourRow(rumour.Id));
            row.Claim = rumour.Claim;
            row.Spreading = rumour.Spreading;
            row.Contradicted = rumour.DenialExposedAt is not null;
            row.Status = rumour.DenialExposedAt is { } exposed ? $"DENIAL CONTRADICTED {MainViewModel.Time(exposed)}"
                : rumour.Spreading ? $"spreading · ~{rumour.ReachThousands}k seen · since {MainViewModel.Time(rumour.FirstSeenAt)}"
                : $"answered {MainViewModel.Time(rumour.RebuttedAt!.Value)} at ~{rumour.ReachThousands}k";
        }
        PressSummary = pub.PressConferences.Count == 0 ? "No press conference yet."
            : $"{pub.PressConferences.Count} press conference(s), last at {MainViewModel.Time(pub.PressConferences[^1])}.";

        Sync(Vulnerable, pub.Vulnerable.GroupBy(v => v.ZoneId).Select(g =>
        {
            var zone = cop.Zones.FirstOrDefault(z => z.Id == g.Key);
            var best = g.MaxBy(v => v.Total + v.PetOwners)!;
            var evacuation = zone?.Type == ZoneType.EvacuationZone;
            var asked = g.Select(v => v.AssistanceRequestedAt).FirstOrDefault(a => a is not null);
            return new VulnerableRow(g.Key, zone?.Name ?? "A lifted zone",
                $"{best.Elderly} elderly · {best.MobilityImpaired} can't leave unaided" + (best.Homeless > 0 ? $" · {best.Homeless} sleeping rough" : "")
                + (best.PetOwners > 0 ? $" · {best.PetOwners} with pets" : ""),
                string.Join("; ", g.Select(v => $"{v.Source} {MainViewModel.Time(v.ReportedAt)}")),
                asked is { } at ? $"assisted evacuation asked for {MainViewModel.Time(at)}" + (g.Max(v => v.Helped) is > 0 and var helped ? $", {helped} helped out" : "")
                    : evacuation ? "no assisted evacuation yet" : "shelter in place",
                evacuation && asked is null && zone is not null);
        }).ToList());

        Sync(Volunteers, pub.Volunteers.Select(v => new VolunteerRow(v.IncidentId,
            cop.FindIncident(v.IncidentId)?.Number ?? "An incident", $"{v.Count}",
            v.Decision switch
            {
                VolunteerDecision.Register => $"registered {MainViewModel.Time(v.DecidedAt!.Value)}",
                VolunteerDecision.TurnAway => $"turned away {MainViewModel.Time(v.DecidedAt!.Value)}",
                _ => $"waiting since {MainViewModel.Time(v.ArrivedAt)}" + (v.SelfDeployed > 0 ? $" · {v.SelfDeployed} WENT IN ON THEIR OWN" : ""),
            }, v.Waiting)).ToList());

        var centre = pub.AssistanceCentres.FirstOrDefault();
        CentreOpen = centre is not null;
        CentreStatus = centre is null
            ? pub.MissingPersons.Any(m => m.Waiting) ? "No family assistance centre: nothing can be traced until one opens." : "No family assistance centre."
            : $"{centre.Name}, open since {MainViewModel.Time(centre.OpenedAt)}" + (centre.Volunteers > 0 ? $" · {centre.Volunteers} volunteers helping" : "");
        Sync(Enquiries, pub.MissingPersons.OrderByDescending(m => m.Waiting).ThenByDescending(m => m.ReceivedAt).Select(m => new MissingPersonRow(
            m.Missing, m.Caller, m.LastKnown,
            m.TracedAt is { } traced
                ? $"{Core.Events.EventDescriberPublic.Outcome(m.Outcome!.Value)}: {m.Detail} ({MainViewModel.Time(traced)}){(m.Supported ? " · family liaison" : "")}"
                : $"waiting {(int)(now - m.ReceivedAt).TotalMinutes} min",
            m.Waiting, m.Outcome is TraceOutcome.StillInArea or TraceOutcome.Deceased)).ToList());

        Sync(Log, pub.DecisionLog.AsEnumerable().Reverse().Take(30)
            .Select(l => new LogRow(MainViewModel.Time(l.At), l.Decision, l.Rationale, l.By ?? "")).ToList());

        var needAssistance = pub.Vulnerable
            .Where(v => v.AssistanceRequestedAt is null && v.MobilityImpaired + v.Homeless > 0
                        && cop.Zones.Any(z => z.Id == v.ZoneId && z.Type == ZoneType.EvacuationZone))
            .Select(v => v.ZoneId).Distinct().Count();
        var pressing = pub.Rumours.Count(r => r.Spreading) + pub.Volunteers.Count(v => v.Waiting)
                       + (centre is null ? pub.MissingPersons.Count(m => m.Waiting) : 0) + needAssistance;
        TabHeader = pressing > 0 ? $"PUBLIC ({pressing})" : "PUBLIC";
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }
}
