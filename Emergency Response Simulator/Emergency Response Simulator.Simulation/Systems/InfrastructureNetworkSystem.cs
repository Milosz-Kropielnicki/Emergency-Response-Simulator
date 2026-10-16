using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Critical infrastructure and how it depends on itself (Phase 17). Power is the root (<see cref="InfrastructureSystem"/>);
/// this follows what hangs off it and off the hazards.
/// <list type="bullet">
/// <item><b>Water</b>: a pumping station in a power cut runs on its standby generator for as many hours as it has fuel
/// (a site's <c>Quantity</c>; none for some), then fails, and mains pressure goes across the area it serves. Fire crews
/// there get half the water from hydrants unless a tanker or water tender of ours is relaying at the incident; hospitals
/// there lose a tenth of their emergency capacity. A mobile generator (<see cref="WorldProtection.GeneratorLeadTime"/>)
/// keeps it going.</item>
/// <item><b>Gas</b>: a main reached by fire, flood or an explosion leaks. Someone rings 999 about the smell. Near fire
/// it ignites (3% a minute; 0.3% elsewhere), hurting whoever is close and starting a fire. Gas Networks Ireland
/// isolates it <see cref="IsolationTime"/> after being asked (sooner if already told), cutting supply to everyone it feeds.</item>
/// <item><b>Telecoms</b>: masts run on batteries in a power cut, then fail (<c>CommsSystem</c>); a generator keeps one up.</item>
/// <item><b>Rail and tram</b>: an electric line stops in a power cut. Trains run on through smoke, a toxic cloud or an
/// evacuation zone until the operator is told (a notification to Irish Rail, the DART or the Luas), and the operator
/// asks command whether to stop.</item>
/// <item><b>Hospitals</b>: IT lost to ransomware costs 30% of emergency capacity (15% working manually); see <see cref="CyberSystem"/>.</item>
/// </list>
/// Each operator reports its assets' state to command <see cref="OperatorReportDelay"/> after a change.
/// </summary>
public sealed class InfrastructureNetworkSystem(IRoutingService? routing = null) : ISimulationSystem
{
    public static readonly TimeSpan OperatorReportDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan GasCallDelay = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan IsolationTime = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan NotifiedIsolationTime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RailStopTime = TimeSpan.FromMinutes(5);
    public const double CustomersPerSquareKm = 4000;
    public const double HospitalWaterFactor = 0.9;
    public const double HospitalCyberFactor = 0.7;
    public const double HospitalCyberFallbackFactor = 0.85;

    private readonly HashSet<Guid> _gasCalled = [];
    private readonly Dictionary<Guid, DateTimeOffset> _railExposed = [];
    private readonly HashSet<Guid> _railAsked = [];
    private readonly Dictionary<Guid, int> _ignitionChecks = [];

    public int Order => 9; // after power (8); before units (10) and the hospitals (12) read what it leaves

    public void Update(SimulationContext context)
    {
        var world = context.World;
        foreach (var site in world.Sites.Values)
            if (!world.Protection.Assets.ContainsKey(site.Id) && Protection.KindOf(site.Kind) is { } kind)
                world.Protection.Assets[site.Id] = new WorldProtection.Asset(site, kind);

        foreach (var asset in world.Protection.Assets.Values)
        {
            if (!asset.Damaged && world.Protection.Damage.TryGetValue(asset.Site.Id, out var cause))
                (asset.Damaged, asset.DamageCause) = (true, cause);
            var (status, detail, customers) = asset.Kind switch
            {
                InfrastructureKind.Power => Power(world, asset),
                InfrastructureKind.Telecom => Telecom(world, asset, context.SimTime),
                InfrastructureKind.Water => Water(context, asset),
                InfrastructureKind.Gas => Gas(context, asset),
                _ => Rail(context, asset),
            };
            if (status != asset.Status) asset.ChangedAt = context.SimTime;
            (asset.Status, asset.Detail, asset.Customers) = (status, detail, customers);
            Report(context, asset);
        }

        Hospitals(context);
    }

    private static int Customers(HazardSite site, double share = 1) =>
        (int)(Math.Round(Math.PI * Math.Pow(site.ServiceRadiusMeters / 1000, 2) * CustomersPerSquareKm * share / 100) * 100);

    private static (InfrastructureStatus, string, int) Power(WorldState world, WorldProtection.Asset asset)
    {
        var outage = world.Outages.Values.FirstOrDefault(o => o.SiteId == asset.Site.Id);
        return outage is null
            ? (InfrastructureStatus.Normal, "Supply normal.", 0)
            : (InfrastructureStatus.Failed, $"{outage.Cause}. Supply lost within {outage.RadiusMeters:F0} m.", Customers(asset.Site));
    }

