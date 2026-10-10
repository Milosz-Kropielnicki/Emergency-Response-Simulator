using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The bridge from truth to the COP for hazards (Design Document §10.1, §11): crews report what they can see, with
/// the errors people make estimating it, and only if their radio works.
/// <list type="bullet">
/// <item>The first fire crew working near a fire sends a size-up (area involved, which way it is spreading, anything
/// at risk next to it), then a progress report every five minutes, and "fire out" at the end.</item>
/// <item>A crew sent to the wrong place says so on arrival, and which way the smoke is.</item>
/// <item>Any crew driving or working inside a toxic cloud reports the smell, then the symptoms.</item>
/// <item>Crews working near floodwater report the depth and whether it is rising.</item>
/// </list>
/// </summary>
public sealed class HazardReportingSystem(IRoutingService? routing = null) : ISimulationSystem
{
    public static readonly TimeSpan FireReportInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PlumeReportInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FloodReportInterval = TimeSpan.FromMinutes(10);

    /// <summary>A crew can see the fire it is fighting from this far away.</summary>
    public const double FireSightMeters = 200;

    /// <summary>Smoke from a working fire is visible from this far.</summary>
    public const double SmokeSightMeters = 1500;

    private sealed class FireWatch
    {
        public DateTimeOffset? LastAt;
        public double LastArea;
        public double PeakArea;
        public bool Controlled;
        public bool Out;
    }

