using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// Ground truth about population protection and critical systems (Phase 17): who was in each evacuation zone and where
/// they went, the routes, traffic control points and buses, the rest centres and who is in them, the state of the
/// infrastructure, GPS interference and ransomware, and the pollution an incident has caused. Discrete changes come from
/// the event stream; <c>EvacuationSystem</c>, <c>ShelterSystem</c>, <c>InfrastructureNetworkSystem</c>,
/// <c>CyberSystem</c> and <c>EnvironmentSystem</c> advance the rest.
/// </summary>
public sealed class WorldProtection
{
    /// <summary>Mobile generators take this long to arrive and connect once asked for.</summary>
    public static readonly TimeSpan GeneratorLeadTime = TimeSpan.FromMinutes(40);

    /// <summary>Traffic control points are staffed this long after the Garda are asked.</summary>
    public static readonly TimeSpan ControlPointLeadTime = TimeSpan.FromMinutes(10);

    /// <summary>Run-off containment (drain covers, booms) is in place this long after it is ordered.</summary>
    public static readonly TimeSpan ContainmentLeadTime = TimeSpan.FromMinutes(20);

    public sealed class Evacuation(Guid zoneId, Geometry area, DateTimeOffset orderedAt, Guid? incidentId, string name)
    {
        public Guid ZoneId { get; } = zoneId;
        public Geometry Area { get; } = area;
        public DateTimeOffset OrderedAt { get; } = orderedAt;

        /// <summary>The perceived incident the zone was declared for.</summary>
        public Guid? IncidentId { get; } = incidentId;

        public string Name { get; } = name;

        /// <summary>Everyone inside when the order came (counted on the first tick after it).</summary>
        public HashSet<int> Population { get; } = [];

        public bool Counted { get; set; }
        public List<GeoPoint> Route { get; } = [];
        public Guid? ShelterId { get; set; }
        public DateTimeOffset? ClearedAt { get; set; }

        /// <summary>When the zone was lifted (re-entry, authorised or not), and whether command authorised it.</summary>
        public DateTimeOffset? LiftedAt { get; set; }

        public bool ReEntryAuthorised { get; set; }

        /// <summary>Where evacuees from this zone make for: the end of the route, if there is one.</summary>
        public GeoPoint? Destination => Route.Count > 0 ? Route[^1] : null;
    }

    public sealed class ControlPoint(TrafficControlPointPlaced placed, DateTimeOffset at)
    {
        public Guid Id { get; } = placed.PointId;
        public GeoPoint Location { get; } = placed.Location;
        public DateTimeOffset PlacedAt { get; } = at;
        public bool Staffed(DateTimeOffset now) => now >= PlacedAt + ControlPointLeadTime;
    }

    public sealed class BusRequest(EvacuationBusesRequested requested, DateTimeOffset at)
    {
        public Guid Id { get; } = requested.RequestId;
        public Guid ZoneId { get; } = requested.ZoneId;
        public int Buses { get; } = requested.Buses;
        public DateTimeOffset RequestedAt { get; } = at;
        public bool Arrived { get; set; }

        /// <summary>When each bus is next free to load (back from its last run).</summary>
        public List<DateTimeOffset> FreeAt { get; } = [];

        public int Carried { get; set; }
    }

    public sealed class ShelterState(ShelterOpened opened, DateTimeOffset at)
    {
        public ShelterOpened Opened { get; } = opened;
        public Guid Id => Opened.ShelterId;
        public DateTimeOffset OpenedAt { get; } = at;
        public bool Ready(DateTimeOffset now) => now >= OpenedAt + Protection.ShelterSetup;

        public HashSet<int> Occupants { get; } = [];
        public HashSet<int> Registered { get; } = [];

        /// <summary>Occupants who have a bed.</summary>
        public HashSet<int> Bedded { get; } = [];

        /// <summary>People on their way here.</summary>
        public HashSet<int> Arriving { get; } = [];

        /// <summary>Everyone who has ever come (for the review).</summary>
        public HashSet<int> Arrivals { get; } = [];

        public HashSet<SupplyKind> Short { get; } = [];
        public DateTimeOffset? SecurityAt { get; set; }
        public bool Secured(DateTimeOffset now) => SecurityAt is { } at && now >= at + TimeSpan.FromMinutes(20);
        public int Incidents { get; set; }
        public int Expected => Occupants.Count + Arriving.Count;
    }

    public sealed class Asset(HazardSite site, InfrastructureKind kind)
    {
        public HazardSite Site { get; } = site;
        public InfrastructureKind Kind { get; } = kind;
        public InfrastructureStatus Status { get; set; }
        public string Detail { get; set; } = "";
        public int Customers { get; set; }

