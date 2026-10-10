using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
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
    IReadOnlyList<CascadeRecord> Cascades)
{
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

    public static readonly WorldSnapshot Empty = new(default, new WorldWeather(0, 0, 0, 0), [], [], [], [], [], [], [], [], [], []);

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
        world.Cascades.ToList());
}
