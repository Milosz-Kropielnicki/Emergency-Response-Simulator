using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Hazards;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// An immutable copy of ground truth taken after each engine step, for the instructor's views (Design Document
/// §10.1, §21). <see cref="WorldState"/> itself belongs to the engine thread; the UI reads this instead.
/// Never shown to the trainee.
/// </summary>
public sealed record WorldSnapshot(
    DateTimeOffset At,
    WorldWeather Weather,
    IReadOnlyList<WorldSnapshot.IncidentView> Incidents,
    IReadOnlyList<WorldSnapshot.HazardView> Hazards,
    IReadOnlyList<WorldSnapshot.SiteView> Sites,
    IReadOnlyList<WorldSnapshot.CivilianView> Civilians,
    IReadOnlyList<WorldSnapshot.CasualtyView> Casualties,
    IReadOnlyList<WorldSnapshot.HospitalView> Hospitals,
    IReadOnlyList<WorldSnapshot.OutageView> Outages,
    IReadOnlyList<WorldSnapshot.UnitView> Units,
    IReadOnlyList<WorldSnapshot.ObstructionView> Obstructions,
    IReadOnlyList<CascadeRecord> Cascades,
    WorldSnapshot.CommsView Comms,
    IReadOnlyList<WorldSnapshot.CrewView> Crews,
    IReadOnlyList<WorldSnapshot.DistressView> Distress)
{
    /// <summary>A crew as it really is (Phase 8): how tired and stressed, who is missing, what it was never told.</summary>
    /// <param name="Activity">"working inside", "working outside", "in rehab", "rescue team", "handing over" or the phase.</param>
    public sealed record CrewView(
        Guid UnitId,
        string Callsign,
        double Fatigue,
        double Stress,
        int OnDuty,
        int Rostered,
        string Activity,
        DateTimeOffset ShiftEnd,
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> ForgottenTasks,
        bool UnawareOfEvacuation,
        string Channel);

    /// <summary>A firefighter really in trouble.</summary>
    public sealed record DistressView(Guid Id, string Callsign, string Member, string Cause, GeoPoint Location, bool Heard,
        DateTimeOffset AirRunsOutAt, string? RescueCallsign, double RescueProgress);

    /// <summary>Communications as they really are: black spots, masts, crews that can't be heard, what went unheard.</summary>
    public sealed record CommsView(
        IReadOnlyList<(GeoPoint Centre, double RadiusMeters, string Description)> BlackSpots,
        IReadOnlyList<(string Name, GeoPoint Location, double RadiusMeters, bool Down, bool OnBattery)> Masts,
        IReadOnlyList<(DateTimeOffset At, string From, string Text, string Reason)> RecentLost,
        IReadOnlyList<(string Callsign, string Problem)> Unreachable,
        int CallsWaiting)
    {
        public static readonly CommsView Empty = new([], [], [], [], 0);
    }

    public sealed record IncidentView(Guid Id, IncidentType Type, GeoPoint Location, double Severity, int Casualties, bool Extinguished, DateTimeOffset StartedAt);

    /// <param name="Footprint">The affected area as last recorded (fire, flood) or now (plume).</param>
    /// <param name="Zones">Plume only: areas above each concentration threshold, lowest first.</param>
    public sealed record HazardView(
        Guid Id,
        HazardKind Kind,
        string Description,
        GeoPoint Origin,
        string? Substance,
        double Rate,
        double AreaSquareMeters,
        double Intensity,
        Geometry? Footprint,
        IReadOnlyList<(HazardLevel Level, Geometry Area)> Zones,
        DateTimeOffset StartedAt);

    public sealed record SiteView(HazardSiteKind Kind, string Name, GeoPoint Location, bool Triggered);

    public readonly record struct CivilianView(GeoPoint Location, CivilianState State);

    public readonly record struct CasualtyView(GeoPoint Location, Triage Triage, CasualtyState State, Guid? IncidentId);

    public sealed record HospitalView(Guid Id, string Name, GeoPoint Location, int Occupied, int Capacity, bool OnDiversion, bool OnGenerator);

    public sealed record OutageView(Guid Id, GeoPoint Centre, double RadiusMeters, string Cause, DateTimeOffset StartedAt, DateTimeOffset RestoreAt);

    public sealed record UnitView(Guid Id, string Callsign, UnitType Type, GeoPoint Location, ResponsePhase Phase, bool RadioFailed, bool BrokenDown);

    public sealed record ObstructionView(Geometry Area, string Description);

    public static readonly WorldSnapshot Empty = new(default, new WorldWeather(0, 0, 0, 0), [], [], [], [], [], [], [], [], [], [], CommsView.Empty, [], []);

    public static WorldSnapshot Capture(WorldState world, DateTimeOffset at) => new(
        at,
        world.Weather,
        world.Incidents.Values.Select(i => new IncidentView(i.Id, i.Type, i.Location, i.Severity, i.ActualCasualties, i.Extinguished, i.StartedAt)).ToList(),
        world.Hazards.Values.Where(h => !h.Ended).Select(h => new HazardView(
            h.Id, h.Kind, h.Description, h.Origin, h.Substance, h.Rate,
            h.Model?.AreaSquareMeters ?? 0, h.Model?.Intensity ?? 0,
            h.Model is PlumeModel plume ? plume.Zones.FirstOrDefault().Area : h.Footprint,
            h.Model is PlumeModel p ? p.Zones.Select(z => (z.Level, (Geometry)z.Area)).ToList() : [],
            h.StartedAt)).ToList(),
        world.Sites.Values.Select(s => new SiteView(s.Kind, s.Name, s.Location, s.Triggered)).ToList(),
        world.Civilians.Select(c => new CivilianView(c.Location, c.State)).ToList(),
        world.Casualties.Values.Select(c => new CasualtyView(c.Location, c.Triage, c.State, c.IncidentId)).ToList(),
        world.Hospitals.Values.Select(h => new HospitalView(h.Id, h.Name, h.Location, h.Occupied, h.Capacity, h.OnDiversion, h.OnGenerator)).ToList(),
        world.Outages.Values.Select(o => new OutageView(o.Id, o.Centre, o.RadiusMeters, o.Cause, o.StartedAt, o.RestoreAt)).ToList(),
        world.Units.Values.Select(u => new UnitView(u.Id, u.Callsign, u.Type, u.Location, u.Phase, u.RadioFailed, u.BrokenDown)).ToList(),
        world.Obstructions.Values.Select(o => new ObstructionView(o.Line, o.Description)).ToList(),
        world.Cascades.ToList(),
        CaptureComms(world),
        world.Units.Values.Where(u => u.Crew.Rostered).Select(u => new CrewView(
            u.Id, u.Callsign, u.Crew.Fatigue, u.Crew.Stress, u.Crew.OnDuty, u.Crew.Members.Count, Activity(u, at), u.Crew.ShiftEnd,
            u.Crew.Members.Where(m => m.State is ResponderState.Trapped or ResponderState.Lost).Select(m => m.Title).ToList(),
            u.Crew.ForgottenTasks.ToList(), u.Crew.UnawareOfEvacuation, u.Channel)).ToList(),
        world.Distress.Values.Where(d => !d.Ended).Select(d => new DistressView(
            d.Id, world.Units.GetValueOrDefault(d.UnitId)?.Callsign ?? "?",
            world.Units.GetValueOrDefault(d.UnitId)?.Crew.Members.FirstOrDefault(m => m.Id == d.MemberId)?.Title ?? "?",
            d.Cause, d.Location, d.Heard, d.AirRunsOutAt,
            d.RescueUnitId is { } rescuer ? world.Units.GetValueOrDefault(rescuer)?.Callsign : null, d.RescueProgress)).ToList());

    private static string Activity(WorldUnit unit, DateTimeOffset at) => unit switch
    {
        { Crew.Rescuing: not null } => "rescue team",
        { Crew.HandoverUntil: { } until } when at < until => "handing over",
        { Phase: ResponsePhase.Rehab } => "in rehab",
        { Phase: ResponsePhase.Operating } when CrewFactors.IsFireCrew(unit.Type) => unit.Crew.Withdrawn ? "working outside" : "working inside",
        { Phase: ResponsePhase.Operating } => "working",
        _ => unit.Phase.ToString().ToLowerInvariant(),
    };

    private static CommsView CaptureComms(WorldState world)
    {
        var radio = world.Radio;
        if (!radio.Active) return CommsView.Empty;

        var unreachable = new List<(string, string)>();
        foreach (var unit in world.Units.Values)
        {
            var aiRun = unit.AgencyId is { } agency && world.Agencies.GetValueOrDefault(agency)?.AiControlled == true;
            string? problem = CommsNet.ReachOf(world, unit) switch
            {
                Reach.None => unit.RadioFailed ? "radio failed" : "radio battery flat",
                Reach.BlackSpot => "in a radio black spot",
                Reach.Weak => $"radio battery at {unit.Battery:P0}",
                // Another agency's own talkgroup is expected; a crew of ours command can't hear is not.
                _ when !aiRun && !radio.Linked(unit.Channel).Any(c => RadioPlan.Find(c)?.Monitored == true) => $"on {unit.Channel}, not monitored",
                _ => null,
            };
            if (problem is not null) unreachable.Add((unit.Callsign, problem));
        }

        return new CommsView(
            radio.DeadZones.Select(z => (z.Centre, z.RadiusMeters, z.Description)).ToList(),
            world.Sites.Values.Where(s => s.Kind == HazardSiteKind.CellTower).Select(s =>
            {
                var mast = radio.Masts.GetValueOrDefault(s.Id);
                return (s.Name, s.Location, s.ServiceRadiusMeters, mast?.Down == true, mast?.OnBatterySince is not null);
            }).ToList(),
            radio.RecentLost.ToList(),
            unreachable,
            radio.CallQueue.Count);
    }
}
