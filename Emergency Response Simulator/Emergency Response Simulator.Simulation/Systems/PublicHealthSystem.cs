using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Public-health interventions (Phase 16). Timescales are compressed so that an outbreak plays out within an exercise;
/// the real ones are days.
/// <list type="bullet">
/// <item><b>Threats</b>: a biological or radiological release (a plume of that material) exposes everyone it reaches
/// outdoors, or indoors at a moderate level and above; cases of an infectious disease among a group of contacts (an
/// inject) spread person to person. HSE Public Health identifies the threat and tells command after
/// <see cref="AlertDelay"/> (<see cref="InfectiousAlertDelay"/> for an outbreak), with an estimate of the exposed.</item>
/// <item><b>Illness</b>: biological: about a third of the unprotected exposed fall ill 2–5 hours later and go to an
/// emergency department; some die. Radiological: no illness during the exercise, but the unprotected exposed carry a
/// thyroid dose. Infectious: each case not isolated infects contacts; quarantine cuts their mixing to a fifth.</item>
/// <item><b>Isolation and quarantine</b> are HSE Public Health's orders to make (a Medical Officer of Health under the
/// Health Acts). Command ordering either without having notified HSE Public Health is a legal exposure.</item>
/// <item><b>Mass prophylaxis</b>: a point of dispensing opens after <see cref="PodSetup"/> and dispenses up to
/// <see cref="PodPerHour"/> an hour, a dose each from its incident's prophylaxis stock (Phase 15). People come slowly by
/// word of mouth, and four times as fast once a public statement tells them to. A dose within the countermeasure's
/// window protects; later, it doesn't.</item>
/// </list>
/// </summary>
public sealed class PublicHealthSystem : ISimulationSystem
{
    public static readonly TimeSpan AlertDelay = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan InfectiousAlertDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PodSetup = TimeSpan.FromMinutes(45);
    public const double PodPerHour = 400;
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Incubation = TimeSpan.FromMinutes(75);

    /// <summary>New infections an hour from each case that is not isolated, among unquarantined contacts.</summary>
    public const double Transmission = 1.0;

    private const string PublicHealth = "HSE Public Health";

    private readonly HashSet<Guid> _hazardsSeen = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextUpdate = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextPodReport = [];
    private readonly Dictionary<Guid, double> _capacity = [];
    private readonly HashSet<(Guid, string)> _ordersChecked = [];
    private readonly List<(Guid Threat, DateTimeOffset At)> _deaths = [];
    private double _illCarry, _deathCarry;
    private DateTimeOffset _nextExposureCheck;

    public int Order => 19;

    public void Update(SimulationContext context)
    {
        Start(context);
        foreach (var threat in context.World.Health.Threats.Values)
        {
            Bind(context, threat);
            Expose(context, threat);
            Alert(context, threat);
            Illness(context, threat);
            Orders(context, threat);
            Dispense(context, threat);
            Updates(context, threat);
        }
        if (context.SimTime >= _nextExposureCheck) _nextExposureCheck = context.SimTime + TimeSpan.FromSeconds(30);
        Deaths(context);
    }

    // ---- Threats ----

    /// <summary>A release of biological or radioactive material is a public-health threat.</summary>
    private void Start(SimulationContext context)
    {
        foreach (var hazard in context.World.Hazards.Values.Where(h => h.Kind == HazardKind.Plume && _hazardsSeen.Add(h.Id)))
        {
            var substance = Substance.For(hazard.Substance);
            if (!substance.IsBiological && !substance.IsRadiological) continue;
            var kind = substance.IsBiological ? HealthThreatKind.Biological : HealthThreatKind.Radiological;
            // Its id comes from the release's, so who falls ill (seeded from it) is the same on every replay.
            context.EmitTruth(new HealthThreatStarted(SimRandom.Derive(hazard.Id, 167), kind, hazard.Substance ?? substance.Name, hazard.Origin,
                Area(context.World, hazard.Origin), hazard.Id));
        }
    }

    public static string Area(WorldState world, GeoPoint point) =>
        world.Interagency.Incidents.Values.Where(i => GeoMath.DistanceMeters(i.Location, point) <= 2000)
            .OrderBy(i => GeoMath.DistanceMeters(i.Location, point)).Select(i => $"around {i.Place}").FirstOrDefault() ?? "the release site";