        /// <summary>Wrecked by a hazard: stays failed (or leaking) whatever the power does.</summary>
        public bool Damaged { get; set; }

        public string? DamageCause { get; set; }
        public DateTimeOffset? BackupSince { get; set; }
        public DateTimeOffset? ChangedAt { get; set; }
        public DateTimeOffset? GeneratorRequestedAt { get; set; }
        public DateTimeOffset? IsolationRequestedAt { get; set; }
        public DateTimeOffset? LeakingSince { get; set; }

        /// <summary>What its operator last told command (so it reports again only when that is out of date).</summary>
        public (InfrastructureStatus Status, string Detail)? Reported { get; set; }

        public bool Generator(DateTimeOffset now) => GeneratorRequestedAt is { } at && now >= at + GeneratorLeadTime;
        public bool Covers(GeoPoint point) => GeoMath.DistanceMeters(Site.Location, point) <= Site.ServiceRadiusMeters;
    }

    public sealed class Attack(CyberAttackStarted started, DateTimeOffset at)
    {
        public CyberAttackStarted Started { get; } = started;
        public CyberTarget Target => Started.Target;
        public DateTimeOffset StartedAt { get; } = at;
        public DateTimeOffset? FallbackAt { get; set; }
        public DateTimeOffset? RestoredAt { get; set; }
        public bool Reported { get; set; }
        public bool WaterUnsafe { get; set; }
        public bool Active => RestoredAt is null;
    }

    public sealed class Monitor(EnvironmentalMonitoringDeployed deployed, DateTimeOffset at)
    {
        public EnvironmentalMonitoringDeployed Deployed { get; } = deployed;
        public DateTimeOffset DeployedAt { get; } = at;
        public DateTimeOffset? LastReadingAt { get; set; }
    }

    /// <summary>An incident's pollution so far.</summary>
    public sealed class Pollution
    {
        public required string Watercourse { get; init; }
        public required GeoPoint WatercoursePoint { get; init; }
        public double Runoff { get; set; }
        public double ToWater { get; set; }
        public double Contained { get; set; }
        public double Soaked { get; set; }
        public int Fish { get; set; }
        public int Birds { get; set; }

        /// <summary>Contaminated ground: circles, by centre and radius.</summary>
        public List<(GeoPoint Centre, double RadiusMeters)> Ground { get; } = [];

        public double GroundSquareMeters => Ground.Sum(g => Math.PI * g.RadiusMeters * g.RadiusMeters);
    }

    public Dictionary<Guid, Evacuation> Evacuations { get; } = [];
    public Dictionary<Guid, ControlPoint> ControlPoints { get; } = [];
    public List<BusRequest> Buses { get; } = [];
    public Dictionary<Guid, ShelterState> Shelters { get; } = [];
    public Dictionary<Guid, Asset> Assets { get; } = [];

    /// <summary>Sites damaged by a hazard or an inject, and how (the infrastructure system marks its asset).</summary>
    public Dictionary<Guid, string> Damage { get; } = [];

    /// <summary>GPS interference going on now.</summary>
    public Dictionary<Guid, GnssInterferenceStarted> Gnss { get; } = [];

    /// <summary>When each began.</summary>
    public Dictionary<Guid, DateTimeOffset> GnssSince { get; } = [];

    public bool ManualPositions { get; private set; }
    public Dictionary<Guid, Attack> Attacks { get; } = [];
    public Dictionary<Guid, Monitor> Monitors { get; } = [];

    /// <summary>Perceived incidents whose run-off command ordered contained, and when.</summary>
    public Dictionary<Guid, DateTimeOffset> Containment { get; } = [];

    /// <summary>Pollution by world incident (<see cref="Guid.Empty"/> for a hazard with none).</summary>
    public Dictionary<Guid, Pollution> Pollutions { get; } = [];

    /// <summary>Hospital capacity factors applied for water and IT (so they can be taken off again exactly).</summary>
    public Dictionary<Guid, double> HospitalFactors { get; } = [];

    /// <summary>Areas where tap water can't be trusted (a treatment works' controls lost), from the event stream.</summary>
    public List<(GeoPoint Centre, double RadiusMeters)> UnsafeWater { get; } = [];

    // ---- Questions other systems ask ----

    /// <summary>The interference over a point, if any (jamming first: nothing gets through it).</summary>
    public GnssInterferenceStarted? GnssAt(GeoPoint point) =>
        Gnss.Values.Where(g => GeoMath.DistanceMeters(g.Centre, point) <= g.RadiusMeters).OrderBy(g => g.Kind).FirstOrDefault();

