using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Casualties, ambulance crews and hospitals as they really are (Design Document §10.6, §17):
/// <list type="bullet">
/// <item>Casualties untreated get worse over time: P3 → P2 → P1 → dead. Treatment on scene slows this a lot.</item>
/// <item>Ambulance crews working at a scene treat the casualties near them, then take the most urgent to the nearest
/// hospital that can take them, by road. A hospital that fills up goes on diversion, and a crew that arrives at a
/// full hospital is sent on to the next one.</item>
/// <item>Hospitals have ordinary demand of their own, report their load to command every 15 minutes (or straight
/// away when they reach 90 % or divert), and activate their surge plan when command notifies them.</item>
/// <item>The first ambulance crew at a scene sends command a casualty count, and updates it as it changes.</item>
/// <item>The incident's true casualty count follows the casualties; casualties a scenario declares are created.</item>
/// </list>
/// What command hears comes only from crews' reports and hospitals' own status reports.
/// </summary>
public sealed class MedicalSystem(IRoutingService? routing = null) : ISimulationSystem
{
    public static readonly TimeSpan TreatmentTime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HandoverTime = TimeSpan.FromMinutes(8);
    public static readonly TimeSpan HospitalReportInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SitrepInterval = TimeSpan.FromMinutes(5);

    /// <summary>Crews treat and collect casualties within this distance of where they are working.</summary>
    public const double CareRadiusMeters = 250;

    /// <summary>Share of extra capacity a hospital's major emergency plan opens up.</summary>
    public const double SurgeFactor = 1.3;

    private readonly Dictionary<Guid, (DateTimeOffset At, string Summary)> _sitreps = [];
    private readonly Dictionary<Guid, List<Guid>> _loads = [];
    private readonly Dictionary<Guid, DateTimeOffset> _treatmentDone = [];
    private readonly HashSet<Guid> _arrivalChecked = [];
    private DateTimeOffset? _lastDemand;

    public int Order => 12; // after units move, before command responses

    public void Update(SimulationContext context)
    {
        SyncDeclaredCasualties(context);
        Deteriorate(context);
        Care(context);
        Handover(context);
        RunHospitals(context);
        Sitreps(context);
        SyncIncidentCounts(context);
    }

    // ---- Casualty counts ----

    /// <summary>A scenario that says "6 people hurt" gets six casualties at the scene.</summary>
    private static void SyncDeclaredCasualties(SimulationContext context)
    {
        var world = context.World;
        foreach (var incident in world.Incidents.Values)
        {
            var have = world.Casualties.Values.Count(c => c.IncidentId == incident.Id);
            if (incident.ActualCasualties <= have) continue;

            var random = SimRandom.For(incident.Id, have + 11);
            for (var i = have; i < incident.ActualCasualties; i++)
            {
                var roll = random.NextDouble();
                var triage = roll < 0.25 ? Triage.Immediate : roll < 0.6 ? Triage.Urgent : Triage.Delayed;
                var location = GeoMath.Destination(incident.Location, random.NextDouble() * 360, 10 + random.NextDouble() * 40);
                var injured = new CasualtyInjured(Guid.NewGuid(), incident.Id, location, triage,
                    $"hurt at the scene ({EventDescriber.Humanize(incident.Type).ToLowerInvariant()})");
                world.Casualties[injured.CasualtyId] = new WorldCasualty
                {
                    Id = injured.CasualtyId, IncidentId = incident.Id, Location = location, Triage = triage,
                    State = CasualtyState.AwaitingTreatment, Cause = injured.Cause, InjuredAt = context.SimTime,
                };
                context.EmitTruth(injured);
            }
        }
    }

    /// <summary>The incident's true casualty count includes everyone hurt there since, however they were hurt.</summary>
    private static void SyncIncidentCounts(SimulationContext context)
    {
        var world = context.World;
        foreach (var incident in world.Incidents.Values)
        {
            var count = world.Casualties.Values.Count(c => c.IncidentId == incident.Id);
            if (count <= incident.ActualCasualties) continue;
            incident.ActualCasualties = count;
            context.EmitTruth(new WorldIncidentChanged(incident.Id, incident.Severity, count, incident.Extinguished));
        }
    }

    // ---- Deterioration ----

    /// <summary>Minutes until an untreated casualty's condition next worsens.</summary>
    private static (double Min, double Max) Survival(Triage triage) => triage switch
    {
        Triage.Immediate => (20, 45),
        Triage.Urgent => (30, 60),
        _ => (60, 120),
    };

