using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One supply at the incident: on hand, how fast it is going, how long it will last.</summary>
/// <param name="Alarm">0 fine, 1 low, 2 run out.</param>
public sealed record SupplyRow(string Name, string OnHand, string Rate, string Left, string Status, int Alarm);

public sealed record SupplyOrderRow(string What, string Status, bool Alarm);

/// <summary>One appliance's fuel, condition and repair, with what can be done about it.</summary>
public sealed record FleetRow(Guid UnitId, string Callsign, string Fuel, string Condition, bool CanService, bool CanFieldRepair, bool CanWorkshop, bool Alarm);

public sealed record FacilityRow(string Name, string Kind, string Units);

public sealed record CostRow(string Category, string Amount);

public sealed record ContractRow(string Vendor, string What, string Value, string Note, bool Alarm);

public sealed record ClaimRow(string Submitted, string Status, string Disallowed);

public sealed record Choice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The LOGISTICS tab (Phase 15): keeping the operation going. Supplies at the selected incident and orders for more
/// (Logistics' supply unit); staging areas and base camps, and staging units (Operations); the fleet's fuel, defects and
/// repairs (ground support); and costs, contracts and the claim to central government (Finance). The same COP for
/// everyone; who may act is the permission matrix's to say.
/// </summary>
public sealed partial class LogisticsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly IC2Service _c2;
    private readonly ICopService _cop;

    public LogisticsViewModel(MainViewModel owner, IC2Service c2, ICopService cop)
    {
        (_owner, _c2, _cop) = (owner, c2, cop);
        _selectedKind = Kinds[0];
        _quantity = Supplies.TypicalOrder(_selectedKind.Value).ToString(CultureInfo.InvariantCulture);
        _selectedTier = Tiers[0];
        _selectedContractKind = ContractKinds[0];
    }

    [ObservableProperty] private string _tabHeader = "LOGISTICS";
    [ObservableProperty] private string _incidentLabel = "";

    private Guid? Incident =>
        _owner.SelectedIncidentId is { } id && _cop.FindIncident(id) is not null ? id
        : _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).OrderByDescending(i => i.Priority).Select(i => (Guid?)i.Id).FirstOrDefault();

    // ---- Supplies and orders ----

    public ObservableCollection<SupplyRow> Stocks { get; } = [];
    public ObservableCollection<SupplyOrderRow> Orders { get; } = [];
    [ObservableProperty] private string _suppliesSummary = "";

    public IReadOnlyList<Choice<SupplyKind>> Kinds { get; } = [.. Supplies.All.Select(k => new Choice<SupplyKind>(k, $"{Supplies.Name(k)} ({Supplies.Measure(k)})"))];
    public IReadOnlyList<Choice<SupplyTier>> Tiers { get; } = [.. Enum.GetValues<SupplyTier>().Select(t => new Choice<SupplyTier>(t, Supplies.TierName(t)))];

    [ObservableProperty] private Choice<SupplyKind> _selectedKind;
    [ObservableProperty] private string _quantity;
    [ObservableProperty] private Choice<SupplyTier> _selectedTier;
    [ObservableProperty] private string _justification = "";
    [ObservableProperty] private string _tierHint = "";

    partial void OnSelectedKindChanged(Choice<SupplyKind> value) =>
        Quantity = Supplies.TypicalOrder(value.Value).ToString(CultureInfo.InvariantCulture);

    partial void OnSelectedTierChanged(Choice<SupplyTier> value) => TierHint = value.Value switch
    {
        SupplyTier.Local => $"Decided in a minute, here in about {Supplies.LeadTime(SupplyTier.Local).TotalMinutes:F0}; the council's stores run out.",
        SupplyTier.Regional => $"Approved in a few minutes, here in about {Supplies.LeadTime(SupplyTier.Regional).TotalMinutes:F0}; +15%. Say why.",
        _ => $"Only in a declared major emergency; here in about {Supplies.LeadTime(SupplyTier.National).TotalMinutes:F0}; +35%. Say why.",
    };

    private bool CanOrder => _owner.Permissions.C2(nameof(IC2Service.OrderSuppliesAsync));

    [RelayCommand(CanExecute = nameof(CanOrder))]
    private async Task OrderAsync()
    {
        if (Incident is not { } incident)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select the incident the supplies are for."));
            return;
        }
        if (!int.TryParse(Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity))
        {
            _owner.ShowCommandResult(CommandResult.Fail("The quantity must be a whole number."));
            return;
        }
        var result = await _owner.RunCommandAsync(() => _c2.OrderSuppliesAsync(incident, SelectedKind.Value, quantity, SelectedTier.Value,
            string.IsNullOrWhiteSpace(Justification) ? null : Justification), "Supplies ordered");
        if (result?.Succeeded != true) return;
        if (result.Detail is { } detail) _owner.ShowCommandResult(result, detail);
        Justification = "";
    }

    // ---- Staging areas and base camps ----

    public ObservableCollection<FacilityRow> Facilities { get; } = [];
    public ObservableCollection<Choice<Guid>> StagingAreas { get; } = [];
    [ObservableProperty] private Choice<Guid>? _selectedStaging;
    [ObservableProperty] private string _stagingHint = "";

    private bool CanStage => _owner.Permissions.C2(nameof(IC2Service.StageUnitAsync));

    /// <summary>Sends the unit selected on the resource board to the chosen staging area.</summary>
    [RelayCommand(CanExecute = nameof(CanStage))]
    private Task StageSelectedAsync()
    {
        if (_owner.SelectedUnitId is not { } unit)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select a unit on the resource board first."));
            return Task.CompletedTask;
        }
        if (SelectedStaging is not { } staging)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Choose a staging area (draw one with the zone tool)."));
            return Task.CompletedTask;
        }
        return _owner.RunCommandAsync(() => _c2.StageUnitAsync(unit, staging.Value), "Unit sent to staging");
    }

    // ---- The fleet ----

    public ObservableCollection<FleetRow> Fleet { get; } = [];

    private bool CanGroundSupport => _owner.Permissions.C2(nameof(IC2Service.RequestRepairAsync));

    [RelayCommand(CanExecute = nameof(CanGroundSupport))]
    private Task ServiceAsync(Guid unitId) => _owner.RunCommandAsync(() => _c2.ServiceUnitAsync(unitId), "Sent for servicing");

    [RelayCommand(CanExecute = nameof(CanGroundSupport))]
    private Task FieldRepairAsync(Guid unitId) => _owner.RunCommandAsync(() => _c2.RequestRepairAsync(unitId, RepairKind.FieldRepair), "Mechanic on the way");

    [RelayCommand(CanExecute = nameof(CanGroundSupport))]
    private Task WorkshopAsync(Guid unitId) => _owner.RunCommandAsync(() => _c2.RequestRepairAsync(unitId, RepairKind.Workshop), "Recovery to the workshop arranged");

    // ---- Finance ----

    public ObservableCollection<CostRow> Costs { get; } = [];
    public ObservableCollection<ContractRow> Contracts { get; } = [];
    public ObservableCollection<ClaimRow> Claims { get; } = [];
    [ObservableProperty] private string _costSummary = "";
    [ObservableProperty] private string _claimSummary = "";

    public IReadOnlyList<Choice<ContractKind>> ContractKinds { get; } =
        [.. Enum.GetValues<ContractKind>().Select(k => new Choice<ContractKind>(k, k switch
        {
            ContractKind.Catering => "Catering (meals and water)",
            ContractKind.Sanitation => "Sanitation (toilets, washing)",
            ContractKind.FuelSupply => "Fuel supply (bowser)",
            ContractKind.PlantHire => "Plant hire (excavator)",
            _ => "Other",
        }))];

    [ObservableProperty] private Choice<ContractKind> _selectedContractKind;
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _scope = "";
    [ObservableProperty] private string _contractValue = "";
    [ObservableProperty] private string _contractJustification = "";

    public string ProcurementHint { get; } =
        $"Above {CostRates.Money(CostRates.ProcurementThreshold)}, a direct award needs a written justification, or it can't be claimed back.";

    private bool CanContract => _owner.Permissions.C2(nameof(IC2Service.AwardContractAsync));

    [RelayCommand(CanExecute = nameof(CanContract))]
    private async Task AwardContractAsync()
    {
        if (!decimal.TryParse(ContractValue.Replace("€", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            _owner.ShowCommandResult(CommandResult.Fail("Give the contract's value in euro."));
            return;
        }
        var result = await _owner.RunCommandAsync(() => _c2.AwardContractAsync(SelectedContractKind.Value, Vendor, Scope, value,
            string.IsNullOrWhiteSpace(ContractJustification) ? null : ContractJustification, Incident), "Contract awarded");
        if (result?.Succeeded != true) return;
        if (result.Detail is { } detail) _owner.ShowCommandResult(result, detail);
        (Vendor, Scope, ContractValue, ContractJustification) = ("", "", "", "");
    }

    private bool CanClaim => _owner.Permissions.C2(nameof(IC2Service.SubmitReimbursementClaimAsync));

    [RelayCommand(CanExecute = nameof(CanClaim))]
    private Task ClaimAsync() => _owner.RunCommandAsync(() => _c2.SubmitReimbursementClaimAsync(), "Claim submitted");

    // ---- Refresh ----

    public void Refresh()
    {
        var logistics = _cop.Logistics;
        var now = _cop.AsOf;
        var incidentId = Incident;
        var incident = incidentId is { } id ? _cop.FindIncident(id) : null;
        IncidentLabel = incident is null ? "No incident" : $"{incident.Number} · {incident.Address ?? incident.Type.ToString()}";
        string Callsign(Guid unit) => _cop.FindUnit(unit)?.Callsign ?? "A unit";
        string Number(Guid incident) => _cop.FindIncident(incident)?.Number ?? "an incident";

        // Supplies at the incident.
        var supplies = incidentId is { } at ? logistics.Incidents.GetValueOrDefault(at) : null;
        SuppliesSummary = supplies is null ? "No return from the supply unit yet: it reports once crews are at work."
            : $"{supplies.Personnel} people working · supply unit return at {MainViewModel.Time(supplies.ReportedAt)}";
        Sync(Stocks, (supplies?.Stocks ?? []).OrderByDescending(s => s.OutSince is not null).ThenByDescending(s => s.LowSince is not null)
            .ThenBy(s => s.Kind).Select(s => new SupplyRow(Supplies.Name(s.Kind), Supplies.Quantity(s.Kind, s.OnHand),
                s.PerHour > 0 ? $"{Supplies.Quantity(s.Kind, s.PerHour)}/h" : "—",
                s.OutSince is not null ? "OUT" : s.HoursLeft is { } h ? h >= 10 ? "10 h+" : h >= 1 ? $"{h:0.#} h" : $"{Math.Max(1, (int)(h * 60))} min" : "—",
                s.OutSince is { } outSince ? $"out since {MainViewModel.Time(outSince)}" : s.LowSince is not null ? "low" : "",
                s.OutSince is not null ? 2 : s.LowSince is not null ? 1 : 0)).ToList());

        Sync(Orders, logistics.Orders.Where(o => incidentId is null || o.IncidentId == incidentId)
            .OrderByDescending(o => o.Open).ThenByDescending(o => o.RequestedAt).Take(12)
            .Select(o => new SupplyOrderRow($"{Supplies.Quantity(o.Kind, o.Granted ?? o.Quantity)} {Supplies.Name(o.Kind).ToLowerInvariant()} · {o.Tier}",
                o.Status switch
                {
                    SupplyOrderStatus.Requested => $"ordered {MainViewModel.Time(o.RequestedAt)}, awaiting {Supplies.Supplier(o.Tier)}",
                    SupplyOrderStatus.Approved => $"{o.Supplier}, due {(o.ExpectedAt is { } due ? MainViewModel.Time(due) : "?")}" + (o.Reason is { } why ? $" ({why})" : ""),
                    SupplyOrderStatus.Declined => $"declined: {o.Reason}",
                    _ => $"delivered {MainViewModel.Time(o.DeliveredAt!.Value)}, {CostRates.Money(o.Cost)}",
                }, o.Status == SupplyOrderStatus.Declined)).ToList());

        // Staging areas and base camps.
        var facilities = _cop.Zones.Where(z => z.Type is ZoneType.StagingArea or ZoneType.BaseCamp).ToList();
        Sync(Facilities, facilities.Select(z =>
        {
            var staged = logistics.Fleet.Values.Where(f => f.StagingZoneId == z.Id)
                .Select(f => Callsign(f.UnitId) + (f.StagedAt is null ? " (on the way)" : "")).ToList();
            var kind = (z.Type == ZoneType.BaseCamp ? "Base camp" : "Staging") + (z.IncidentId is { } of ? $" · {Number(of)}" : "");
            return new FacilityRow(z.Name, kind, staged.Count > 0 ? string.Join(", ", staged) : z.Type == ZoneType.BaseCamp ? "" : "no units staged");
        }).ToList());
        var stagingChoices = facilities.Where(z => z.Type == ZoneType.StagingArea).Select(z => new Choice<Guid>(z.Id, z.Name)).ToList();
        if (!StagingAreas.Select(s => s.Value).SequenceEqual(stagingChoices.Select(s => s.Value)))
        {
            var keep = SelectedStaging?.Value;
            Sync(StagingAreas, stagingChoices);
            SelectedStaging = StagingAreas.FirstOrDefault(s => s.Value == keep) ?? StagingAreas.FirstOrDefault();
        }
        StagingHint = facilities.Count == 0
            ? "No staging area or base camp: deliveries have nowhere to unload. Draw one with the zone tool (Staging area, Base camp)."
            : _owner.SelectedUnitId is { } unit ? $"Selected unit: {Callsign(unit)}" : "Select a unit on the resource board to stage it.";

        // The fleet: anything with something to say.
        Sync(Fleet, logistics.Fleet.Values
            .Where(f => f.Fuel is < 0.5 || f.Defect is not null || f.Repair is not null || f.RepairedAt is { } back && now - back < TimeSpan.FromMinutes(15))
            .Select(f => (Record: f, Alarm: f.Immobilised || f.Dry || f.Fuel is <= 0.1))
            .OrderByDescending(x => x.Alarm).ThenBy(x => Callsign(x.Record.UnitId))
            .Select(x =>
            {
                var f = x.Record;
                var condition = f.Repair is { } repair
                    ? $"{(repair == RepairKind.Service ? "being serviced" : repair == RepairKind.FieldRepair ? "mechanic" : "in the workshop")}" +
                      (f.RepairExpectedAt is { } back ? $", back {MainViewModel.Time(back)}" : "")
                    : f.Immobilised ? $"BROKEN DOWN: {f.Defect} ({(f.FieldRepairable ? "mechanic can fix" : "needs the workshop")})"
                    : f.Defect is { } defect ? $"{defect} — still working"
                    : f.RepairedAt is { } fixedAt ? $"back in service {MainViewModel.Time(fixedAt)}" : "";
                var free = f.Repair is null;
                return new FleetRow(f.UnitId, Callsign(f.UnitId),
                    f.Fuel is { } fuel ? fuel <= 0 ? "DRY" : $"{fuel:P0}" : "—", condition,
                    free && !f.Immobilised, free && f.Immobilised && f.FieldRepairable, free && f.Immobilised, x.Alarm);
            }).ToList());

        // Finance.
        Sync(Costs, logistics.Costs.Select(c => new CostRow(CostRates.Name(c.Category), CostRates.Money(c.Amount))).ToList());
        CostSummary = logistics.CostsReportedAt is { } costsAt
            ? $"Total {CostRates.Money(logistics.TotalCost)} at {MainViewModel.Time(costsAt)}"
            : "No figures from the finance section yet.";
        ClaimSummary = logistics.MajorEmergencySince is null
            ? "No major emergency declared: these costs fall to the service."
            : $"Claimable now: {CostRates.Money(logistics.Reimbursable)} (eligible costs since {MainViewModel.Time(logistics.MajorEmergencySince.Value)}, not yet claimed).";
        Sync(Contracts, logistics.Contracts.OrderByDescending(c => c.AwardedAt).Select(c => new ContractRow(c.Vendor,
            $"{c.Scope}{(c.IncidentId is { } of ? $" · {Number(of)}" : "")}", CostRates.Money(c.Value),
            (c.Documented ? "" : "no justification · ") + (c.Deliveries > 0 ? $"{c.Deliveries} deliveries" : $"let {MainViewModel.Time(c.AwardedAt)}"),
            !c.Documented)).ToList());
        Sync(Claims, logistics.Claims.OrderByDescending(c => c.SubmittedAt).Select(c => new ClaimRow(
            $"{CostRates.Money(c.Amount)} at {MainViewModel.Time(c.SubmittedAt)}",
            c.Approved is { } approved ? $"{CostRates.Money(approved)} approved {MainViewModel.Time(c.DecidedAt!.Value)}" : "with central government",
            string.Join("; ", c.Disallowed))).ToList());

        var pressing = (supplies?.Stocks.Count(s => s.LowSince is not null || s.OutSince is not null) ?? 0)
                       + logistics.Fleet.Values.Count(f => f.Repair is null && (f.Immobilised || f.Dry));
        TabHeader = pressing > 0 ? $"LOGISTICS ({pressing})" : "LOGISTICS";
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }
}