    /// <summary>The perceived incident it belongs to: the nearest command has opened.</summary>
    private static void Bind(SimulationContext context, WorldHealth.Threat threat)
    {
        if (threat.IncidentId is not null) return;
        threat.IncidentId = context.World.Logistics.IncidentLocations.Where(i => GeoMath.DistanceMeters(i.Value, threat.Started.Location) <= 3000)
            .OrderBy(i => GeoMath.DistanceMeters(i.Value, threat.Started.Location)).Select(i => (Guid?)i.Key).FirstOrDefault();
    }

    /// <summary>Everyone the release reaches: outdoors at any level, indoors only at a moderate level and above.</summary>
    private void Expose(SimulationContext context, WorldHealth.Threat threat)
    {
        if (context.SimTime < _nextExposureCheck || threat.Started.HazardId is not { } hazardId
            || !context.World.Hazards.TryGetValue(hazardId, out var hazard) || hazard.Ended || hazard.Model is not { } model) return;
        foreach (var person in context.World.Civilians)
        {
            if (threat.Exposed.ContainsKey(person.Id)) continue;
            var level = model.ExposureAt(person.Location).Level;
            if (level == HazardLevel.None || person.State == CivilianState.Sheltering && level < HazardLevel.Moderate) continue;
            var random = SimRandom.For(threat.Started.ThreatId, person.Id);
            var onset = context.SimTime + TimeSpan.FromHours(2 + random.NextDouble() * 3);
            threat.Exposed[person.Id] = new WorldHealth.Exposure(person.Id, context.SimTime, onset,
                threat.Kind == HealthThreatKind.Biological && random.NextDouble() < 0.35);
        }
    }

    private static void Alert(SimulationContext context, WorldHealth.Threat threat)
    {
        if (threat.Alerted) return;
        var delay = threat.Kind == HealthThreatKind.Infectious ? InfectiousAlertDelay : AlertDelay;
        if (context.SimTime - threat.StartedAt < delay) return;
        threat.Alerted = true;
        var exposed = threat.TotalExposed;
        var estimate = (int)Math.Round(exposed * (0.8 + SimRandom.For(threat.Started.ThreatId, 166).NextDouble() * 0.4) / 10) * 10;
        var window = Health.Window(threat.Kind);
        var advice = threat.Kind switch
        {
            HealthThreatKind.Biological =>
                $"Everyone exposed needs {Health.Countermeasure(threat.Kind)} within {window.TotalHours:F0} hours: open points of dispensing and tell people where. " +
                "It is not passed person to person: isolation and quarantine won't help.",
            HealthThreatKind.Radiological =>
                $"Those exposed should take {Health.Countermeasure(threat.Kind)} within {window.TotalHours:F0} hours, and shelter: open points of dispensing.",
            _ => $"Isolate the cases and quarantine their {threat.Started.Contacts} contacts; {Health.Countermeasure(threat.Kind)} within " +
                 $"{window.TotalHours:F0} hours of exposure protects. Isolation and quarantine orders are ours to make: involve us.",
        };
        CommsNet.Chat(context, PublicHealth, $"{PublicHealth}: {threat.Started.Agent} confirmed {threat.Started.Area}; about {Math.Max(estimate, 10)} people exposed. {advice}",
            new PublicHealthAlert(threat.Started.ThreatId, threat.Kind, threat.Started.Agent, threat.Started.Area, threat.Started.Location, threat.IncidentId,
                Math.Max(estimate, threat.Kind == HealthThreatKind.Infectious ? exposed : 10), advice), TimeSpan.Zero);
    }

    // ---- Illness ----