    private static (InfrastructureStatus, string, int) Telecom(WorldState world, WorldProtection.Asset asset, DateTimeOffset now)
    {
        var mast = world.Radio.Masts.GetValueOrDefault(asset.Site.Id);
        if (mast is { Down: true })
            return (InfrastructureStatus.Failed, $"Mast down: {mast.DownCause ?? "fault"}. No mobile service within {asset.Site.ServiceRadiusMeters:F0} m.", Customers(asset.Site));
        if (mast?.OnBatterySince is not null)
            return (InfrastructureStatus.OnBackup, "Mains power lost: running on batteries.", 0);
        return (InfrastructureStatus.Normal, asset.Generator(now) && world.InOutage(asset.Site.Location)
            ? "Running on a mobile generator." : "Service normal.", 0);
    }

    private static (InfrastructureStatus, string, int) Water(SimulationContext context, WorldProtection.Asset asset)
    {
        var world = context.World;
        var site = asset.Site;
        if (asset.Damaged)
            return (InfrastructureStatus.Failed, $"Pumping station out of action ({asset.DamageCause}). No mains pressure within {site.ServiceRadiusMeters:F0} m.",
                Customers(site));
        var generator = asset.Generator(context.SimTime);
        if (!world.InOutage(site.Location) || generator)
        {
            var wasDown = asset.BackupSince is not null;
            asset.BackupSince = null;
            return (InfrastructureStatus.Normal, generator && world.InOutage(site.Location) ? "Running on a mobile generator: pressure normal."
                : wasDown ? "Mains power back: pressure restored." : "Pressure normal.", 0);
        }

        asset.BackupSince ??= context.SimTime;
        var standby = TimeSpan.FromHours(site.Quantity);
        var left = standby - (context.SimTime - asset.BackupSince.Value);
        if (left > TimeSpan.Zero)
            return (InfrastructureStatus.OnBackup, $"Mains power lost: on the standby generator, fuel for about {Math.Ceiling(left.TotalMinutes / 10) * 10:F0} min.", 0);
        if (asset.Status != InfrastructureStatus.Failed)
            context.EmitTruth(new CascadeOccurred($"Power outage: {site.Name} " + (site.Quantity > 0 ? "standby generator out of fuel" : "has no standby generator"),
                $"No mains water pressure within {site.ServiceRadiusMeters:F0} m: hydrants weak, hospitals short of water"));
        return (InfrastructureStatus.Failed, (site.Quantity > 0 ? "Standby generator out of fuel" : "No standby generator") +
                                             $": pumps stopped, no mains pressure within {site.ServiceRadiusMeters:F0} m.", Customers(site));
    }

    // ---- Gas ----

    private (InfrastructureStatus, string, int) Gas(SimulationContext context, WorldProtection.Asset asset)
    {
        var world = context.World;
        var site = asset.Site;
        var isolated = (InfrastructureStatus.Isolated,
            $"Isolated by Gas Networks Ireland: supply off within {site.ServiceRadiusMeters:F0} m until it is repaired and every meter relit.", Customers(site));
        if (asset.Status == InfrastructureStatus.Isolated) return isolated;
        if (asset.IsolationRequestedAt is { } asked)
        {
            // Told about the leak before being asked, the crew is already on its way.
            var told = world.Notified.Where(n => IsGasNetworks(n.Key)).Select(n => (DateTimeOffset?)n.Value).Min();
            if (context.SimTime >= asked + (told is { } t && t <= asked ? NotifiedIsolationTime : IsolationTime))
            {
                asset.LeakingSince = null;
                context.EmitTruth(new CascadeOccurred($"{site.Name} isolated", $"Gas off to about {Customers(site):N0} customers: no heating or cooking"));
                return isolated;
            }
        }
        if (asset.Status == InfrastructureStatus.Failed)
            return (asset.Status, asset.Detail, asset.Customers); // burning: nothing more to happen here
        if (!asset.Damaged)
            return (InfrastructureStatus.Normal, "Supply normal.", 0);

        asset.LeakingSince ??= context.SimTime;
        if (context.SimTime - asset.LeakingSince >= GasCallDelay && _gasCalled.Add(site.Id))
            CommsNet.EmergencyCall(context, new PendingCall
            {
                Caller = "999 caller (mobile)", Mobile = true, AccuracyMeters = 80,
                Location = GeoMath.Destination(site.Location, 60, 50),
                Summary = $"There's a really strong smell of gas {Near(site.Location, "on ", "here")}, you can hear it hissing",
            });

        if (Ignites(context, asset))
        {
            Ignite(context, asset);
            return (InfrastructureStatus.Failed, "Gas main ignited: burning at the break. Do not extinguish until isolated.", Customers(site));
        }
        return (InfrastructureStatus.Leaking, $"Gas escaping at {site.Name} ({asset.DamageCause}). Isolation needed.", 0);
    }