    private static void Deteriorate(SimulationContext context)
    {
        foreach (var casualty in context.World.Casualties.Values)
        {
            if (!casualty.Alive || casualty.State is CasualtyState.AtHospital) continue;

            if (casualty.DeteriorateAt is null)
            {
                var (min, max) = Survival(casualty.Triage);
                var minutes = min + SimRandom.For(casualty.Id, (int)casualty.Triage).NextDouble() * (max - min);
                // Treated casualties (on scene or in an ambulance) are stable for much longer.
                if (casualty.State != CasualtyState.AwaitingTreatment) minutes *= 3;
                casualty.DeteriorateAt = context.SimTime + TimeSpan.FromMinutes(minutes);
            }
            if (context.SimTime < casualty.DeteriorateAt) continue;

            casualty.Triage = casualty.Triage switch
            {
                Triage.Delayed => Triage.Urgent,
                Triage.Urgent => Triage.Immediate,
                _ => Triage.Deceased,
            };
            casualty.DeteriorateAt = null;
            if (casualty.Triage == Triage.Deceased)
            {
                casualty.State = CasualtyState.Deceased;
                casualty.CrewId = null;
            }
            context.EmitTruth(new CasualtyChanged(casualty.Id, casualty.Triage, casualty.State, casualty.HospitalId));
        }
    }

    // ---- Treatment and transport ----

    private static bool IsAmbulance(UnitType type) => type is UnitType.AmbulanceAls or UnitType.AmbulanceBls;

    private void Care(SimulationContext context)
    {
        var world = context.World;
        foreach (var crew in world.Units.Values.Where(u => IsAmbulance(u.Type) && u.Phase == ResponsePhase.Operating && !u.BrokenDown))
        {
            var nearby = world.Casualties.Values
                .Where(c => c.NeedsCare && (c.CrewId is null || c.CrewId == crew.Id)
                            && GeoMath.DistanceMeters(c.Location, crew.Location) <= CareRadiusMeters)
                .OrderBy(c => c.Triage)
                .ThenBy(c => c.InjuredAt)
                .ToList();
            if (nearby.Count == 0) continue;

            // Start treating the most urgent casualty if the crew is free.
            var patient = nearby.FirstOrDefault(c => c.CrewId == crew.Id) ?? nearby[0];
            if (patient.CrewId is null)
            {
                patient.CrewId = crew.Id;
                _treatmentDone[crew.Id] = context.SimTime + TreatmentTime;
                continue;
            }

            if (!_treatmentDone.TryGetValue(crew.Id, out var done) || context.SimTime < done) continue;
            _treatmentDone.Remove(crew.Id);

            if (patient.State == CasualtyState.AwaitingTreatment)
            {
                patient.State = CasualtyState.TreatedOnScene;
                patient.DeteriorateAt = null; // stabilised: the clock restarts, slower
                context.EmitTruth(new CasualtyChanged(patient.Id, patient.Triage, patient.State));
            }

            // One P1, or two less serious casualties, per ambulance.
            var load = new List<WorldCasualty> { patient };
            if (patient.Triage != Triage.Immediate)
                load.AddRange(nearby.Where(c => c != patient && c.Triage != Triage.Immediate && c.CrewId is null).Take(1));
            Transport(context, crew, load);
        }
    }

