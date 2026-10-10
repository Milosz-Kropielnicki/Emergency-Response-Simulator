using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Crews;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>Tuning for crews and responder safety (bound from the "Crews" settings section).</summary>
public sealed class CrewOptions
{
    public const string SectionName = "Crews";

    /// <summary>Time a crew spends in rehab before it is fit to go back to work.</summary>
    public TimeSpan RehabTime { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Time for a relief crew to reach a unit at a scene (a few minutes more at random).</summary>
    public TimeSpan ReliefTravelTime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan BriefedHandover { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan QuickHandover { get; set; } = TimeSpan.FromMinutes(1.5);

    /// <summary>Time for the peer support team to reach a crew.</summary>
    public TimeSpan PeerSupportTravelTime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Fatigue per hour for a crew working inside a burning building (breathing apparatus, heat).</summary>
    public double InteriorFatigue { get; set; } = 0.45;

    /// <summary>Fatigue per hour fighting a fire from outside.</summary>
    public double DefensiveFatigue { get; set; } = 0.2;

    /// <summary>Fatigue per hour for other work at a scene: treating casualties, cordons.</summary>
    public double OtherWorkFatigue { get; set; } = 0.12;

    /// <summary>Fatigue recovered per hour in rehab.</summary>
    public double RehabRecovery { get; set; } = 0.8;

    /// <summary>Chance per hour that a fresh crew working inside gets someone into trouble (lost, a fall); tired crews more.</summary>
    public double DistressPerHour { get; set; } = 0.03;

    /// <summary>Burning buildings come down this long after the fire starts, unless it is put out first.</summary>
    public bool Collapses { get; set; } = true;
    public TimeSpan CollapseAfterMin { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan CollapseAfterMax { get; set; } = TimeSpan.FromMinutes(28);

    /// <summary>Crews working inside within this distance of where the fire started are caught by a collapse.</summary>
    public double CollapseRadiusMeters { get; set; } = 60;
}

/// <summary>
/// Responders as people (Design Document §12). Every unit has a named crew with qualifications and a shift.
/// <list type="bullet">
/// <item><b>Fatigue and stress</b> (truth): crews tire with hard work (most inside a burning building), more past the end
/// of their shift, and recover in rehab. Stress comes from what they see and go through: deaths, a collapse, a Mayday.
/// Tired, stressed crews are slower, make more mistakes on the radio and in read-backs, and put out less fire.
/// Officers report it, a little late: "getting tired", "exhausted", "shaken". Too much and people are stood down.</item>
/// <item><b>Shifts, relief and handover</b>: crews at their station change watch at shift end; crews committed carry on
/// until command relieves them. A relief crew with a full briefing takes over cleanly; a quick changeover loses what
/// the old crew knew: its channel, its orders, a blocked road, even that the building was evacuated.</item>
/// <item><b>Accountability</b>: PARs go out on the radio and each crew that hears one counts heads. An evacuation signal
/// (radio and air horns) pulls crews out of the building, and they report a PAR once out.</item>
/// <item><b>Mayday</b>: a burning building collapses some 20–28 minutes in unless the fire is out, after a crew warns of
/// it; crews still inside nearby are caught. Firefighters also get lost or fall, more often when tired. The firefighter
/// calls a Mayday (if their radio gets through); a rescue team sent by command gets them out, faster with USAR
/// training and with the channel cleared for emergency traffic. Left too long, they run out of air.</item>
/// <item><b>Peer support</b>: the team sees a crew once it is off the line, easing stress and preventing stress reactions.</item>
/// </list>
/// </summary>
public sealed class CrewSystem(CrewOptions? options = null) : ISimulationSystem
{
    /// <summary>Crews within this distance of a death at the scene are affected by it.</summary>
    public const double TraumaRadiusMeters = 200;

    private readonly CrewOptions _options = options ?? new CrewOptions();
    private readonly HashSet<Guid> _rosterRequested = [];
    private readonly Dictionary<Guid, CrewCondition> _reported = [];
    private readonly HashSet<Guid> _shaken = [];
    private readonly Dictionary<Guid, string> _lastTrauma = [];
    private readonly Dictionary<Guid, string> _bands = [];
    private readonly HashSet<(Guid Unit, DateTimeOffset ShiftEnd)> _overtimeReported = [];
    private readonly HashSet<(Guid Casualty, Guid Unit)> _deathsSeen = [];
    private readonly HashSet<Guid> _rehabAnnounced = [];
    private readonly HashSet<(Guid Par, Guid Unit)> _hornsChecked = [];
    private readonly Dictionary<Guid, CollapsePlan> _collapses = [];

    private sealed class CollapsePlan(DateTimeOffset at)
    {
        public DateTimeOffset At { get; } = at;
        public bool Warned { get; set; }
        public bool WarnedUrgently { get; set; }
        public bool Done { get; set; }
    }

    public int Order => 16; // after units, casualties and civilians move; before AI agencies and command responses

    public void Update(SimulationContext context)
    {
        var world = context.World;
        Roster(context);

        var fires = world.Hazards.Values.Where(h => !h.Ended && h.Model is FireSpreadModel).Select(h => (FireSpreadModel)h.Model!).ToList();
        foreach (var unit in world.Units.Values.Where(u => u.Crew.Rostered))
        {
            Track(context, unit);
            Tire(context, unit, fires);
            Strain(context, unit);
            StandDowns(context, unit);
            Bands(context, unit);
            Conditions(context, unit);
        }

        Rehab(context);
        Reliefs(context);
        ShiftChanges(context);
        PeerSupport(context);
        Deaths(context);
        Collapses(context);
        Mishaps(context, fires);
        Distress(context);
        Pars(context);
    }

    private static bool AiRun(WorldState world, WorldUnit unit) =>
        unit.AgencyId is { } agency && world.Agencies.GetValueOrDefault(agency)?.AiControlled == true;

    private static bool AtScene(WorldUnit unit) => unit.Phase is ResponsePhase.OnScene or ResponsePhase.Operating or ResponsePhase.Rehab;

    /// <summary>A number in [0, 1) that stays the same for a person, so some tire faster than others.</summary>
    private static double Constitution(WorldResponder member) => (uint)member.Id.GetHashCode() % 1000 / 1000.0;

    // ---- Roster ----

    /// <summary>Units that arrive without a crew list (mutual aid, relief appliances) get one.</summary>
    private void Roster(SimulationContext context)
    {
        foreach (var unit in context.World.Units.Values.Where(u => !u.Crew.Rostered && _rosterRequested.Add(u.Id)))
        {
            var members = CrewRoster.Generate(unit.Callsign, unit.Type, unit.CrewSize, unit.Capabilities.ToList());
            context.EmitPerceived(new CrewRostered(unit.Id, members, CrewRoster.DefaultOnShift(unit.Callsign, unit.Type),
                CrewRoster.ShiftLength(unit.Type)), EventSources.Engine);
        }
    }

    // ---- Fatigue and stress ----

    /// <summary>Keeps track of when the crew started working, and whether it is working from outside.</summary>
    private static void Track(SimulationContext context, WorldUnit unit)
    {
        var crew = unit.Crew;
        if (!AtScene(unit))
        {
            crew.WorkingSince = null;
            crew.RehabSince = null;
            crew.Withdrawn = false;
            crew.UnawareOfEvacuation = false;
            return;
        }
        if (unit.Phase == ResponsePhase.Operating)
            crew.WorkingSince ??= context.SimTime;
        // Crews arriving at an incident that has gone defensive are told so by the incident commander.
        if (unit.Phase == ResponsePhase.Operating && !crew.Withdrawn && !crew.UnawareOfEvacuation
            && unit.OrderedIncidentId is { } incident && context.World.DefensiveIncidents.Contains(incident))
            crew.Withdrawn = true;
    }

    private void Tire(SimulationContext context, WorldUnit unit, List<FireSpreadModel> fires)
    {
        var crew = unit.Crew;
        var fireCrew = CrewFactors.IsFireCrew(unit.Type);
        var nearFire = fireCrew && fires.Any(f => f.DistanceToFire(unit.Location) <= HazardSystem.HoseReachMeters);
        double rate = unit.Phase switch
        {
            ResponsePhase.Rehab => -_options.RehabRecovery,
            _ when crew.Rescuing is not null => _options.InteriorFatigue,
            ResponsePhase.Operating when nearFire => crew.Withdrawn ? _options.DefensiveFatigue : _options.InteriorFatigue,
            ResponsePhase.Operating => _options.OtherWorkFatigue,
            ResponsePhase.Idle => 0.01,
            _ => 0.02,
        };
        var overtime = context.SimTime >= crew.ShiftEnd ? 1.5 : 1;
        var hours = context.Delta.TotalHours;
        foreach (var member in crew.Available)
        {
            var personal = rate;
            // The driver stays at the pump.
            if (rate > _options.OtherWorkFatigue && member.Role == CrewRole.Driver) personal = _options.OtherWorkFatigue;
            var change = personal > 0 ? personal * overtime * (0.8 + 0.4 * Constitution(member)) : personal;
            member.Fatigue = Math.Clamp(member.Fatigue + change * hours, 0.02, 1);
        }
    }

    private static void Strain(SimulationContext context, WorldUnit unit)
    {
        var rate = unit.Phase == ResponsePhase.Rehab ? -0.2 : AtScene(unit) ? -0.04 : -0.1;
        if (unit.Phase == ResponsePhase.Operating
            && HazardSystem.ExposuresAt(context.World, unit.Location).Any(e => e.Exposure.Level >= HazardLevel.Moderate))
            rate += 0.15; // working in heat, smoke or fumes
        var hours = context.Delta.TotalHours;
        foreach (var member in unit.Crew.Available)
            member.Stress = Math.Clamp(member.Stress + rate * hours, 0, 1);
    }

    /// <summary>
    /// Something traumatic: everyone on the crew feels it, some more than others. Each new shock adds less to someone
    /// already shaken, so it takes a lot to push a crew to breaking point.
    /// </summary>
    private void Bump(WorldUnit unit, double amount, string what)
    {
        foreach (var member in unit.Crew.Available)
        {
            member.Stress = Math.Clamp(member.Stress + amount * (0.8 + 0.4 * Constitution(member)) * (1 - member.Stress), 0, 1);
            member.TraumaLoad += amount;
        }
        _lastTrauma[unit.Id] = what;
    }

    /// <summary>Everyone working at the same incident as this unit.</summary>
    private static IEnumerable<WorldUnit> Colleagues(WorldState world, WorldUnit unit) =>
        world.Units.Values.Where(u => u != unit && u.Crew.Rostered && AtScene(u) && u.OrderedIncidentId == unit.OrderedIncidentId);

    /// <summary>People who can't go on: spent, or an acute stress reaction (unless the stress eases first).</summary>
    private void StandDowns(SimulationContext context, WorldUnit unit)
    {
        foreach (var member in unit.Crew.Available.ToList())
        {
            if (member.Fatigue >= 0.97)
            {
                StandDown(context, unit, member, "is spent and can't carry on");
                continue;
            }
            if (member.Stress >= 0.85 && member.StandDownAt is null)
                member.StandDownAt = context.SimTime + TimeSpan.FromMinutes(5 + 10 * SimRandom.For(member.Id, 13).NextDouble());
            else if (member.Stress < 0.7)
                member.StandDownAt = null;
            if (member.StandDownAt is { } at && context.SimTime >= at)
                StandDown(context, unit, member, "has taken it badly (acute stress reaction)");
        }
    }

    private static void StandDown(SimulationContext context, WorldUnit unit, WorldResponder member, string reason)
    {
        member.State = ResponderState.StoodDown;
        member.StandDownAt = null;
        if (AiRun(context.World, unit)) return;
        var text = $"Control, {unit.Callsign}: {member.Title} {reason}; we've stood them down. Crew of {unit.Crew.OnDuty} now.";
        CommsNet.Voice(context, unit, text, new CrewMemberStoodDown(unit.Id, member.Id, $"{member.Title} {reason}"), priority: 2);
    }

    /// <summary>Records for the instructor when a crew really moves from fresh to tired to exhausted.</summary>
    private void Bands(SimulationContext context, WorldUnit unit)
    {
        var band = CrewFactors.Band(unit.Crew.Fatigue);
        if (_bands.TryGetValue(unit.Id, out var previous) && previous != band)
            context.EmitTruth(new CrewWelfareChanged(unit.Id, unit.Crew.Fatigue, unit.Crew.Stress, band));
        _bands[unit.Id] = band;
    }

    /// <summary>
    /// What the officer tells command. Crews report tiredness later than it sets in (at 0.6, not 0.5), exhaustion at
    /// 0.85, being shaken once after a traumatic event, and that they are well past the end of their shift.
    /// </summary>
    private void Conditions(SimulationContext context, WorldUnit unit)
    {
        var world = context.World;
        if (AiRun(world, unit) || unit.OrderedIncidentId is null || world.AgencyTasks.ContainsKey(unit.OrderedIncidentId.Value)) return;
        var crew = unit.Crew;
        var reported = _reported.GetValueOrDefault(unit.Id, CrewCondition.Fine);

        if (crew.Fatigue >= 0.85 && reported < CrewCondition.Exhausted)
            Say(CrewCondition.Exhausted, "crew's exhausted. We need relief or rehab now.");
        else if (crew.Fatigue >= 0.6 && reported < CrewCondition.Tired)
            Say(CrewCondition.Tired, "crew's getting tired, maybe twenty minutes left in us before we need a break.");
        else if (crew.Stress >= 0.5 && _shaken.Add(unit.Id))
        {
            var after = _lastTrauma.TryGetValue(unit.Id, out var what) ? $" after {what}" : "";
            Report(CrewCondition.Shaken, $"crew's a bit shaken{after}. We're keeping going.");
        }
        else if (context.SimTime >= crew.ShiftEnd + TimeSpan.FromMinutes(30) && _overtimeReported.Add((unit.Id, crew.ShiftEnd)))
            Report(CrewCondition.Tired, $"we're {(context.SimTime - crew.ShiftEnd).TotalMinutes:F0} minutes past the end of our shift. Request relief when you can.");

        void Say(CrewCondition condition, string note)
        {
            _reported[unit.Id] = condition;
            Report(condition, note);
        }

        void Report(CrewCondition condition, string note) =>
            CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: {note}", new CrewConditionReported(unit.Id, condition, note), priority: 1);
    }

    /// <summary>Crews close to a death at the scene carry it with them.</summary>
    private void Deaths(SimulationContext context)
    {
        var world = context.World;
        foreach (var casualty in world.Casualties.Values.Where(c => c.State == CasualtyState.Deceased))
        {
            foreach (var unit in world.Units.Values.Where(u => u.Crew.Rostered && AtScene(u)
                                                               && GeoMath.DistanceMeters(u.Location, casualty.Location) <= TraumaRadiusMeters))
            {
                if (_deathsSeen.Add((casualty.Id, unit.Id)))
                    Bump(unit, 0.15, "the fatality");
            }
        }
    }

    // ---- Rehab ----

    private void Rehab(SimulationContext context)
    {
        var world = context.World;
        foreach (var unit in world.Units.Values.Where(u => u.Crew.Rostered))
        {
            var crew = unit.Crew;

            // Other services rotate their own crews.
            if (AiRun(world, unit) && unit.Phase == ResponsePhase.Operating && crew.WorkingSince is { } working
                && context.SimTime - working >= TimeSpan.FromMinutes(45))
            {
                unit.Phase = ResponsePhase.Rehab;
                unit.PhaseStartedAt = context.SimTime;
                crew.RehabSince = context.SimTime;
            }
            if (unit.Phase != ResponsePhase.Rehab) continue;

            crew.RehabSince ??= unit.PhaseStartedAt;
            if (!AiRun(world, unit) && _rehabAnnounced.Add(unit.Id))
            {
                CommsNet.Data(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OnScene), EventSources.Comms);
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: copy, going to rehab.", null);
            }
            if (context.SimTime - crew.RehabSince < _options.RehabTime) continue;

            // The rehab medic checks everyone over before they go back.
            foreach (var member in crew.Available.Where(m => m.Fatigue >= 0.75).ToList())
                StandDown(context, unit, member, "has been kept back by the rehab medic (heat exhaustion)");

            crew.RehabSince = null;
            crew.WorkingSince = context.SimTime;
            unit.Phase = unit.OrderedIncidentId is null ? ResponsePhase.Idle : ResponsePhase.Operating;
            unit.PhaseStartedAt = context.SimTime;
            _reported.Remove(unit.Id);
            _rehabAnnounced.Remove(unit.Id);
            if (AiRun(world, unit)) continue;

            var note = $"rehab complete, crew of {crew.OnDuty} fit and ready to go back to work.";
            CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: {note}", new CrewRehabEnded(unit.Id, note), priority: 1);
            if (unit.Phase == ResponsePhase.Operating)
                CommsNet.Data(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.Operating), EventSources.Comms);
        }
    }

    // ---- Shifts, relief and handover ----

    private void Reliefs(SimulationContext context)
    {
        var world = context.World;
        foreach (var relief in world.Reliefs.Values.ToList())
        {
            if (!world.Units.TryGetValue(relief.UnitId, out var unit) || !unit.Crew.Rostered)
            {
                world.Reliefs.Remove(relief.Id);
                continue;
            }
            var crew = unit.Crew;
            relief.ArriveAt ??= relief.RequestedAt + (unit.Phase == ResponsePhase.Idle && GeoMath.DistanceMeters(unit.Location, unit.Home) < 100
                ? TimeSpan.FromMinutes(2)
                : _options.ReliefTravelTime + TimeSpan.FromMinutes(4 * SimRandom.For(relief.Id).NextDouble()));
            if (context.SimTime < relief.ArriveAt) continue;

            if (crew.HandoverUntil is null)
            {
                crew.HandoverUntil = context.SimTime + (relief.Briefing ? _options.BriefedHandover : _options.QuickHandover) * CrewFactors.SlowFactor(unit);
                continue;
            }
            if (context.SimTime < crew.HandoverUntil) continue;

            world.Reliefs.Remove(relief.Id);
            Relieve(context, unit, relief.Id, relief.Briefing, announce: true);
        }
    }

    /// <summary>Crews at their station change watch at the end of their shift; committed crews carry on until relieved.</summary>
    private void ShiftChanges(SimulationContext context)
    {
        var world = context.World;
        foreach (var unit in world.Units.Values.Where(u => u.Crew.Rostered && u.Phase == ResponsePhase.Idle && context.SimTime >= u.Crew.ShiftEnd
                                                           && u.OrderedIncidentId is null && !world.Reliefs.Values.Any(r => r.UnitId == u.Id)))
        {
            Relieve(context, unit, Guid.NewGuid(), briefed: true, announce: false);
        }
    }

    /// <summary>
    /// The new crew takes over. A rushed changeover loses what only the old crew knew: the channel it had been moved
    /// to, the orders it had (the new crew says so only when asked), a blocked road, that the building was evacuated.
    /// </summary>
    private void Relieve(SimulationContext context, WorldUnit unit, Guid reliefId, bool briefed, bool announce)
    {
        var world = context.World;
        var crew = unit.Crew;
        crew.Reliefs++;
        var members = CrewRoster.Generate(unit.Callsign, unit.Type, unit.CrewSize, unit.Capabilities.ToList(), salt: crew.Reliefs);

        var lost = new List<string>();
        if (!briefed)
        {
            var random = SimRandom.For(reliefId, 17);
            if (unit.KnownObstructions.Count > 0)
            {
                unit.KnownObstructions.Clear();
                lost.Add("where the road was blocked");
            }
            var normal = RadioPlan.DefaultChannel(unit.Type, unit.AgencyId is { } agency ? world.Agencies.GetValueOrDefault(agency)?.RadioChannel : null);
            if (unit.Channel != normal)
            {
                lost.Add($"to work on {unit.Channel}");
                unit.Channel = normal;
            }
            foreach (var task in crew.Tasks.ToList())
            {
                if (random.NextDouble() >= 0.6) continue;
                crew.Tasks.Remove(task);
                crew.ForgottenTasks.Add(task);
                lost.Add($"the order \"{task}\"");
            }
            if (crew.Withdrawn && random.NextDouble() < 0.5)
            {
                crew.Withdrawn = false;
                crew.UnawareOfEvacuation = true;
                lost.Add("that the building had been evacuated and operations are defensive");
            }
            if (lost.Count > 0)
                context.EmitTruth(new HandoverInformationLost(unit.Id, lost));
        }
        else
        {
            crew.ForgottenTasks.Clear();
        }

        var length = CrewRoster.ShiftLength(unit.Type);
        crew.Roster(members, context.SimTime, length, context.SimTime);
        crew.RehabSince = null;
        crew.WorkingSince = unit.Phase == ResponsePhase.Operating ? context.SimTime : null;
        _reported.Remove(unit.Id);
        _shaken.Remove(unit.Id);
        _lastTrauma.Remove(unit.Id);

        var relieved = new CrewRelieved(reliefId, unit.Id, members, length, briefed);
        if (!announce || AiRun(world, unit))
        {
            context.EmitPerceived(relieved, EventSources.Engine);
            return;
        }
        var officer = members.FirstOrDefault(m => m.Role is CrewRole.Officer or CrewRole.Paramedic) ?? members[0];
        var control = unit.AgencyId is { } owner && world.Agencies.TryGetValue(owner, out var agencyInfo) ? $"{agencyInfo.Name} rostering" : "Rostering";
        CommsNet.Chat(context, control,
            $"{unit.Callsign}: relief crew in place, {WorldResponder.RoleTitle(officer.Role)} {officer.Name.Split(' ')[^1]} in charge, crew of {members.Count}. " +
            (briefed ? "Full handover briefing given." : "Quick changeover, no formal briefing."),
            relieved, TimeSpan.FromSeconds(10));
    }

    // ---- Peer support ----

    private void PeerSupport(SimulationContext context)
    {
        foreach (var unit in context.World.Units.Values.Where(u => u.Crew.PeerSupportArrangedAt is not null))
        {
            var crew = unit.Crew;
            if (context.SimTime - crew.PeerSupportArrangedAt < _options.PeerSupportTravelTime) continue;
            // They can't sit down with a crew that is still working: they wait until it comes off the line.
            if (unit.Phase == ResponsePhase.Operating || crew.Rescuing is not null) continue;

            foreach (var member in crew.Available)
            {
                member.Stress = Math.Max(0, member.Stress - 0.35);
                member.TraumaLoad *= 0.4;
                member.StandDownAt = null;
            }
            crew.PeerSupportArrangedAt = null;
            _shaken.Remove(unit.Id);
            var after = _lastTrauma.TryGetValue(unit.Id, out var what) ? $" about {what}" : "";
            var note = $"We've sat down with {unit.Callsign}'s crew{after}. They're coping; we'll follow up with each of them after the shift.";
            CommsNet.Chat(context, "Peer support team", note, new PeerSupportGiven(unit.Id, note), TimeSpan.Zero);
        }
    }

    // ---- Collapse and other mishaps ----

    private void Collapses(SimulationContext context)
    {
        if (!_options.Collapses) return;
        var world = context.World;
        foreach (var hazard in world.Hazards.Values.Where(h => h.Kind == HazardKind.Fire && h.IncidentId is not null))
        {
            if (!_collapses.TryGetValue(hazard.Id, out var plan))
            {
                var span = _options.CollapseAfterMax - _options.CollapseAfterMin;
                _collapses[hazard.Id] = plan = new CollapsePlan(hazard.StartedAt + _options.CollapseAfterMin + span * SimRandom.For(hazard.Id, 31).NextDouble());
            }
            if (plan.Done) continue;
            if (hazard.Ended || hazard.Model is not FireSpreadModel { IsActive: true })
            {
                plan.Done = hazard.Ended;
                continue;
            }

            // Someone has to be there to see it coming; as it gets worse they say so again, urgently.
            if (!plan.Warned && context.SimTime >= plan.At - TimeSpan.FromMinutes(4))
                plan.Warned = Warn(context, hazard, urgent: false);
            if (!plan.WarnedUrgently && context.SimTime >= plan.At - TimeSpan.FromMinutes(1.5))
                plan.WarnedUrgently = Warn(context, hazard, urgent: true);
            if (context.SimTime >= plan.At)
            {
                plan.Done = true;
                Collapse(context, hazard);
            }
        }
    }

    /// <summary>The crew nearest the fire sees the signs and says so: the trainee's chance to pull everyone out.</summary>
    private static bool Warn(SimulationContext context, WorldHazard hazard, bool urgent)
    {
        var world = context.World;
        var witness = world.Units.Values
            .Where(u => u.Phase == ResponsePhase.Operating && CrewFactors.IsFireCrew(u.Type) && !AiRun(world, u)
                        && GeoMath.DistanceMeters(u.Location, hazard.Origin) <= 150 && CommsNet.ReachOf(world, u) != Reach.None)
            .OrderBy(u => u.Crew.Withdrawn).ThenBy(u => GeoMath.DistanceMeters(u.Location, hazard.Origin))
            .FirstOrDefault();
        if (witness is null) return false;
        CommsNet.Report(context, witness, new ReportReceived(Guid.NewGuid(), witness.OrderedIncidentId, ReportSource.FieldUnit, witness.Callsign,
            urgent
                ? "Roof's going! Cracking and bulging at the rear wall. Get everyone out now!"
                : "Roof's sagging at the rear and there's heavy fire through it. The building is going: recommend we pull everyone out.",
            Confidence.High, VerificationStatus.Reported, hazard.Origin, 30, witness.Id), priority: urgent ? CommsSystem.EmergencyPriority : 5);
        return true;
    }

    private void Collapse(SimulationContext context, WorldHazard hazard)
    {
        var world = context.World;
        context.EmitTruth(new StructureCollapsed(hazard.Id, hazard.Origin, "part of the roof and the rear wall come down"));
        context.EmitTruth(new CascadeOccurred("Fire weakened the structure", "Roof and rear wall collapse", HazardId: hazard.Id));

        var inside = world.Units.Values
            .Where(u => u.Phase == ResponsePhase.Operating && u.Crew.Rostered && CrewFactors.IsFireCrew(u.Type) && !u.Crew.Withdrawn
                        && u.Crew.Rescuing is null && GeoMath.DistanceMeters(u.Location, hazard.Origin) <= _options.CollapseRadiusMeters)
            .OrderBy(u => GeoMath.DistanceMeters(u.Location, hazard.Origin))
            .ToList();
        var random = SimRandom.For(hazard.Id, 37);
        foreach (var unit in inside)
        {
            var exposed = unit.Crew.Available.Where(m => m.Role != CrewRole.Driver).ToList();
            if (exposed.Count == 0) continue;
            // Someone on the crew nearest the fire is always caught; others with bad luck.
            var sure = unit == inside[0] ? exposed[random.Next(exposed.Count)] : null;
            foreach (var member in exposed.Where(m => m == sure || random.NextDouble() < 0.15))
                StartDistress(context, unit, member, "trapped by the collapse", trapped: true);
        }

        // Everyone close by pulls back and feels it.
        foreach (var unit in world.Units.Values.Where(u => u.Crew.Rostered && AtScene(u) && GeoMath.DistanceMeters(u.Location, hazard.Origin) <= 150))
        {
            Bump(unit, 0.25, "the collapse");
            if (CrewFactors.IsFireCrew(unit.Type)) unit.Crew.Withdrawn = true;
        }

        var reporter = world.Units.Values
            .Where(u => AtScene(u) && !AiRun(world, u) && GeoMath.DistanceMeters(u.Location, hazard.Origin) <= 250 && CommsNet.ReachOf(world, u) == Reach.Good)
            .OrderBy(u => GeoMath.DistanceMeters(u.Location, hazard.Origin))
            .FirstOrDefault();
        if (reporter is not null)
        {
            CommsNet.Report(context, reporter, new ReportReceived(Guid.NewGuid(), reporter.OrderedIncidentId, ReportSource.FieldUnit, reporter.Callsign,
                "Collapse! Part of the roof and the rear wall have come down.", Confidence.High, VerificationStatus.Confirmed,
                hazard.Origin, 30, reporter.Id), priority: 7);
        }
    }

    /// <summary>Firefighters working inside sometimes get into trouble by themselves; tired ones more often.</summary>
    private void Mishaps(SimulationContext context, List<FireSpreadModel> fires)
    {
        if (_options.DistressPerHour <= 0 || fires.Count == 0) return;
        var world = context.World;
        var tick = (int)(context.SimTime.ToUnixTimeMilliseconds() / 1000);
        foreach (var unit in world.Units.Values.Where(u => u.Phase == ResponsePhase.Operating && u.Crew.Rostered && CrewFactors.IsFireCrew(u.Type)
                                                           && !u.Crew.Withdrawn && u.Crew.Rescuing is null && !AiRun(world, u)
                                                           && fires.Any(f => f.DistanceToFire(u.Location) <= HazardSystem.HoseReachMeters)).ToList())
        {
            var chance = _options.DistressPerHour * (1 + 3 * unit.Crew.Fatigue) * context.Delta.TotalHours;
            var random = SimRandom.For(unit.Id, tick);
            if (random.NextDouble() >= chance) continue;
            var exposed = unit.Crew.Available.Where(m => m.Role != CrewRole.Driver).ToList();
            if (exposed.Count == 0) continue;
            var (cause, trapped) = random.Next(3) switch
            {
                0 => ("separated from their partner and lost in the smoke", false),
                1 => ("fell through a weakened floor", true),
                _ => ("low on air and can't find the way out", false),
            };
            StartDistress(context, unit, exposed[random.Next(exposed.Count)], cause, trapped);
        }
    }

    private void StartDistress(SimulationContext context, WorldUnit unit, WorldResponder member, string cause, bool trapped)
    {
        var world = context.World;
        var id = Guid.NewGuid();
        var random = SimRandom.For(id);
        var air = 8 + random.NextDouble() * 10;
        var location = GeoMath.Destination(unit.Location, random.NextDouble() * 360, 10 + random.NextDouble() * 25);
        member.State = trapped ? ResponderState.Trapped : ResponderState.Lost;
        world.Distress[id] = new WorldDistress
        {
            Id = id, UnitId = unit.Id, MemberId = member.Id, Cause = cause, Location = location, Trapped = trapped,
            StartedAt = context.SimTime, AirRunsOutAt = context.SimTime + TimeSpan.FromMinutes(air),
        };
        context.EmitTruth(new FirefighterInDistress(id, unit.Id, member.Id, cause, location, air));

        Bump(unit, 0.35, $"{member.Title}'s Mayday");
        foreach (var colleague in Colleagues(world, unit))
            Bump(colleague, 0.2, $"{member.Title}'s Mayday");
    }

    // ---- Mayday and rescue ----

    private void Distress(SimulationContext context)
    {
        var world = context.World;
        var now = context.SimTime;
        foreach (var distress in world.Distress.Values.Where(d => !d.Ended).ToList())
        {
            if (!world.Units.TryGetValue(distress.UnitId, out var unit)) continue;
            var member = unit.Crew.Members.FirstOrDefault(m => m.Id == distress.MemberId);
            if (member is null)
            {
                distress.Ended = true; // the crew changed: nothing left to track
                continue;
            }

            // The firefighter (or their partner) keeps calling until command hears, while they still have air.
            if (!distress.Heard && now < distress.AirRunsOutAt && distress.Calls < 4 && now - distress.StartedAt >= TimeSpan.FromSeconds(15)
                && (distress.LastCallAt is null || now - distress.LastCallAt >= TimeSpan.FromSeconds(75)))
                Call(context, unit, member, distress);

            if (distress.RescueUnitId is { } rescueId && world.Units.TryGetValue(rescueId, out var rescuer) && AtScene(rescuer))
            {
                if (rescuer.Phase == ResponsePhase.Rehab)
                {
                    // Pulled out of rehab to go in.
                    rescuer.Phase = ResponsePhase.Operating;
                    rescuer.PhaseStartedAt = now;
                    rescuer.Crew.RehabSince = null;
                }
                rescuer.Crew.Rescuing = distress.Id;
                distress.RescueProgress += context.Delta.TotalSeconds / RescueTime(world, distress, rescuer, unit).TotalSeconds;
                if (distress.RescueProgress >= 1)
                {
                    End(context, distress, unit, member, rescuer, now <= distress.AirRunsOutAt ? Outcome.Rescued : Outcome.RescuedLate);
                    continue;
                }
            }

            // Someone lost (not trapped) may find their own way out.
            if (!distress.Trapped && distress.RescueUnitId is null)
            {
                var random = SimRandom.For(distress.Id, 5);
                var at = distress.StartedAt + TimeSpan.FromMinutes(4 + 6 * random.NextDouble());
                if (random.NextDouble() < 0.55 && now >= at && now < distress.AirRunsOutAt)
                {
                    End(context, distress, unit, member, null, Outcome.FoundOwnWay);
                    continue;
                }
            }

            // Nobody sent in: another crew eventually stumbles on them, long after their air has gone.
            if (distress.RescueUnitId is null && now >= distress.AirRunsOutAt + TimeSpan.FromMinutes(8))
                End(context, distress, unit, member, null, Outcome.FoundLate);
        }
    }

    /// <summary>
    /// How long it takes to get them out: about 9 minutes for someone trapped, 5 for someone lost; quicker with USAR
    /// training for a collapse, slower for a short or tired crew, and slower when the channel is full of routine
    /// traffic because nobody declared emergency traffic.
    /// </summary>
    private static TimeSpan RescueTime(WorldState world, WorldDistress distress, WorldUnit rescuer, WorldUnit victim)
    {
        var minutes = distress.Trapped ? 9.0 : 5.0;
        if (distress.Trapped && rescuer.Crew.Holding(Qualifications.Usar) >= 2) minutes *= 0.6;
        if (rescuer.Crew.Rostered && rescuer.Crew.OnDuty < 3) minutes *= 1.4;
        minutes *= CrewFactors.SlowFactor(rescuer);
        if (world.Radio.Active && !world.Radio.UnderEmergencyTraffic(victim.Channel)) minutes *= 1.25;
        return TimeSpan.FromMinutes(minutes);
    }

    private static void Call(SimulationContext context, WorldUnit unit, WorldResponder member, WorldDistress distress)
    {
        distress.Calls++;
        distress.LastCallAt = context.SimTime;
        var air = Math.Max(0, (distress.AirRunsOutAt - context.SimTime).TotalMinutes);
        var bearing = GeoMath.CompassPoint(GeoMath.BearingDegrees(unit.Location, distress.Location));
        var where = $"about {GeoMath.DistanceMeters(unit.Location, distress.Location):F0} metres {bearing} of the appliance";
        var text = $"MAYDAY MAYDAY MAYDAY. {member.Title}, {unit.Callsign}. {char.ToUpperInvariant(distress.Cause[0])}{distress.Cause[1..]}, {where}. " +
                   $"{air:F0} minutes of air.";
        CommsNet.Voice(context, unit, text,
            new MaydayDeclared(distress.Id, unit.Id, member.Title, $"{member.Title} ({unit.Callsign}): {distress.Cause}, {where}, {air:F0} min of air"),
            priority: 10);
    }

    private enum Outcome
    {
        Rescued,
        RescuedLate,
        FoundOwnWay,
        FoundLate,
    }

    private void End(SimulationContext context, WorldDistress distress, WorldUnit unit, WorldResponder member, WorldUnit? rescuer, Outcome how)
    {
        var world = context.World;
        distress.Ended = true;
        // Still busy if it is getting someone else out.
        if (rescuer is not null)
            rescuer.Crew.Rescuing = world.Distress.Values.FirstOrDefault(d => !d.Ended && d.RescueUnitId == rescuer.Id)?.Id;

        Triage? hurt = null;
        string outcome;
        switch (how)
        {
            case Outcome.Rescued when SimRandom.For(distress.Id, 7).NextDouble() < 0.5:
                outcome = $"{member.Title} is out, conscious with burns to the hands. Handing over to the paramedics.";
                hurt = Triage.Urgent;
                break;
            case Outcome.Rescued:
                outcome = $"{member.Title} is out, shaken but unhurt. Stood down for a medical check.";
                member.State = ResponderState.StoodDown;
                break;
            case Outcome.RescuedLate:
                outcome = $"{member.Title} is out but unconscious, out of air. Priority 1, paramedics to the front.";
                hurt = Triage.Immediate;
                break;
            case Outcome.FoundOwnWay:
                outcome = $"{member.Title} has found their own way out and is accounted for.";
                member.State = ResponderState.OnDuty;
                break;
            default:
                outcome = $"{member.Title} found by another crew, unconscious and out of air. Priority 1, paramedics to the front.";
                hurt = Triage.Immediate;
                break;
        }

        if (hurt is { } triage)
        {
            member.State = ResponderState.Injured;
            var incident = world.Incidents.Values.Where(i => GeoMath.DistanceMeters(i.Location, distress.Location) <= 500)
                .OrderBy(i => GeoMath.DistanceMeters(i.Location, distress.Location)).FirstOrDefault();
            var injured = new CasualtyInjured(Guid.NewGuid(), incident?.Id, distress.Location, triage, $"firefighter, {distress.Cause}");
            world.Casualties[injured.CasualtyId] = new WorldCasualty
            {
                Id = injured.CasualtyId, IncidentId = incident?.Id, Location = distress.Location, Triage = triage,
                State = CasualtyState.AwaitingTreatment, Cause = injured.Cause, InjuredAt = context.SimTime,
            };
            context.EmitTruth(injured);
        }
        context.EmitTruth(new DistressEnded(distress.Id, outcome));

        if (how is Outcome.RescuedLate or Outcome.FoundLate)
        {
            Bump(unit, 0.3, $"what happened to {member.Title}");
            foreach (var colleague in Colleagues(world, unit))
                Bump(colleague, 0.25, $"what happened to {member.Title}");
        }

        var reporter = rescuer ?? unit;
        var text = $"Control, {reporter.Callsign}: {(distress.MaydayIds.Count > 0 ? "Mayday resolved. " : "")}{outcome}";
        DomainEvent payload = distress.MaydayIds.FirstOrDefault() is var mayday && mayday != Guid.Empty
            ? new MaydayResolved(mayday, outcome)
            // Command never heard the Mayday: the crew still tells it what happened.
            : new ReportReceived(Guid.NewGuid(), reporter.OrderedIncidentId, ReportSource.FieldUnit, reporter.Callsign,
                $"{member.Title} ({unit.Callsign}) was {distress.Cause}. {outcome}", Confidence.High, VerificationStatus.Confirmed,
                distress.Location, 20, reporter.Id);
        CommsNet.Voice(context, reporter, text, payload, priority: 9);
        foreach (var other in distress.MaydayIds.Skip(1))
            context.EmitPerceived(new MaydayResolved(other, outcome), EventSources.Comms);
    }

    // ---- Accountability ----

    /// <summary>Crews that heard a PAR count heads and answer; on an evacuation signal they withdraw first.</summary>
    private void Pars(SimulationContext context)
    {
        var world = context.World;
        var now = context.SimTime;
        foreach (var par in world.Pars.Values.Where(p => p.Answered.Count < p.Involved.Count && now - p.RequestedAt <= TimeSpan.FromMinutes(15)))
        {
            foreach (var unitId in par.Involved.Where(id => !par.Answered.Contains(id)).ToList())
            {
                if (!world.Units.TryGetValue(unitId, out var unit)) continue;
                if (!par.HeardBy.TryGetValue(unitId, out var heardAt))
                {
                    // Air horns reach some crews the radio didn't.
                    if (!par.Evacuation || now - par.RequestedAt < TimeSpan.FromSeconds(20) || !_hornsChecked.Add((par.Id, unitId))
                        || SimRandom.For(par.Id, unitId.GetHashCode()).NextDouble() >= 0.65)
                        continue;
                    par.HeardBy[unitId] = heardAt = now;
                }

                if (par.Evacuation && CrewFactors.IsFireCrew(unit.Type))
                {
                    unit.Crew.Withdrawn = true;
                    unit.Crew.UnawareOfEvacuation = false;
                }

                var pause = (par.Evacuation ? 75 : 10) + 20 * SimRandom.For(par.Id, unitId.GetHashCode() ^ 3).NextDouble();
                if (now < heardAt + TimeSpan.FromSeconds(pause * CrewFactors.SlowFactor(unit))) continue;

                par.Answered.Add(unitId);
                Answer(context, par, unit);
            }
        }
    }

    private static void Answer(SimulationContext context, WorldPar par, WorldUnit unit)
    {
        var crew = unit.Crew;
        var expected = crew.Members.Count(m => m.State is ResponderState.OnDuty or ResponderState.Trapped or ResponderState.Lost);
        var missing = crew.Members.Where(m => m.State is ResponderState.Trapped or ResponderState.Lost).Select(m => m.Title).ToList();
        var accounted = expected - missing.Count;
        var text = missing.Count == 0
            ? $"Control, {unit.Callsign}: PAR, {accounted} of {expected}{(par.Evacuation ? ", all out" : "")}."
            : $"Control, {unit.Callsign}: PAR, {accounted} of {expected}. Missing {string.Join(" and ", missing)}!";
        CommsNet.Voice(context, unit, text, new ParReported(par.Id, unit.Id, accounted, expected, missing),
            priority: par.Evacuation || missing.Count > 0 ? CommsSystem.EmergencyPriority : 6);
    }
}