    private void Illness(SimulationContext context, WorldHealth.Threat threat)
    {
        var world = context.World;
        if (threat.Kind == HealthThreatKind.Biological)
        {
            foreach (var exposure in threat.Exposed.Values.Where(e => e.FallsIll && !e.Protected && !e.Ill && context.SimTime >= e.Onset))
            {
                exposure.Ill = true;
                threat.Ill++;
                threat.Hospitalised++;
                Present(world, threat.Started.Location, 1);
                if (SimRandom.For(threat.Started.ThreatId, exposure.CivilianId + 7).NextDouble() < 0.15)
                    _deaths.Add((threat.Started.ThreatId, context.SimTime + TimeSpan.FromMinutes(90)));
            }
            return;
        }
        if (threat.Kind != HealthThreatKind.Infectious) return;

        var hours = context.Delta.TotalHours;
        var isolating = threat.IsolationAt is { } isolated && context.SimTime - isolated >= TimeSpan.FromMinutes(20);
        if (isolating)
        {
            threat.Isolated += threat.Infectious;
            threat.Infectious = 0;
        }
        var mixing = threat.QuarantineAt is { } quarantined && context.SimTime - quarantined >= TimeSpan.FromMinutes(30) ? 0.2 : 1;
        var contacts = Math.Max(1, threat.Started.Contacts);
        var infections = Math.Min(threat.Susceptible, Transmission * threat.Infectious * mixing * hours * threat.Susceptible / contacts);
        if (infections > 0)
        {
            threat.Susceptible -= infections;
            threat.Incubating.Add((context.SimTime, infections));
        }

        // Incubation over: ill, and infectious unless isolation is in force.
        foreach (var batch in threat.Incubating.Where(b => context.SimTime - b.At >= Incubation).ToList())
        {
            threat.Incubating.Remove(batch);
            if (isolating) threat.Isolated += batch.Count;
            else threat.Infectious += batch.Count;
            _illCarry += batch.Count;
            var whole = (int)Math.Floor(_illCarry);
            _illCarry -= whole;
            threat.Ill += whole;
            var hospitalised = (int)Math.Round(whole * 0.2);
            threat.Hospitalised += hospitalised;
            Present(world, threat.Started.Location, hospitalised);
            _deathCarry += whole * 0.02;
            if (_deathCarry >= 1)
            {
                _deathCarry -= 1;
                _deaths.Add((threat.Started.ThreatId, context.SimTime + TimeSpan.FromMinutes(60)));
            }
        }
    }

    /// <summary>The ill go to the nearest adult emergency department.</summary>
    private static void Present(WorldState world, GeoPoint from, int count)
    {
        if (count <= 0) return;
        if (world.Hospitals.Values.Where(h => world.Health.Accepts(h, null)).MinBy(h => GeoMath.DistanceMeters(h.Location, from)) is { } hospital)
            hospital.Baseline += count;
    }

    private void Deaths(SimulationContext context)
    {
        foreach (var death in _deaths.Where(d => context.SimTime >= d.At).ToList())
        {
            _deaths.Remove(death);
            if (context.World.Health.Threats.TryGetValue(death.Threat, out var threat)) threat.Deaths++;
        }
    }

    // ---- Orders ----

    /// <summary>Isolation and quarantine are a Medical Officer of Health's orders: made without HSE Public Health, they are open to challenge.</summary>
    private void Orders(SimulationContext context, WorldHealth.Threat threat)
    {
        var consulted = context.World.Notified.Keys.Any(k => k.Contains("public health", StringComparison.OrdinalIgnoreCase));
        foreach (var (order, at) in new[] { ("Isolation", threat.IsolationAt), ("Quarantine", threat.QuarantineAt) })
        {
            if (at is null || !_ordersChecked.Add((threat.Started.ThreatId, order)) || consulted) continue;
            context.EmitTruth(new LegalExposure("Public health order",
                $"{order} of people was ordered without HSE Public Health. Under the Health Acts detention is a Medical Officer of Health's order; " +
                "made by command alone it could be challenged.", threat.Started.ThreatId));
        }
    }

    // ---- Mass prophylaxis ----