    public Attack? ActiveAttack(CyberTarget target) => Attacks.Values.FirstOrDefault(a => a.Target == target && a.Active);

    /// <summary>The control room's mobile data is down to ransomware.</summary>
    public bool DispatchDown => ActiveAttack(CyberTarget.Dispatch) is not null;

    /// <summary>And command has told crews to go by voice.</summary>
    public bool DispatchFallback => ActiveAttack(CyberTarget.Dispatch)?.FallbackAt is not null;

    /// <summary>No mains water pressure here: a pumping station serving it has failed.</summary>
    public bool LowWaterPressure(GeoPoint point) =>
        Assets.Values.Any(a => a.Kind == InfrastructureKind.Water && a.Status == InfrastructureStatus.Failed && a.Covers(point));

    /// <summary>The ground here is contaminated.</summary>
    public bool Contaminated(GeoPoint point) =>
        Pollutions.Values.Any(p => p.Ground.Any(g => GeoMath.DistanceMeters(g.Centre, point) <= g.RadiusMeters));

    public bool HasGenerator(Guid siteId, DateTimeOffset now) => Assets.TryGetValue(siteId, out var asset) && asset.Generator(now);

    public ShelterState? ShelterOf(Civilian person) =>
        person.ShelterId is { } id && Shelters.TryGetValue(id, out var shelter) ? shelter : null;

    public void Apply(DomainEvent payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case ZoneDeclared { Type: ZoneType.EvacuationZone } e when e.Boundary.Count >= 3:
                Evacuations[e.ZoneId] = new Evacuation(e.ZoneId, Wgs84.CreatePolygon(e.Boundary), at, e.IncidentId, e.Name);
                break;
            case ZoneLifted e when Evacuations.TryGetValue(e.ZoneId, out var lifted):
                lifted.LiftedAt ??= at;
                break;
            case ReEntryAuthorised e when Evacuations.TryGetValue(e.ZoneId, out var reEntry):
                reEntry.ReEntryAuthorised = true;
                break;
            case EvacuationRouteSet e when Evacuations.TryGetValue(e.ZoneId, out var routed):
                routed.Route.Clear();
                routed.Route.AddRange(e.Route);
                routed.ShelterId = e.ShelterId;
                break;
            case TrafficControlPointPlaced e:
                ControlPoints.TryAdd(e.PointId, new ControlPoint(e, at));
                break;
            case EvacuationBusesRequested e:
                Buses.Add(new BusRequest(e, at));
                break;

            case ShelterOpened e:
                Shelters.TryAdd(e.ShelterId, new ShelterState(e, at));
                break;
            case ShelterSecurityArranged e when Shelters.TryGetValue(e.ShelterId, out var secured):
                secured.SecurityAt ??= at;
                break;

            case InfrastructureDamaged e:
                Damage.TryAdd(e.SiteId, e.Cause);
                break;
            case GeneratorRequested e when Assets.TryGetValue(e.SiteId, out var powered):
                powered.GeneratorRequestedAt ??= at;
                break;
            case GasIsolationRequested e when Assets.TryGetValue(e.SiteId, out var isolating):
                isolating.IsolationRequestedAt ??= at;
                break;

            case GnssInterferenceStarted e:
                Gnss[e.InterferenceId] = e;
                GnssSince[e.InterferenceId] = at;
                break;
            case GnssInterferenceEnded e:
                Gnss.Remove(e.InterferenceId);
                break;
            case ManualPositionReportingSet e:
                ManualPositions = e.Enabled;
                break;

            case CyberAttackStarted e:
                Attacks.TryAdd(e.AttackId, new Attack(e, at));
                break;
            case CyberFallbackActivated e when Attacks.TryGetValue(e.AttackId, out var fallback):
                fallback.FallbackAt ??= at;
                break;
            case CyberSystemsRestored e when Attacks.TryGetValue(e.AttackId, out var restored):
                restored.RestoredAt ??= at;
                break;
            case DrinkingWaterUnsafe e:
                UnsafeWater.Add((e.Location, e.RadiusMeters));
                if (Attacks.TryGetValue(e.AttackId, out var poisoned)) poisoned.WaterUnsafe = true;
                break;

            case EnvironmentalMonitoringDeployed e:
                Monitors.TryAdd(e.MonitorId, new Monitor(e, at));
                break;
            case RunoffContainmentOrdered e:
                Containment.TryAdd(e.IncidentId, at);
                break;
        }
    }
}
