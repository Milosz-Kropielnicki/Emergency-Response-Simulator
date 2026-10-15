using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Triage tags, patient tracking, specialist centres and blood (Phase 16).
/// <list type="bullet">
/// <item><b>Tags</b>: when an ambulance crew starts treating a casualty it tags them (T-0001…) with their priority and
/// what they need: major trauma, burns, a children's hospital, or an ordinary emergency department. The tag goes on the
/// tracking board, and every change after it: re-triage, leaving the scene, arrival, transfer, death.</item>
/// <item><b>The right hospital</b>: crews go where command's destination for the incident and need says, or else the
/// nearest open hospital (never a children's hospital for an adult). A patient who arrives somewhere without the service
/// they need is transferred after <see cref="TransferDelay"/>, to the nearest hospital with a free bed of the right kind;
/// a P1 may not survive the wait.</item>
/// <item><b>Blood</b>: hospitals use blood for their ordinary work and far more for seriously injured patients. Below
/// <see cref="BloodLow"/> units they say so; command can ask the Irish Blood Transfusion Service for more, which arrives
/// after <see cref="BloodLeadTime"/>. A P1 who arrives to no blood may die.</item>
/// <item><b>Reports</b>: each hospital reports its specialist beds, blood, mortuary and walk-ins every
/// <see cref="ReportInterval"/>, and at once when blood runs low.</item>
/// </list>
/// </summary>
public sealed class PatientTrackingSystem : ISimulationSystem
{
    public static readonly TimeSpan TransferDelay = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan BloodLeadTime = TimeSpan.FromMinutes(40);
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(15);
    public const int BloodLow = 15;

    /// <summary>Share of its usual stock a hospital uses an hour for its ordinary patients (resupplied overnight, outside the exercise).</summary>
    public const double OrdinaryBloodPerHour = 0.025;

    private const string Ibts = "Irish Blood Transfusion Service";

    private readonly Dictionary<Guid, (Triage Triage, CasualtyState State, Guid? HospitalId)> _tagged = [];
    private readonly Dictionary<Guid, DateTimeOffset> _transferAt = [];
    private readonly Dictionary<Guid, DateTimeOffset> _dieAt = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextReport = [];
    private readonly HashSet<Guid> _bloodWarned = [];
    private readonly HashSet<(Guid, Specialty)> _noBedWarned = [];
    private int _tags;

    public int Order => 14; // after ambulance crews (12) have treated, carried and handed over

    public void Update(SimulationContext context)
    {
        Tag(context);
        Track(context);
        Transfer(context);
        Deaths(context);
        Blood(context);
        Report(context);
    }

    private static string HospitalName(WorldState world, Guid? id) => id is { } h && world.Hospitals.TryGetValue(h, out var hospital) ? hospital.Name : "hospital";

    // ---- Tags ----

    private void Tag(SimulationContext context)
    {
        var world = context.World;
        foreach (var casualty in world.Casualties.Values)
        {
            if (_tagged.ContainsKey(casualty.Id) || casualty.CrewId is not { } crewId || !casualty.Alive || !world.Units.TryGetValue(crewId, out var crew)) continue;
            _tagged[casualty.Id] = (casualty.Triage, casualty.State, casualty.HospitalId);
            var tag = Health.TagNumber(++_tags);
            CommsNet.Data(context, crew, new PatientTagged(casualty.Id, tag, crew.OrderedIncidentId, casualty.Triage,
                world.Health.Needs.GetValueOrDefault(casualty.Id), Injury(casualty.Cause), crew.Id), EventSources.Comms);
        }
    }

    /// <summary>The injury as a crew would write it on the card.</summary>
    private static string Injury(string cause) => cause.Length > 60 ? cause[..60] : cause;

    // ---- Tracking ----

