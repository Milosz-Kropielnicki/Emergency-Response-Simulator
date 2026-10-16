using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Evacuation modelling (Phase 17): the people in each evacuation zone, how they get out, and how they get back.
/// <list type="bullet">
/// <item><b>The population</b>: everyone in a zone when it is ordered. The gardaí report an estimate, how many are out
/// and still in, and when it will be clear at the present rate, every <see cref="ReportInterval"/>.</item>
/// <item><b>Routes</b>: with a route set, evacuees make for its end (<c>CivilianSystem</c>); without, straight out from
/// the middle of the zone, whatever lies that way.</item>
/// <item><b>Buses</b>: arrive <see cref="BusLeadTime"/> after they are asked for and take people leaving on foot,
/// <see cref="Protection.BusSeats"/> a run, to the route's end (or its rest centre). Each run takes the drive there and
/// back at <see cref="BusKph"/> plus loading. The buses are on the roads with everyone else.</item>
/// <item><b>Traffic control points</b> are staffed <see cref="WorldProtection.ControlPointLeadTime"/> after they are
/// asked for; <c>TrafficSystem</c> moves traffic through them.</item>
/// <item><b>Re-entry</b>: once a zone is lifted its evacuees go home over the next one to one and a half hours, and
/// leave the rest centres. Going home to a hazard still there, or to contaminated ground, is recorded.</item>
/// </list>
/// </summary>
public sealed class EvacuationSystem : ISimulationSystem
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OutcomeInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BusLeadTime = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan BusLoading = TimeSpan.FromMinutes(8);
    public const double BusKph = 25;

    /// <summary>A zone is clear once this share of the people willing to leave are out.</summary>
    public const double ClearedShare = 0.9;

    private readonly Dictionary<Guid, DateTimeOffset> _reported = [];
    private readonly Dictionary<Guid, DateTimeOffset> _outcome = [];
    private readonly Dictionary<Guid, List<(DateTimeOffset At, int Out)>> _history = [];
    private readonly HashSet<Guid> _staffed = [];
    private readonly Dictionary<(Guid Zone, string Reason), (int Count, int Reported, DateTimeOffset At)> _unsafe = [];

    public int Order => 16; // after the civilians have moved (15)

    public void Update(SimulationContext context)
    {
        var world = context.World;
        foreach (var evacuation in world.Protection.Evacuations.Values)
        {
            Count(world, evacuation);
            if (evacuation.LiftedAt is null)
            {
                Track(context, evacuation);
                Buses(context, evacuation);
            }
            else ReEnter(context, evacuation);
        }
        ControlPoints(context);
        ReportUnsafe(context);
    }

    /// <summary>Everyone inside when the order came: those not already hurt or out of the area.</summary>
    private static void Count(WorldState world, WorldProtection.Evacuation evacuation)
    {
        if (evacuation.Counted) return;
        evacuation.Counted = true;
        foreach (var person in world.Civilians.Where(p => p.State is not (CivilianState.Injured or CivilianState.Safe) && evacuation.Area.Contains(p.Location.ToPoint())))
            evacuation.Population.Add(person.Id);
    }

    private static bool Inside(WorldProtection.Evacuation evacuation, Civilian person) => evacuation.Area.Contains(person.Location.ToPoint());

    /// <summary>How the zone really stands: out, still in, hurt, and those who won't go.</summary>
    private static (int Out, int Inside, int Hurt, int Refused) Tally(WorldState world, WorldProtection.Evacuation evacuation)
    {
        int @out = 0, inside = 0, hurt = 0, refused = 0;
        foreach (var person in world.Civilians.Where(p => evacuation.Population.Contains(p.Id)))
        {
            if (person.State == CivilianState.Injured) hurt++;
            else if (Inside(evacuation, person))
            {
                inside++;
                if (person.IgnoredOrder == evacuation.ZoneId) refused++;
            }
            else @out++;
        }
        return (@out, inside, hurt, refused);
    }

    private void Track(SimulationContext context, WorldProtection.Evacuation evacuation)
    {
        var world = context.World;
        foreach (var person in world.Civilians.Where(p => p.EvacuatedFrom is null && p.State != CivilianState.Injured && evacuation.Population.Contains(p.Id)
                                                          && !Inside(evacuation, p)))
            person.EvacuatedFrom = evacuation.ZoneId;

        var (@out, inside, hurt, refused) = Tally(world, evacuation);
        var willing = evacuation.Population.Count - refused - hurt;
        if (evacuation.ClearedAt is null && evacuation.Population.Count > 0 && @out >= Math.Ceiling(willing * ClearedShare))
            evacuation.ClearedAt = context.SimTime;

        if (!_history.TryGetValue(evacuation.ZoneId, out var history)) _history[evacuation.ZoneId] = history = [];
        if (history.Count == 0 || context.SimTime - history[^1].At >= TimeSpan.FromMinutes(1)) history.Add((context.SimTime, @out));
        history.RemoveAll(h => context.SimTime - h.At > TimeSpan.FromMinutes(11));

        if (context.SimTime - evacuation.OrderedAt >= ReportInterval
            && (!_reported.TryGetValue(evacuation.ZoneId, out var last) || context.SimTime - last >= ReportInterval))
        {
            _reported[evacuation.ZoneId] = context.SimTime;
            Report(context, evacuation, @out, inside, history);
        }

        if (!_outcome.TryGetValue(evacuation.ZoneId, out var lastOutcome) || context.SimTime - lastOutcome >= OutcomeInterval)
        {
            _outcome[evacuation.ZoneId] = context.SimTime;
            Outcome(context, evacuation);
        }
    }

    /// <summary>
    /// The gardaí's estimate: the zone's population from the census (out by up to an eighth), the people they have seen
    /// leave and still inside to the nearest five, and the time to clear at the rate of the last ten minutes.
    /// </summary>
    private static void Report(SimulationContext context, WorldProtection.Evacuation evacuation, int @out, int inside, List<(DateTimeOffset At, int Out)> history)
    {
        var bias = 1 + (SimRandom.For(evacuation.ZoneId, 171).NextDouble() - 0.5) / 4;
        var estimated = (int)Math.Round(evacuation.Population.Count * bias / 10) * 10;
        var left = (int)Math.Round(@out / 5.0) * 5;
        var remaining = (int)Math.Round(inside / 5.0) * 5;
        int? clearance = null;
        if (history.Count >= 2 && history[^1].Out > history[0].Out)
        {
            var perMinute = (history[^1].Out - history[0].Out) / Math.Max(1, (history[^1].At - history[0].At).TotalMinutes);
            clearance = (int)Math.Ceiling(inside / perMinute);
        }
        else if (evacuation.ClearedAt is not null || inside == 0) clearance = 0;
        var text = $"Garda: evacuation of {evacuation.Name}, about {left} out, {remaining} still in" +
                   (clearance is { } c ? c == 0 ? ", effectively clear." : $", clear in about {Protection.Clearance(c)} at this rate." : ", nobody moving.");
        CommsNet.Chat(context, "Garda Síochána", text, new EvacuationProgressReported(evacuation.ZoneId, Math.Max(estimated, left + remaining), left, remaining, clearance),
            TimeSpan.Zero);
    }

    private static void Outcome(SimulationContext context, WorldProtection.Evacuation evacuation)
    {
        var (@out, inside, hurt, _) = Tally(context.World, evacuation);
        context.EmitTruth(new EvacuationOutcome(evacuation.ZoneId, evacuation.Population.Count, @out, inside, hurt,
            evacuation.ClearedAt is { } cleared ? Math.Round((cleared - evacuation.OrderedAt).TotalMinutes, 1) : null));
    }

    // ---- Buses ----

    private static void Buses(SimulationContext context, WorldProtection.Evacuation evacuation)
    {
        var world = context.World;
        foreach (var request in world.Protection.Buses.Where(b => b.ZoneId == evacuation.ZoneId))
        {
            if (!request.Arrived)
            {
                if (context.SimTime < request.RequestedAt + BusLeadTime) continue;
                request.Arrived = true;
                request.FreeAt.AddRange(Enumerable.Repeat(context.SimTime, request.Buses));
                CommsNet.Chat(context, "Dublin Bus", $"Dublin Bus: {request.Buses} bus(es) at {evacuation.Name}, loading.",
                    new EvacuationBusesArrived(request.Id, evacuation.ZoneId, request.Buses), TimeSpan.Zero);
            }

            for (var bus = 0; bus < request.FreeAt.Count; bus++)
            {
                if (context.SimTime < request.FreeAt[bus]) continue;
                // People leaving on foot (not running from the fire: they won't stop for a bus), still in the zone or with a
                // long walk ahead to where the route ends, nearest the pick-up first.
                var pickup = evacuation.Route.Count > 0 && evacuation.Area.Contains(evacuation.Route[0].ToPoint())
                    ? evacuation.Route[0]
                    : GeoPoint.FromPoint(evacuation.Area.Centroid);
                var end = evacuation.Destination;
                var load = world.Civilians
                    .Where(p => p is { State: CivilianState.Evacuating, VehicleId: null, Panicking: false }
                                && (Inside(evacuation, p) || p.EvacuatedFrom == evacuation.ZoneId && end is { } far && GeoMath.DistanceMeters(p.Location, far) > 400))
                    .OrderBy(p => GeoMath.DistanceMeters(p.Location, pickup))
                    .Take(Protection.BusSeats)
                    .ToList();
                if (load.Count == 0) break;

                pickup = new GeoPoint(load.Average(p => p.Location.Latitude), load.Average(p => p.Location.Longitude));
                var destination = end ?? JustOutside(evacuation, load[0].Location);
                var vehicle = new WorldVehicle { Id = Guid.NewGuid(), Location = pickup, Destination = destination, Occupants = [.. load.Select(p => p.Id)] };
                world.Vehicles[vehicle.Id] = vehicle;
                foreach (var person in load)
                {
                    person.VehicleId = vehicle.Id;
                    person.Target = destination;
                    person.Location = pickup;
                    // To the rest centre at the route's end: they go in.
                    if (evacuation.ShelterId is { } shelter)
                    {
                        person.NeedsShelter = true;
                        person.ShelterId = shelter;
                    }
                }
                request.Carried += load.Count;
                var hours = GeoMath.DistanceMeters(pickup, destination) * 1.3 * 2 / 1000 / BusKph;
                request.FreeAt[bus] = context.SimTime + BusLoading + TimeSpan.FromHours(hours);
            }
        }
    }

    /// <summary>A drop-off point 300 m beyond the zone's edge, on the side the person is.</summary>
    private static GeoPoint JustOutside(WorldProtection.Evacuation evacuation, GeoPoint from)
    {
        var centre = GeoPoint.FromPoint(evacuation.Area.Centroid);
        var radius = evacuation.Area.Coordinates.Max(c => GeoMath.DistanceMeters(centre, new GeoPoint(c.Y, c.X)));
        var bearing = GeoMath.DistanceMeters(centre, from) < 1 ? 0 : GeoMath.BearingDegrees(centre, from);
        return GeoMath.Destination(centre, bearing, radius + 300);
    }

    // ---- Traffic control points ----

    private void ControlPoints(SimulationContext context)
    {
        foreach (var point in context.World.Protection.ControlPoints.Values)
        {
            if (!point.Staffed(context.SimTime) || !_staffed.Add(point.Id)) continue;
            CommsNet.Chat(context, "Garda Traffic Corps", "Garda Traffic Corps: members in place at the traffic control point.",
                new TrafficControlPointStaffed(point.Id), TimeSpan.Zero);
        }
    }

    // ---- Re-entry ----

    /// <summary>
    /// The zone is lifted: its evacuees go home, each 15–90 minutes later, leaving the rest centre they were in. Anyone
    /// who gets home to a hazard still there, or contaminated ground, is recorded.
    /// </summary>
    private void ReEnter(SimulationContext context, WorldProtection.Evacuation evacuation)
    {
        var world = context.World;
        if (_outcome.TryGetValue(evacuation.ZoneId, out var last) && last < evacuation.LiftedAt!.Value)
        {
            _outcome[evacuation.ZoneId] = context.SimTime;
            Outcome(context, evacuation); // where it stood when lifted
        }

        foreach (var person in world.Civilians.Where(p => p.EvacuatedFrom == evacuation.ZoneId && p.ReturnAt != DateTimeOffset.MaxValue))
        {
            if (person.State == CivilianState.Injured || person.VehicleId is not null) continue;
            person.ReturnAt ??= evacuation.LiftedAt!.Value + TimeSpan.FromMinutes(15 + SimRandom.For(evacuation.ZoneId, person.Id).NextDouble() * 75);
            if (context.SimTime < person.ReturnAt) continue;

            person.ReturnAt = DateTimeOffset.MaxValue; // home, once
            if (world.Protection.ShelterOf(person) is { } shelter)
            {
                shelter.Occupants.Remove(person.Id);
                shelter.Arriving.Remove(person.Id);
            }
            (person.ShelterId, person.ShelterArrivalAt, person.EvacuatedFrom) = (null, null, null);
            (person.Location, person.State, person.Target, person.Panicking) = (person.Home, CivilianState.Normal, null, false);
            (person.WarnedAt, person.IgnoredOrder, person.AwareSince) = (null, null, null);

            var reason = HazardSystem.ExposuresAt(world, person.Home).Any(e => e.Exposure.Level >= HazardLevel.Low) ? "the hazard is still there"
                : world.Protection.Contaminated(person.Home) ? "the ground there is contaminated"
                : null;
            if (reason is null) continue;
            var key = (evacuation.ZoneId, reason);
            var tally = _unsafe.GetValueOrDefault(key);
            _unsafe[key] = tally with { Count = tally.Count + 1 };
        }
    }

    /// <summary>Unsafe homecomings, recorded as they mount up (at most every ten minutes per zone and reason).</summary>
    private void ReportUnsafe(SimulationContext context)
    {
        foreach (var ((zone, reason), (count, reported, at)) in _unsafe.ToList())
        {
            if (count == reported || reported > 0 && context.SimTime - at < TimeSpan.FromMinutes(10)) continue;
            _unsafe[(zone, reason)] = (count, count, context.SimTime);
            context.EmitTruth(new UnsafeReEntry(zone, count, reason));
        }
    }
}