    /// <summary>Checked once a simulated minute: 3% near a fire, 0.3% anywhere else (a spark, a car, a cigarette).</summary>
    private bool Ignites(SimulationContext context, WorldProtection.Asset asset)
    {
        var minute = (int)(context.SimTime - asset.LeakingSince!.Value).TotalMinutes;
        if (_ignitionChecks.GetValueOrDefault(asset.Site.Id, -1) >= minute) return false;
        _ignitionChecks[asset.Site.Id] = minute;
        var nearFire = context.World.Hazards.Values.Any(h => !h.Ended && h.Model is FireSpreadModel fire && fire.IsActive && fire.DistanceToFire(asset.Site.Location) <= 150);
        return SimRandom.For(asset.Site.Id, 1300 + minute).NextDouble() < (nearFire ? 0.03 : 0.003);
    }

    private static void Ignite(SimulationContext context, WorldProtection.Asset asset)
    {
        var world = context.World;
        var site = asset.Site;
        asset.LeakingSince = null;
        context.EmitTruth(new GasIgnited(site.Id));
        var incident = world.Incidents.Values.Where(i => !i.Extinguished).MinBy(i => GeoMath.DistanceMeters(i.Location, site.Location))?.Id;

        // Whoever is close is burned.
        foreach (var person in world.Civilians.Where(p => p.State is not (CivilianState.Injured or CivilianState.Safe) && p.VehicleId is null
                                                          && GeoMath.DistanceMeters(p.Location, site.Location) <= 40).Take(4).ToList())
        {
            person.State = CivilianState.Injured;
            person.Target = null;
            var injured = new CasualtyInjured(Guid.NewGuid(), incident, person.Location, Triage.Urgent, "burns: gas explosion", person.Who);
            world.Casualties.TryAdd(injured.CasualtyId, new WorldCasualty
            {
                Id = injured.CasualtyId, IncidentId = incident, Location = person.Location, Triage = injured.Triage, State = CasualtyState.AwaitingTreatment,
                Cause = injured.Cause, InjuredAt = context.SimTime, CivilianId = person.Id,
            });
            context.EmitTruth(injured);
        }

        if (!world.Hazards.Values.Any(h => !h.Ended && h.Kind == HazardKind.Fire && h.Model is FireSpreadModel fire && fire.DistanceToFire(site.Location) <= 30))
            context.EmitTruth(new HazardStarted(Guid.NewGuid(), incident, HazardKind.Fire, site.Location, $"Gas fire at {site.Name}"));
        context.EmitTruth(new CascadeOccurred($"Gas leaking at {site.Name}", "Gas ignited: explosion and fire at the break"));
    }

    public static bool IsGasNetworks(string recipient) =>
        recipient.Contains("gas", StringComparison.OrdinalIgnoreCase) || recipient.Contains("GNI", StringComparison.Ordinal);

    // ---- Rail ----

    public static bool IsRailOperator(string recipient) =>
        new[] { "rail", "dart", "luas", "transport infrastructure", "tii", "iarnród" }.Any(w => recipient.Contains(w, StringComparison.OrdinalIgnoreCase));

