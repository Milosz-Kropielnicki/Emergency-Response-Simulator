using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Incidents within the incident (Phase 19): the threats that turn on the responders themselves.
/// <list type="bullet">
/// <item><b>Secondary device</b>: left where responders gather. A crew within 150 m spots it (or, failing that, a passer-by
/// rings in after five minutes). A bomb disposal unit on scene within 150 m makes it safe after <see cref="RenderSafeTime"/>
/// of work. Otherwise it goes off at the end of its fuse: everyone still within <see cref="BlastRadiusMeters"/> is hurt —
/// civilians, and one member of each unit there — and the blast starts a fire. Pulling crews back and clearing the area is
/// what saves them.</item>
/// <item><b>Active shooter</b>: callers report shots at once. Every two minutes the shooter hits someone within
/// <see cref="ShooterRangeMeters"/> (people sheltering indoors are much safer), and an unarmed crew working there may lose
/// someone too. It ends when armed gardaí (an armed response unit) have been on scene within range for
/// <see cref="ArmedResponseTime"/>; otherwise the shooter stops after an hour or twelve victims, never caught.</item>
/// </list>
/// </summary>
public sealed class CompoundThreatSystem : ISimulationSystem
{
    public const double SpotMeters = 150;
    public const double BlastRadiusMeters = 60;
    public const double ShooterRangeMeters = 200;
    public static readonly TimeSpan RenderSafeTime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PasserByCall = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ShotInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ArmedResponseTime = TimeSpan.FromMinutes(3);

    private readonly HashSet<Guid> _called = [];

    public int Order => 6; // after hazards (5), before units move (10)

    public void Update(SimulationContext context)
    {
        foreach (var device in context.World.Training.Devices.Values.Where(d => d.Live))
            Device(context, device);
        foreach (var shooter in context.World.Training.Shooters.Values.Where(s => !s.Stopped))
            Shooter(context, shooter);
    }

    private static IEnumerable<WorldUnit> AtScene(WorldState world, GeoPoint at, double meters) =>
        world.Units.Values.Where(u => u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating && GeoMath.DistanceMeters(u.Location, at) <= meters);

    // ---- Secondary device ----

    private void Device(SimulationContext context, WorldTraining.Device device)
    {
        var world = context.World;
        var at = device.Placed.Location;

        if (!device.Found)
        {
            if (AtScene(world, at, SpotMeters).Where(u => !u.RadioFailed).OrderBy(u => GeoMath.DistanceMeters(u.Location, at)).FirstOrDefault() is { } spotter)
            {
                device.Found = true;
                CommsNet.Report(context, spotter, new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, spotter.Callsign,
                    $"{spotter.Callsign}: suspicious item, {device.Placed.Description}. It wasn't there when we arrived. Recommend we pull back and get the bomb squad.",
                    Confidence.Medium, VerificationStatus.Reported, at, 20, spotter.Id), priority: 9);
            }
            else if (context.SimTime - device.PlacedAt >= PasserByCall && _called.Add(device.Placed.DeviceId))
            {
                device.Found = true;
                CommsNet.EmergencyCall(context, new PendingCall
                {
                    Caller = "999 caller (mobile)", Location = GeoMath.Destination(at, 90, 30), AccuracyMeters = 50, Patience = TimeSpan.FromMinutes(3),
                    Summary = $"There's {device.Placed.Description.TrimStart()} near all the emergency vehicles, it looks dodgy, nobody's near it",
                });
            }
        }