    private void Dispense(SimulationContext context, WorldHealth.Threat threat)
    {
        var world = context.World;
        var hours = context.Delta.TotalHours;
        var window = Health.Window(threat.Kind);
        foreach (var pod in world.Health.Pods.Values.Where(p => p.Opened.ThreatId == threat.Started.ThreatId))
        {
            // People come: slowly by word of mouth, far faster once command tells them where.
            var informed = world.Health.Statements.Any(s => s.At >= pod.OpenedAt - TimeSpan.FromMinutes(10)
                                                            && (s.Text.Contains(pod.Opened.Name, StringComparison.OrdinalIgnoreCase) || WorldHealth.TellsPeopleToCome(s.Text)));
            var waiting = Unprotected(threat) - pod.Queue - world.Health.Pods.Values.Where(p => p != pod && p.Opened.ThreatId == pod.Opened.ThreatId).Sum(p => p.Queue);
            pod.Arrivals += Math.Max(0, waiting) * (informed ? 0.6 : 0.15) * hours;
            var arriving = Math.Floor(pod.Arrivals);
            pod.Arrivals -= arriving;
            pod.Queue += arriving;

            var ready = context.SimTime - pod.OpenedAt >= PodSetup;
            if (ready)
            {
                _capacity[pod.Opened.PodId] = _capacity.GetValueOrDefault(pod.Opened.PodId) + PodPerHour * hours;
                var wanted = (int)Math.Min(pod.Queue, Math.Floor(_capacity[pod.Opened.PodId]));
                var doses = wanted;
                if (wanted > 0 && world.Logistics.Active)
                    doses = threat.IncidentId is { } incident ? (int)world.Logistics.Draw(incident, SupplyKind.Prophylaxis, wanted, context.SimTime) : 0;
                var outOfStock = doses < wanted;
                _capacity[pod.Opened.PodId] -= doses;
                if (outOfStock) _capacity[pod.Opened.PodId] = 0;
                pod.Queue -= doses;
                pod.Dispensed += doses;
                Protect(context, threat, doses, window);
                if (outOfStock != pod.OutOfStock)
                {
                    pod.OutOfStock = outOfStock;
                    _nextPodReport.Remove(pod.Opened.PodId);
                }
            }

            if (_nextPodReport.TryGetValue(pod.Opened.PodId, out var next) && context.SimTime < next) continue;
            _nextPodReport[pod.Opened.PodId] = context.SimTime + TimeSpan.FromMinutes(10);
            context.EmitPerceived(new DispensingProgress(pod.Opened.PodId, ready, pod.Dispensed, (int)pod.Queue, pod.OutOfStock), EventSources.Comms);
        }
    }

    /// <summary>Who still needs a dose: the exposed not yet protected or ill; for an outbreak, the contacts not yet ill.</summary>
    private static double Unprotected(WorldHealth.Threat threat) => threat.Kind == HealthThreatKind.Infectious
        ? threat.Susceptible + threat.Incubating.Sum(b => b.Count)
        : threat.Exposed.Values.Count(e => !e.Protected && !e.Ill);

    private static void Protect(SimulationContext context, WorldHealth.Threat threat, int doses, TimeSpan window)
    {
        if (doses <= 0) return;
        threat.Protected += doses;
        if (threat.Kind != HealthThreatKind.Infectious)
        {
            foreach (var exposure in threat.Exposed.Values.Where(e => !e.Protected && !e.Ill).OrderBy(e => e.At).Take(doses))
            {
                exposure.Protected = true;
                if (context.SimTime - exposure.At <= window) threat.ProtectedInTime++;
            }
            return;
        }

        // An outbreak: the recently exposed (still in the window) first, then the contacts not yet exposed.
        double left = doses;
        foreach (var batch in threat.Incubating.Where(b => context.SimTime - b.At <= window).ToList())
        {
            var taken = Math.Min(left, batch.Count);
            threat.Incubating.Remove(batch);
            if (batch.Count - taken > 0) threat.Incubating.Add((batch.At, batch.Count - taken));
            threat.Recovered += taken;
            threat.ProtectedInTime += (int)Math.Round(taken);
            left -= taken;
            if (left <= 0) return;
        }
        var fromContacts = Math.Min(left, threat.Susceptible);
        threat.Susceptible -= fromContacts;
        threat.ProtectedInTime += (int)Math.Round(fromContacts);
    }

    // ---- Reports ----

    private void Updates(SimulationContext context, WorldHealth.Threat threat)
    {
        if (!threat.Alerted) return;
        var id = threat.Started.ThreatId;
        if (_nextUpdate.TryGetValue(id, out var next) && context.SimTime < next) return;
        _nextUpdate[id] = context.SimTime + UpdateInterval;
        var exposed = threat.TotalExposed;
        context.EmitPerceived(new HealthThreatUpdate(id, exposed, threat.Ill, threat.Hospitalised, threat.Deaths, threat.Protected), EventSources.Comms);
        context.EmitTruth(new HealthThreatOutcome(id, exposed, threat.Protected, threat.ProtectedInTime, threat.Ill, threat.Deaths));
    }
}