    private void Track(SimulationContext context)
    {
        var world = context.World;
        foreach (var (id, seen) in _tagged.ToList())
        {
            if (!world.Casualties.TryGetValue(id, out var casualty)) continue;
            if (casualty.Triage == seen.Triage && casualty.State == seen.State && casualty.HospitalId == seen.HospitalId) continue;
            _tagged[id] = (casualty.Triage, casualty.State, casualty.HospitalId);
            if (seen.Triage == Triage.Deceased) continue;

            if (!casualty.Alive)
            {
                var where = seen.State switch
                {
                    CasualtyState.Transporting => "on the way to hospital",
                    CasualtyState.AtHospital => $"in {HospitalName(world, casualty.HospitalId)}",
                    _ => "at the scene",
                };
                context.EmitPerceived(new PatientDied(id, where), EventSources.Comms);
                continue;
            }
            if (casualty.Triage != seen.Triage)
                context.EmitPerceived(new PatientRetriaged(id, casualty.Triage), EventSources.Comms);

            if (casualty.State == CasualtyState.Transporting && seen.State != CasualtyState.Transporting
                && casualty.CrewId is { } crew && casualty.HospitalId is { } bound)
                context.EmitPerceived(new PatientTransported(id, crew, bound), EventSources.Comms);

            if (casualty.State == CasualtyState.AtHospital && seen.State != CasualtyState.AtHospital && casualty.HospitalId is { } at)
                Arrive(context, casualty, at);
        }
    }

    private void Arrive(SimulationContext context, WorldCasualty casualty, Guid hospitalId)
    {
        var world = context.World;
        var health = world.Health;
        var need = health.Needs.GetValueOrDefault(casualty.Id);
        var right = health.CanTreat(hospitalId, need);
        if (right && need is { } specialty) Admit(health, hospitalId, specialty);
        if (!right) _transferAt[casualty.Id] = context.SimTime + TransferDelay;
        context.EmitPerceived(new PatientArrived(casualty.Id, hospitalId, right), EventSources.Comms);

        // Blood: a seriously injured patient needs a lot of it, now.
        if (health.Hospitals.GetValueOrDefault(hospitalId) is not { } care) return;
        var units = (need, casualty.Triage) switch
        {
            (Specialty.Trauma, Triage.Immediate) => 6,
            (Specialty.Trauma, _) => 2,
            (Specialty.Burns, Triage.Immediate) => 2,
            (_, Triage.Immediate) => 1,
            _ => 0,
        };
        if (care.Blood >= units)
        {
            care.Blood -= units;
            return;
        }
        care.Blood = 0;
        if (casualty.Triage == Triage.Immediate && SimRandom.For(casualty.Id, 163).NextDouble() < 0.4)
            _dieAt[casualty.Id] = context.SimTime + TimeSpan.FromMinutes(20);
    }

    private static void Admit(WorldHealth health, Guid hospitalId, Specialty specialty)
    {
        var care = health.Hospitals[hospitalId];
        care.Occupied[specialty] = care.Occupied.GetValueOrDefault(specialty) + 1;
    }

    // ---- Secondary transfers ----