    private (InfrastructureStatus, string, int) Rail(SimulationContext context, WorldProtection.Asset asset)
    {
        var world = context.World;
        var site = asset.Site;
        if (asset.Damaged)
            return (InfrastructureStatus.Failed, $"Line blocked at {site.Name} ({asset.DamageCause}): services stopped.", 0);
        if (world.InOutage(site.Location))
            return (InfrastructureStatus.Failed, "No traction power: trains stopped, passengers being walked off.", 0);
        var told = world.Notified.Where(n => IsRailOperator(n.Key)).Select(n => (DateTimeOffset?)n.Value).Min();
        if (told is { } t && context.SimTime >= t + RailStopTime)
            return (InfrastructureStatus.Suspended, $"Services suspended through {site.Name} at command's request.", 0);

        // Running: through whatever is around it.
        var where = Danger(world, site);
        if (where is not null)
        {
            if (!_railExposed.TryGetValue(site.Id, out var last) || context.SimTime - last >= TimeSpan.FromMinutes(15))
            {
                _railExposed[site.Id] = context.SimTime;
                context.EmitTruth(new RailServiceExposed(site.Id, where));
            }
            if (_railAsked.Add(site.Id))
            {
                var text = $"Irish Rail: our drivers are passing through {where} at {site.Name}. Services are still running: do you want them stopped?";
                CommsNet.Chat(context, "Irish Rail", text, new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, "Irish Rail", text,
                    Confidence.High, VerificationStatus.Confirmed, site.Location, site.ServiceRadiusMeters), TimeSpan.FromMinutes(3));
            }
        }
        return (InfrastructureStatus.Normal, where is null ? "Services running normally." : $"Services running through {where}.", 0);
    }

    /// <summary>What a running line passes through: smoke or a toxic cloud at moderate or above, or an evacuation zone.</summary>
    private static string? Danger(WorldState world, HazardSite site)
    {
        var exposures = HazardSystem.ExposuresAt(world, site.Location).Where(e => e.Exposure.Level >= HazardLevel.Moderate).ToList();
        if (exposures.Count > 0 && exposures.MaxBy(e => e.Exposure.Level) is var worst)
            return worst.Hazard.Kind switch
            {
                HazardKind.Fire => "heavy smoke",
                HazardKind.Plume => $"the {worst.Hazard.Substance ?? "toxic"} cloud",
                _ => "floodwater",
            };
        var point = site.Location.ToPoint();
        return world.ProtectiveActions.Values.Any(a => a.Type == ZoneType.EvacuationZone && a.Area.Contains(point)) ? "an evacuation zone" : null;
    }

    // ---- Reporting ----

    /// <summary>The operator tells command once a change has lasted <see cref="OperatorReportDelay"/>; nothing for assets that have only ever been normal.</summary>
    private static void Report(SimulationContext context, WorldProtection.Asset asset)
    {
        if (asset.Reported is null && asset.Status == InfrastructureStatus.Normal) return;
        if (asset.Reported?.Status == asset.Status) return;
        if (asset.ChangedAt is { } changed && context.SimTime - changed < OperatorReportDelay) return;

        asset.Reported = (asset.Status, asset.Detail);
        var site = asset.Site;
        var report = new InfrastructureStatusReported(site.Id, asset.Kind, site.Name, site.Location, site.ServiceRadiusMeters, asset.Status, asset.Detail, asset.Customers);
        // Power and the masts already have their own messages (InfrastructureSystem, CommsSystem): the board just gets the record.
        if (asset.Kind is InfrastructureKind.Power or InfrastructureKind.Telecom)
            context.EmitPerceived(report, EventSources.Comms);
        else
        {
            var from = Protection.Operator(asset.Kind);
            CommsNet.Chat(context, from, $"{from}: {site.Name} {Protection.Name(asset.Status)}. {asset.Detail}", report, TimeSpan.Zero);
        }
    }

    // ---- Hospitals ----

    /// <summary>Emergency capacity lost to no water pressure, and to ransomware (applied and taken off exactly).</summary>
    private static void Hospitals(SimulationContext context)
    {
        var world = context.World;
        foreach (var hospital in world.Hospitals.Values)
        {
            var water = world.Protection.LowWaterPressure(hospital.Location);
            var attack = world.Protection.Attacks.Values.FirstOrDefault(a => a is { Target: CyberTarget.Hospital, Active: true } && a.Started.HospitalId == hospital.Id);
            var factor = (water ? HospitalWaterFactor : 1) * (attack is null ? 1 : attack.FallbackAt is null ? HospitalCyberFactor : HospitalCyberFallbackFactor);
            var applied = world.Protection.HospitalFactors.GetValueOrDefault(hospital.Id, 1);
            if (Math.Abs(factor - applied) < 1e-9) continue;

            world.Protection.HospitalFactors[hospital.Id] = factor;
            hospital.Capacity = Math.Max(1, (int)Math.Round(hospital.Capacity / applied * factor));
            var reason = string.Join("; ", new[]
            {
                water ? "no mains water pressure" : null,
                attack is null ? null : attack.FallbackAt is null ? "IT systems down to ransomware" : "IT down, working on paper",
            }.OfType<string>().DefaultIfEmpty("water and IT restored"));
            context.EmitTruth(new HospitalCapacityChanged(hospital.Id, hospital.Capacity, reason));
            if (factor < applied)
                context.EmitTruth(new CascadeOccurred(reason, $"{hospital.Name}: emergency capacity down to {hospital.Capacity}"));
        }
    }

    private string Near(GeoPoint point, string prefix, string fallback) =>
        routing?.NearestRoad(point) is { } road && road.DistanceMeters < 150 ? prefix + road.Name : fallback;
}