    private void Transport(SimulationContext context, WorldUnit crew, List<WorldCasualty> load)
    {
        if (ChooseHospital(context, crew.Location, exclude: null) is not { } hospital) return;

        foreach (var casualty in load)
        {
            casualty.CrewId = crew.Id;
            casualty.State = CasualtyState.Transporting;
            casualty.HospitalId = hospital.Id;
            casualty.DeteriorateAt = null;
            context.EmitTruth(new CasualtyChanged(casualty.Id, casualty.Triage, casualty.State, hospital.Id));
        }
        _loads[crew.Id] = load.Select(c => c.Id).ToList();

        crew.Destination = hospital.Location;
        crew.Phase = ResponsePhase.Transporting;
        crew.PhaseStartedAt = context.SimTime;
        crew.PendingPlanReason = $"Transporting to {hospital.Name}";

        var summary = string.Join(", ", load.GroupBy(c => c.Triage).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {EventDescriber.TriageLabel(g.Key)}"));
        Report(context, crew, new UnitStatusChanged(crew.Id, UnitStatus.Transporting));
        Report(context, crew, new ReportReceived(Guid.NewGuid(), crew.OrderedIncidentId, ReportSource.FieldUnit, crew.Callsign,
            $"Leaving scene with {summary} for {hospital.Name}.", Confidence.High, VerificationStatus.Confirmed, crew.Location, 20, crew.Id));
    }

    /// <summary>The hospital that can take patients soonest by road, skipping full ones.</summary>
    private WorldHospital? ChooseHospital(SimulationContext context, GeoPoint from, Guid? exclude)
    {
        var candidates = context.World.Hospitals.Values.Where(h => h.Id != exclude && !h.OnDiversion).ToList();
        if (candidates.Count == 0)
            candidates = context.World.Hospitals.Values.Where(h => h.Id != exclude).ToList(); // everywhere is full: least bad
        if (candidates.Count == 0) return null;

        return candidates.MinBy(h =>
            routing?.Route(from, h.Location, new RouteOptions(UnitType.AmbulanceAls, context.SimTime))?.Duration.TotalSeconds
            ?? GeoMath.DistanceMeters(from, h.Location) / 10);
    }

    private void Handover(SimulationContext context)
    {
        var world = context.World;
        foreach (var crew in world.Units.Values.Where(u => u.Phase == ResponsePhase.AtHospital))
        {
            var load = _loads.GetValueOrDefault(crew.Id) ?? [];
            var hospitalId = load.Select(id => world.Casualties.GetValueOrDefault(id)?.HospitalId).FirstOrDefault(h => h is not null);
            var hospital = hospitalId is { } hid ? world.Hospitals.GetValueOrDefault(hid) : null;

            // Arrived to find the department full: on to the next hospital.
            if (hospital is { OnDiversion: true } && _arrivalChecked.Add(crew.Id)
                && ChooseHospital(context, crew.Location, hospital.Id) is { } next && next.Id != hospital.Id)
            {
                foreach (var casualty in load.Select(world.Casualties.GetValueOrDefault).OfType<WorldCasualty>())
                {
                    casualty.HospitalId = next.Id;
                    context.EmitTruth(new CasualtyChanged(casualty.Id, casualty.Triage, casualty.State, next.Id));
                }
                crew.Destination = next.Location;
                crew.Phase = ResponsePhase.Transporting;
                crew.PendingPlanReason = $"Diverted from {hospital.Name} (full) to {next.Name}";
                context.EmitTruth(new CascadeOccurred($"{hospital.Name} emergency department full",
                    $"{crew.Callsign} diverted to {next.Name}", crew.Id));
                Report(context, crew, new ReportReceived(Guid.NewGuid(), crew.OrderedIncidentId, ReportSource.FieldUnit, crew.Callsign,
                    $"{hospital.Name} is on diversion, continuing to {next.Name}.", Confidence.High, VerificationStatus.Confirmed,
                    crew.Location, 20, crew.Id));
                continue;
            }

            if (context.SimTime - crew.PhaseStartedAt < HandoverTime) continue;

            foreach (var casualty in load.Select(world.Casualties.GetValueOrDefault).OfType<WorldCasualty>())
            {
                if (!casualty.Alive) continue;
                casualty.State = CasualtyState.AtHospital;
                casualty.CrewId = null;
                casualty.Location = crew.Location;
                if (casualty.HospitalId is { } at && world.Hospitals.TryGetValue(at, out var receiving))
                    receiving.Delivered++;
                context.EmitTruth(new CasualtyChanged(casualty.Id, casualty.Triage, casualty.State, casualty.HospitalId));
            }
            _loads.Remove(crew.Id);
            _arrivalChecked.Remove(crew.Id);

            // Clear: available again from the hospital.
            crew.Phase = ResponsePhase.Idle;
            crew.OrderedIncidentId = null;
            crew.SpeedKph = 0;
            Report(context, crew, new UnitStatusChanged(crew.Id, UnitStatus.Available));
        }
    }

    // ---- Hospitals ----

    private void RunHospitals(SimulationContext context)
    {
        var world = context.World;
        if (world.Hospitals.Count == 0) return;

        // Ordinary demand: every 10 minutes each department gains or loses a patient or two.
        _lastDemand ??= context.SimTime;
        var demandTick = context.SimTime - _lastDemand >= TimeSpan.FromMinutes(10);
        if (demandTick) _lastDemand = context.SimTime;

        foreach (var hospital in world.Hospitals.Values)
        {
            if (demandTick)
            {
                var random = SimRandom.For(hospital.Id, (int)(context.SimTime.ToUnixTimeSeconds() / 600));
                hospital.Baseline = Math.Clamp(hospital.Baseline + random.Next(-1, 3), 0, hospital.Capacity);
            }

            // Notified of the major incident by command: the hospital activates its major emergency plan.
            if (!hospital.SurgeActivated && world.Notified.Keys.Any(k => Mentions(k, hospital.Name)))
            {
                hospital.SurgeActivated = true;
                SetCapacity(context, hospital, (int)Math.Round(hospital.Capacity * SurgeFactor), "major emergency plan activated");
            }

            // On generators: fewer beds and theatres usable.
            var dark = world.InOutage(hospital.Location);
            if (dark != hospital.OnGenerator)
            {
                hospital.OnGenerator = dark;
                SetCapacity(context, hospital, (int)Math.Round(dark ? hospital.Capacity * 0.85 : hospital.Capacity / 0.85),
                    dark ? "mains power lost, running on generators" : "mains power restored");
                if (dark)
                    context.EmitTruth(new CascadeOccurred("Power outage", $"{hospital.Name} on generators, emergency capacity reduced"));
            }

            ReportStatus(context, hospital);
        }
    }

    /// <summary>"St. James's Hospital" matches a notification to "St James's" or "St. James's Hospital ED".</summary>
    private static bool Mentions(string recipient, string hospitalName)
    {
        static string Key(string s) => new(s.ToLowerInvariant().Where(char.IsLetter).ToArray());
        var name = Key(hospitalName.Replace("Hospital", "", StringComparison.OrdinalIgnoreCase));
        return name.Length >= 4 && Key(recipient).Contains(name);
    }

    private static void SetCapacity(SimulationContext context, WorldHospital hospital, int capacity, string reason)
    {
        hospital.Capacity = Math.Max(1, capacity);
        context.EmitTruth(new HospitalCapacityChanged(hospital.Id, hospital.Capacity, reason));
    }

    private static void ReportStatus(SimulationContext context, WorldHospital hospital)
    {
        var load = (double)hospital.Occupied / hospital.Capacity;
        var reportedLoad = (double)(hospital.ReportedOccupied ?? 0) / hospital.Capacity;
        var due = hospital.ReportedAt is not { } last || context.SimTime - last >= HospitalReportInterval;
        var crossed = (load >= 0.9) != (reportedLoad >= 0.9) || hospital.OnDiversion != hospital.ReportedDiversion;
        if (!due && !crossed) return;

        hospital.ReportedAt = context.SimTime;
        hospital.ReportedOccupied = hospital.Occupied;
        hospital.ReportedDiversion = hospital.OnDiversion;

        var free = Math.Max(0, hospital.Capacity - hospital.Occupied);
        var note = hospital.OnDiversion
            ? "Emergency department full: ambulances diverted elsewhere."
            : load >= 0.9 ? $"Near capacity: can accept {free} more."
            : $"Can accept {free} more.";
        if (hospital.SurgeActivated) note += " Major emergency plan active.";
        if (hospital.OnGenerator) note += " On generator power.";
        context.EmitPerceived(new HospitalStatusReported(hospital.Id, hospital.Name, hospital.Occupied, hospital.Capacity,
            hospital.OnDiversion, note), EventSources.Comms);
    }

    // ---- Crew sitreps ----

    /// <summary>
    /// The first ambulance crew working at a scene counts the casualties it can see and tells command, then
    /// updates the count every few minutes when it changes. What it can see is what is within its care radius.
    /// </summary>
    private void Sitreps(SimulationContext context)
    {
        var world = context.World;
        foreach (var incident in world.Incidents.Values)
        {
            var crew = world.Units.Values
                .Where(u => IsAmbulance(u.Type) && u.Phase == ResponsePhase.Operating && !u.RadioFailed
                            && GeoMath.DistanceMeters(u.Location, incident.Location) <= CareRadiusMeters * 2)
                .OrderBy(u => u.PhaseStartedAt)
                .FirstOrDefault();
            if (crew is null) continue;

            var seen = world.Casualties.Values
                .Where(c => c.State != CasualtyState.AtHospital && c.State != CasualtyState.Transporting
                            && GeoMath.DistanceMeters(c.Location, crew.Location) <= CareRadiusMeters * 1.5)
                .ToList();
            var alive = seen.Where(c => c.Alive).ToList();
            var summary = $"{alive.Count} casualties at scene" +
                          (alive.Count > 0 ? ": " + string.Join(", ", alive.GroupBy(c => c.Triage).OrderBy(g => g.Key)
                              .Select(g => $"{g.Count()} {EventDescriber.TriageLabel(g.Key)}")) : "") +
                          (seen.Count > alive.Count ? $"; {seen.Count - alive.Count} deceased" : "");

            var previous = _sitreps.GetValueOrDefault(incident.Id);
            if (previous.Summary == summary || (previous.Summary is not null && context.SimTime - previous.At < SitrepInterval)) continue;
            _sitreps[incident.Id] = (context.SimTime, summary);

            context.EmitPerceived(new ReportReceived(Guid.NewGuid(), crew.OrderedIncidentId, ReportSource.FieldUnit, crew.Callsign,
                summary + ".", Confidence.Medium, VerificationStatus.Reported, crew.Location, 30, crew.Id), EventSources.Comms);
        }
    }

    private static void Report(SimulationContext context, WorldUnit unit, DomainEvent payload)
    {
        if (!unit.RadioFailed)
            context.EmitPerceived(payload, EventSources.Comms);
    }
}