    private readonly Dictionary<Guid, FireWatch> _fires = [];
    private readonly HashSet<(Guid Hazard, Guid Site)> _exposuresWarned = [];
    private readonly Dictionary<Guid, ResponsePhase> _lastPhase = [];
    private readonly HashSet<(Guid Unit, Guid Destination)> _arrivalsChecked = [];
    private readonly Dictionary<(Guid Unit, Guid Hazard), (DateTimeOffset At, HazardLevel Level)> _plumeReports = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, double Depth)> _floodReports = [];

    public int Order => 30; // after the world has moved this tick

    public void Update(SimulationContext context)
    {
        Arrivals(context);
        Fires(context);
        Plumes(context);
        Floods(context);
    }

    private static bool IsFireCrew(UnitType type) => HazardSystem.SuppressionRate(type) > 0;

    // ---- Wrong place ----

    /// <summary>A crew arriving where nothing is happening says so, and which way the smoke is.</summary>
    private void Arrivals(SimulationContext context)
    {
        var world = context.World;
        foreach (var unit in world.Units.Values)
        {
            var arrived = unit.Phase == ResponsePhase.OnScene && _lastPhase.GetValueOrDefault(unit.Id) != ResponsePhase.OnScene;
            _lastPhase[unit.Id] = unit.Phase;
            if (!arrived || unit.RadioFailed || unit.OrderedIncidentId is not { } destination) continue;
            if (world.AgencyTasks.ContainsKey(destination) || !_arrivalsChecked.Add((unit.Id, destination))) continue;

            var nearestIncident = world.Incidents.Values.Where(i => !i.Extinguished)
                .MinBy(i => GeoMath.DistanceMeters(i.Location, unit.Location));
            var nearestHazard = world.Hazards.Values.Where(h => !h.Ended && h.Model is { IsActive: true })
                .Select(h => (Hazard: h, Distance: Distance(h, unit.Location)))
                .MinBy(x => x.Distance);
            var incidentDistance = nearestIncident is null ? double.PositiveInfinity : GeoMath.DistanceMeters(nearestIncident.Location, unit.Location);
            if (incidentDistance <= 250 || nearestHazard.Distance <= 250) continue;

            string claim;
            if (nearestHazard.Hazard is { Kind: HazardKind.Fire } fire && nearestHazard.Distance <= SmokeSightMeters)
            {
                var bearing = GeoMath.BearingDegrees(unit.Location, fire.Origin);
                claim = $"{unit.Callsign} at the given location: nothing showing here. Heavy smoke visible to the " +
                        $"{GeoMath.CompassPoint(bearing)}, roughly {Round(GeoMath.DistanceMeters(unit.Location, fire.Origin), 50):F0} m away.";
            }
            else
            {
                claim = $"{unit.Callsign} at the given location: nothing showing here, no sign of any incident. Request an updated location.";
            }
            Report(context, unit, claim, Confidence.High, VerificationStatus.Confirmed);
        }
    }

    private static double Distance(WorldHazard hazard, GeoPoint point) => hazard.Model switch
    {
        FireSpreadModel fire => fire.DistanceToFire(point),
        _ => GeoMath.DistanceMeters(hazard.Origin, point),
    };

    // ---- Fire ----

    private void Fires(SimulationContext context)
    {
        var world = context.World;
        foreach (var hazard in world.Hazards.Values.Where(h => h.Kind == HazardKind.Fire && h.Model is FireSpreadModel))
        {
            var fire = (FireSpreadModel)hazard.Model!;
            if (!_fires.TryGetValue(hazard.Id, out var watch))
                _fires[hazard.Id] = watch = new FireWatch();
            if (watch.Out) continue;

            var crew = world.Units.Values
                .Where(u => IsFireCrew(u.Type) && u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating && !u.RadioFailed
                            && (hazard.Ended || fire.DistanceToFire(u.Location) <= FireSightMeters
                                || GeoMath.DistanceMeters(u.Location, hazard.Origin) <= FireSightMeters))
                .OrderBy(u => u.PhaseStartedAt)
                .FirstOrDefault();
            if (crew is null) continue;

            var burning = fire.BurningAreaSquareMeters;
            watch.PeakArea = Math.Max(watch.PeakArea, burning);

            if (hazard.Ended || !fire.IsActive)
            {
                if (watch.LastAt is null) continue; // nobody reported it burning; nothing to close off
                watch.Out = true;
                Report(context, crew, $"{crew.Callsign}: fire is out. Damping down and checking for hot spots.",
                    Confidence.High, VerificationStatus.Confirmed);
                continue;
            }

            if (watch.LastAt is { } last && context.SimTime - last < FireReportInterval) continue;

            // Crews misjudge size consistently (one crew always high, another low) plus a little each time,
            // so successive reports are wrong but tell a coherent story.
            var bias = 1 + Gaussian(SimRandom.For(hazard.Id, crew.Id.GetHashCode())) * 0.2;
            var random = SimRandom.For(hazard.Id, (int)(context.SimTime.ToUnixTimeSeconds() / 60));
            var estimate = Round(burning * Math.Max(0.5, bias) * (1 + Gaussian(random) * 0.05), 50);
            var spreading = fire.SpreadDirection() is { } direction ? $", spreading {direction}" : "";

            string claim;
            if (watch.LastAt is null)
            {
                claim = $"{crew.Callsign} size-up: well-developed fire, about {estimate:N0} m² involved{spreading}.";
            }
            else
            {
                var trend = burning > watch.LastArea * 1.15 ? "growing" : burning < watch.LastArea * 0.85 ? "reducing" : "holding";
                var controlled = !watch.Controlled && burning < watch.PeakArea * 0.25 && trend == "reducing";
                if (controlled) watch.Controlled = true;
                claim = controlled
                    ? $"{crew.Callsign}: fire under control, about {estimate:N0} m² still burning."
                    : $"{crew.Callsign}: fire {trend}, about {estimate:N0} m² involved{spreading}.";
            }

            claim += Exposures(world, hazard, fire, random);
            watch.LastAt = context.SimTime;
            watch.LastArea = burning;
            Report(context, crew, claim, Confidence.Medium, VerificationStatus.Reported);
        }
    }

    /// <summary>Anything dangerous right next to the fire that the crew can see.</summary>
    private string Exposures(WorldState world, WorldHazard hazard, FireSpreadModel fire, Random random)
    {
        var warnings = new List<string>();
        foreach (var site in world.Sites.Values.Where(s => !s.Triggered && fire.DistanceToFire(s.Location) <= 60))
        {
            if (!_exposuresWarned.Add((hazard.Id, site.Id))) continue;
            warnings.Add(site.Kind switch
            {
                HazardSiteKind.ChemicalStore => random.NextDouble() < 0.7 && site.Substance is { } substance
                    ? $"Exposure at risk: hazard placards for {substance} on {site.Name}, fire is about to reach it"
                    : $"Exposure at risk: {site.Name}, contents unknown, fire is about to reach it",
                _ => $"Exposure at risk: electricity substation ({site.Name}) right beside the fire",
            });
        }
        return warnings.Count == 0 ? "" : " " + string.Join(". ", warnings) + ".";
    }

    // ---- Toxic cloud ----

    private void Plumes(SimulationContext context)
    {
        var world = context.World;
        foreach (var hazard in world.Hazards.Values.Where(h => h.Kind == HazardKind.Plume && !h.Ended && h.Model is PlumeModel))
        {
            var plume = (PlumeModel)hazard.Model!;
            foreach (var unit in world.Units.Values.Where(u => !u.RadioFailed))
            {
                var exposure = plume.ExposureAt(unit.Location);
                if (exposure.Level == HazardLevel.None) continue;

                var key = (unit.Id, hazard.Id);
                var previous = _plumeReports.GetValueOrDefault(key);
                if (previous.At != default && exposure.Level <= previous.Level && context.SimTime - previous.At < PlumeReportInterval) continue;
                _plumeReports[key] = (context.SimTime, exposure.Level);

                var where = Near(unit.Location);
                // Crews don't know what it is from the smell alone.
                var smell = plume.Substance == Substance.Chlorine ? "a strong bleach-like smell, possibly chlorine" : "a strong chemical smell";
                var claim = exposure.Level switch
                {
                    HazardLevel.High => $"{unit.Callsign}: crew overcome by fumes{where}, coughing and struggling to breathe. Withdrawing now.",
                    HazardLevel.Moderate => $"{unit.Callsign}: crew have stinging eyes and throats{where}. Withdrawing upwind.",
                    _ => $"{unit.Callsign}: {smell}{where}.",
                };
                Report(context, unit, claim, exposure.Level == HazardLevel.Low ? Confidence.Medium : Confidence.High,
                    VerificationStatus.Reported);
            }
        }
    }

    // ---- Flood ----

    private void Floods(SimulationContext context)
    {
        var world = context.World;
        foreach (var hazard in world.Hazards.Values.Where(h => h.Kind == HazardKind.Flood && !h.Ended && h.Model is FloodModel))
        {
            var flood = (FloodModel)hazard.Model!;
            var crew = world.Units.Values
                .Where(u => u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating && !u.RadioFailed
                            && GeoMath.DistanceMeters(u.Location, hazard.Origin) <= 600)
                .OrderBy(u => u.PhaseStartedAt)
                .FirstOrDefault();
            if (crew is null) continue;

            var previous = _floodReports.GetValueOrDefault(hazard.Id);
            if (previous.At != default && context.SimTime - previous.At < FloodReportInterval) continue;

            var depth = flood.MaxDepth;
            if (depth < 0.1) continue;
            _floodReports[hazard.Id] = (context.SimTime, depth);

            var trend = previous.At == default ? "" : depth > previous.Depth * 1.1 ? ", still rising" : depth < previous.Depth * 0.9 ? ", falling" : ", steady";
            var area = Round(flood.AreaSquareMeters / 10_000, 0.5);
            Report(context, crew,
                $"{crew.Callsign}: flooding{Near(hazard.Origin)}, water up to about {Round(depth * 100, 10):F0} cm{trend}. " +
                $"Roughly {area:0.#} hectares under water.",
                Confidence.Medium, VerificationStatus.Reported);
        }
    }

    // ---- Helpers ----

    private void Report(SimulationContext context, WorldUnit unit, string claim, Confidence confidence, VerificationStatus verification) =>
        context.EmitPerceived(new ReportReceived(Guid.NewGuid(), IncidentFor(context.World, unit), ReportSource.FieldUnit, unit.Callsign,
            claim, confidence, verification, unit.Location, 30, unit.Id), EventSources.Comms);

    /// <summary>The COP incident the unit is assigned to (not an AI agency's own job).</summary>
    private static Guid? IncidentFor(WorldState world, WorldUnit unit) =>
        unit.OrderedIncidentId is { } id && !world.AgencyTasks.ContainsKey(id) ? id : null;

    private string Near(GeoPoint point) =>
        routing?.NearestRoad(point) is { } road && road.DistanceMeters < 80 ? $" on {road.Name}" : "";

    private static double Round(double value, double step) => Math.Max(step, Math.Round(value / step) * step);

    private static double Gaussian(Random random)
    {
        var u1 = 1 - random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * random.NextDouble());
    }
}