    private void Transfer(SimulationContext context)
    {
        var world = context.World;
        var health = world.Health;
        foreach (var (id, due) in _transferAt.ToList())
        {
            if (context.SimTime < due) continue;
            if (!world.Casualties.TryGetValue(id, out var casualty) || !casualty.Alive || casualty.HospitalId is not { } fromId
                || health.Needs.GetValueOrDefault(id) is not { } need)
            {
                _transferAt.Remove(id);
                continue;
            }
            var from = world.Hospitals[fromId];
            var to = world.Hospitals.Values.Where(h => h.Id != fromId && health.CanTreat(h.Id, need) && health.Accepts(h, need))
                .MinBy(h => GeoMath.DistanceMeters(h.Location, from.Location));
            if (to is null)
            {
                _transferAt[id] = context.SimTime + ReportInterval;
                if (_noBedWarned.Add((fromId, need)))
                    CommsNet.Chat(context, from.Name, $"{from.Name}: we have a patient needing {Health.Name(need).ToLowerInvariant()} care and there is no free bed " +
                                                      "anywhere in the region. We need a national (or cross-border) transfer.", null);
                continue;
            }
            _transferAt.Remove(id);

            // A seriously injured patient waiting in the wrong place for definitive care may not make it.
            if (casualty.Triage == Triage.Immediate && SimRandom.For(id, 164).NextDouble() < 0.2)
            {
                Die(context, casualty);
                continue;
            }
            from.Delivered = Math.Max(0, from.Delivered - 1);
            to.Delivered++;
            casualty.HospitalId = to.Id;
            casualty.Location = to.Location;
            Admit(health, to.Id, need);
            _tagged[id] = (casualty.Triage, casualty.State, casualty.HospitalId);
            context.EmitTruth(new CasualtyChanged(id, casualty.Triage, casualty.State, to.Id));
            CommsNet.Chat(context, from.Name, $"{from.Name}: transferring a patient to {to.Name} for {Health.Name(need).ToLowerInvariant()} care.",
                new PatientTransferred(id, fromId, to.Id, $"needs {Health.Name(need).ToLowerInvariant()} care"));
        }
    }

    private void Deaths(SimulationContext context)
    {
        foreach (var (id, due) in _dieAt.ToList())
        {
            if (context.SimTime < due) continue;
            _dieAt.Remove(id);
            if (context.World.Casualties.TryGetValue(id, out var casualty) && casualty.Alive) Die(context, casualty);
        }
    }

    /// <summary>A patient dies in hospital (the tracking pass reports it next tick).</summary>
    private static void Die(SimulationContext context, WorldCasualty casualty)
    {
        casualty.Triage = Triage.Deceased;
        context.World.Health.DiedInHospital.Add(casualty.Id);
        context.EmitTruth(new CasualtyChanged(casualty.Id, Triage.Deceased, casualty.State, casualty.HospitalId));
    }

    // ---- Blood ----

    private void Blood(SimulationContext context)
    {
        var world = context.World;
        var health = world.Health;
        foreach (var due in health.BloodDue.Where(d => context.SimTime >= d.Due).ToList())
        {
            health.BloodDue.Remove(due);
            if (!health.Hospitals.TryGetValue(due.HospitalId, out var stocked)) continue;
            stocked.Blood += due.Units;
            CommsNet.Chat(context, Ibts, $"{Ibts}: {due.Units} units delivered to {HospitalName(world, due.HospitalId)}.",
                new BloodDelivered(due.HospitalId, due.Units), TimeSpan.Zero);
        }

        foreach (var (id, care) in health.Hospitals)
        {
            care.Blood = Math.Max(0, care.Blood - care.UsualBlood * OrdinaryBloodPerHour * context.Delta.TotalHours);
            if (care.Blood >= BloodLow * 2) _bloodWarned.Remove(id);
            if (care.Blood >= BloodLow || !_bloodWarned.Add(id)) continue;
            var name = HospitalName(world, id);
            context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.ResourceShortage, AlertSeverity.Warning, $"Blood running low at {name}",
                $"{name} has {(int)care.Blood} units of blood left. Ask the IBTS for more before the next seriously injured patient arrives.", null, null),
                EventSources.Comms);
            _nextReport.Remove(id); // report the new state now
        }
    }

    // ---- Hospital reports ----

    private void Report(SimulationContext context)
    {
        var world = context.World;
        foreach (var (id, care) in world.Health.Hospitals)
        {
            if (_nextReport.TryGetValue(id, out var next) && context.SimTime < next) continue;
            _nextReport[id] = context.SimTime + ReportInterval;
            context.EmitPerceived(new HospitalResourcesReported(id, care.Specialties(), (int)care.Blood, care.MorgueOccupied, care.WorriedWellNow),
                EventSources.Comms);
        }
    }
}