        // The bomb disposal team at work.
        var disposal = AtScene(world, at, SpotMeters).FirstOrDefault(u => u.Type == UnitType.BombDisposal);
        if (disposal is not null)
        {
            device.WorkStartedAt ??= context.SimTime;
            if (context.SimTime - device.WorkStartedAt >= RenderSafeTime && context.SimTime < device.GoesOffAt)
            {
                device.Safe = true;
                context.EmitTruth(new SecondaryDeviceRenderedSafe(device.Placed.DeviceId));
                CommsNet.Report(context, disposal, new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, disposal.Callsign,
                    $"{disposal.Callsign}: device rendered safe. Area can be reopened.", Confidence.High, VerificationStatus.Confirmed, at, 10, disposal.Id));
                return;
            }
        }
        else device.WorkStartedAt = null;

        if (context.SimTime < device.GoesOffAt) return;
        Detonate(context, device);
    }

    private static void Detonate(SimulationContext context, WorldTraining.Device device)
    {
        var world = context.World;
        var at = device.Placed.Location;
        device.Detonated = true;
        var incident = world.Incidents.Values.Where(i => !i.Extinguished).MinBy(i => GeoMath.DistanceMeters(i.Location, at))?.Id;
        var random = SimRandom.For(device.Placed.DeviceId, 19);
        var hurt = 0;
        foreach (var person in world.Civilians.Where(p => p.State is not (CivilianState.Injured or CivilianState.Safe) && p.VehicleId is null
                                                          && GeoMath.DistanceMeters(p.Location, at) <= BlastRadiusMeters).ToList())
        {
            Hurt(context, person.Location, incident, random.NextDouble() < 0.4 ? Triage.Immediate : Triage.Urgent, "blast injuries: secondary device", person.Who, person);
            hurt++;
        }
        var responders = 0;
        foreach (var unit in AtScene(world, at, BlastRadiusMeters).ToList())
        {
            Hurt(context, unit.Location, incident, random.NextDouble() < 0.5 ? Triage.Immediate : Triage.Urgent, "blast injuries: secondary device",
                $"a responder from {unit.Callsign}", null);
            context.EmitTruth(new ResponderDown(unit.Id, $"a responder from {unit.Callsign}", "caught by the secondary device"));
            responders++;
        }
        context.EmitTruth(new SecondaryDeviceDetonated(device.Placed.DeviceId, hurt + responders, responders));
        context.EmitTruth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Explosion, at, 0.25, 0));
        CommsNet.EmergencyCall(context, new PendingCall
        {
            Caller = "999 caller (mobile)", Location = GeoMath.Destination(at, 200, 60), AccuracyMeters = 60, Patience = TimeSpan.FromMinutes(3),
            Summary = "Another explosion, right where the ambulances were, people are down",
        });
    }

    private static void Hurt(SimulationContext context, GeoPoint at, Guid? incident, Triage triage, string cause, string? who, Civilian? person)
    {
        var injured = new CasualtyInjured(Guid.NewGuid(), incident, at, triage, cause, who);
        if (person is not null)
        {
            person.State = CivilianState.Injured;
            person.Target = null;
        }
        context.World.Casualties.TryAdd(injured.CasualtyId, new WorldCasualty
        {
            Id = injured.CasualtyId, IncidentId = incident, Location = at, Triage = triage, State = CasualtyState.AwaitingTreatment, Cause = cause,
            InjuredAt = context.SimTime, CivilianId = person?.Id,
        });
        context.EmitTruth(injured);
    }

    // ---- Active shooter ----

    private void Shooter(SimulationContext context, WorldTraining.Shooter shooter)
    {
        var world = context.World;
        var at = shooter.Started.Location;
        var id = shooter.Started.ThreatId;
        if (_called.Add(id))
        {
            foreach (var (bearing, summary) in new[] { (30.0, "Shots fired! Someone's shooting at people on the street, we're hiding in a shop"),
                                                         (250.0, "There's a man with a gun, I heard four or five shots, people are running") })
                CommsNet.EmergencyCall(context, new PendingCall
                {
                    Caller = "999 caller (mobile)", Location = GeoMath.Destination(at, bearing, 80), AccuracyMeters = 70, Patience = TimeSpan.FromMinutes(4), Summary = summary,
                });
        }

        // Armed gardaí on scene, long enough, end it.
        var armed = AtScene(world, at, ShooterRangeMeters + 100).FirstOrDefault(u => u.Type == UnitType.Swat);
        shooter.ArmedSince = armed is null ? null : shooter.ArmedSince ?? context.SimTime;
        if (armed is not null && context.SimTime - shooter.ArmedSince >= ArmedResponseTime)
        {
            shooter.Stopped = true;
            context.EmitTruth(new ActiveShooterNeutralised(id, shooter.Victims));
            CommsNet.Report(context, armed, new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, armed.Callsign,
                $"{armed.Callsign}: suspect neutralised. Scene safe for medical teams to come forward.", Confidence.High, VerificationStatus.Confirmed, at, 20, armed.Id), priority: 9);
            return;
        }
        if (context.SimTime - shooter.StartedAt >= TimeSpan.FromHours(1) || shooter.Victims >= 12)
        {
            shooter.Stopped = true; // gone: never caught
            return;
        }

        if (shooter.LastShotAt is { } last && context.SimTime - last < ShotInterval) return;
        shooter.LastShotAt = context.SimTime;
        var random = SimRandom.For(id, (int)(context.SimTime - shooter.StartedAt).TotalMinutes);
        var incident = world.Incidents.Values.Where(i => !i.Extinguished).MinBy(i => GeoMath.DistanceMeters(i.Location, at))?.Id;

        // Someone in the open in range; sheltering indoors protects most people.
        var exposed = world.Civilians.Where(p => p.State is not (CivilianState.Injured or CivilianState.Safe) && p.VehicleId is null
                                                 && GeoMath.DistanceMeters(p.Location, at) <= ShooterRangeMeters
                                                 && (p.State != CivilianState.Sheltering || random.NextDouble() < 0.15)).ToList();
        if (exposed.Count > 0)
        {
            var victim = exposed[random.Next(exposed.Count)];
            Hurt(context, victim.Location, incident, random.NextDouble() < 0.5 ? Triage.Immediate : Triage.Urgent, "gunshot wound", victim.Who, victim);
            shooter.Victims++;
        }

        // Unarmed crews working in range are targets too.
        foreach (var unit in AtScene(world, at, ShooterRangeMeters).Where(u => ResourceGroups.AgencyFor(u.Type) != AgencyType.Police).ToList())
        {
            if (random.NextDouble() >= 0.15) continue;
            Hurt(context, unit.Location, incident, Triage.Immediate, "gunshot wound", $"a responder from {unit.Callsign}", null);
            context.EmitTruth(new ResponderDown(unit.Id, $"a responder from {unit.Callsign}", "shot while working at the scene"));
            shooter.Victims++;
            break;
        }
    }
}
