using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Political pressure (Phase 11 M5): people in public life ring command about a serious incident, as they do.
/// <list type="bullet">
/// <item><b>Who</b>: the local TD first, then the council's chief executive, then the minister's office. They ask for the
/// Incident Commander, or the PIO once command has one.</item>
/// <item><b>How often</b>: by what command has said about the incident: sooner and more often for a Critical incident than
/// a Medium one (Low ones don't make the news), more so with casualties or people told to evacuate or shelter.</item>
/// <item><b>Ignored</b>: a call nobody returns within <see cref="AnswerWindow"/> moves the pressure up: the next office
/// rings, knowing the last call wasn't returned. Ignore the minister's office and the minister goes public: a media
/// report and an alert, and command loses the chance to shape the story.</item>
/// <item><b>Answered</b>: the pressure eases a step and the next call comes later.</item>
/// </list>
/// The instructor can take the politicians over (<see cref="Instructor.AiRoles.Politicians"/>): then nobody rings
/// unless the instructor plays them, and silence has no consequences they don't choose.
/// </summary>
public sealed class PoliticalPressureSystem : ISimulationSystem
{
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromMinutes(5);

    /// <summary>After the minister has gone public, the politicians leave command alone for this long.</summary>
    public static readonly TimeSpan QuietAfterFallout = TimeSpan.FromMinutes(20);

    public const string Td = "Deputy Ciara Nolan TD";
    public const string ChiefExecutive = "Owen Keane, Chief Executive, Dublin City Council";
    public const string MinistersOffice = "Private Secretary, Office of the Minister for Housing, Local Government and Heritage";

    private sealed class Pressure
    {
        public int Level { get; set; } = 1;
        public DateTimeOffset? NextCallAt { get; set; }
        public DateTimeOffset QuietUntil { get; set; }
    }

    /// <summary>
    /// A serious incident running this long brings a request to visit the scene (Phase 12): a Critical one sooner than a
    /// High one. Approved, the party arrives <see cref="VisitArrivesAfter"/> later.
    /// </summary>
    public static readonly TimeSpan VisitAfterCritical = TimeSpan.FromMinutes(25), VisitAfterHigh = TimeSpan.FromMinutes(40);

    public static readonly TimeSpan VisitArrivesAfter = TimeSpan.FromMinutes(15);

    private readonly Dictionary<Guid, Pressure> _incidents = [];
    private readonly HashSet<Guid> _eased = [];
    private readonly HashSet<Guid> _visitAsked = [];
    private readonly Dictionary<Guid, (Guid IncidentId, string Visitor)> _visits = [];
    private readonly List<(DateTimeOffset At, Guid IncidentId, string Visitor, bool Live)> _arrivals = [];

    public int Order => 21;

    public void Update(SimulationContext context)
    {
        var world = context.World;
        var byInstructor = world.PlayedByHuman(Instructor.AiRoles.Politicians);
        Answered(context, byInstructor);
        Ignored(context, byInstructor);
        Visits(context, byInstructor);
        if (byInstructor) return;

        foreach (var (incidentId, facts) in world.Interagency.Incidents)
        {
            if (facts.Closed || facts.Priority < IncidentPriority.Medium) continue;
            var pressure = For(incidentId);
            if (pressure.NextCallAt is null)
            {
                var first = facts.CreatedAt + Scaled(world, incidentId, facts, facts.Priority switch
                {
                    IncidentPriority.Critical => TimeSpan.FromMinutes(4),
                    IncidentPriority.High => TimeSpan.FromMinutes(7),
                    _ => TimeSpan.FromMinutes(12),
                });
                pressure.NextCallAt = first > context.SimTime ? first : context.SimTime;
                continue;
            }
            if (context.SimTime < pressure.NextCallAt || context.SimTime < pressure.QuietUntil) continue;
            // One call at a time: while one is waiting, its fate decides what happens next.
            if (world.PoliticalCalls.Values.Any(c => c.Call.IncidentId == incidentId && OnLadder(c.Call.Office) && c.AnsweredAt is null && !c.Escalated)) continue;

            Ring(context, incidentId, facts, (PoliticalOffice)pressure.Level, chasing: null);
            pressure.NextCallAt = context.SimTime + Interval(world, incidentId, facts);
        }

        CommunityLeaders(context);
        AgencyHeads(context);
    }

    /// <summary>The TD → council → minister ladder (Phase 11 M5); community leaders and agency heads stand outside it (Phase 14).</summary>
    public static bool OnLadder(PoliticalOffice office) => office <= PoliticalOffice.MinistersOffice;

    // ---- Phase 14: community leaders and agency heads ----

    /// <summary>A community leader rings this long after their area is put under an evacuation or shelter order.</summary>
    public static readonly TimeSpan CommunityCallAfter = TimeSpan.FromMinutes(6);

    /// <summary>An evacuation ordered without the Garda: the Chief Superintendent rings this long after.</summary>
    public static readonly TimeSpan GardaConsultationWindow = TimeSpan.FromMinutes(5);

    public const string GardaChief = "Chief Superintendent, Garda Dublin Metropolitan Region";
    public const string HseLead = "Emergency management lead, HSE Dublin & North East";

    private static readonly string[] CommunityLeadersNames =
    [
        "Chair, Ringsend & Irishtown Residents' Association",
        "Fr Brendan Lacey, St Andrew's parish",
        "Cllr Aoife Doyle, local councillor",
        "Manager, Pearse Street community centre",
    ];

    private readonly HashSet<Guid> _communityCalled = [];
    private readonly HashSet<Guid> _gardaCalled = [];
    private readonly HashSet<Guid> _hseCalled = [];

    /// <summary>
    /// A community leader whose area is under an order rings: who is old, who won't leave the dog, who sleeps rough.
    /// Called back, they are engaged (Phase 14): people listen to them who don't listen to officials, and they say who
    /// needs help. Ignored, they say so to the media.
    /// </summary>
    private void CommunityLeaders(SimulationContext context)
    {
        var world = context.World;
        foreach (var zone in world.ProtectiveActions.Values)
        {
            if (context.SimTime - zone.DeclaredAt < CommunityCallAfter || !_communityCalled.Add(zone.ZoneId)) continue;
            var centre = GeoPoint.FromPoint(zone.Area.Centroid);
            var incident = world.Interagency.Incidents.Where(i => !i.Value.Closed)
                .OrderBy(i => GeoMath.DistanceMeters(i.Value.Location, centre)).Select(i => (Guid?)i.Key).FirstOrDefault();
            var caller = CommunityLeadersNames[(_communityCalled.Count - 1) % CommunityLeadersNames.Length];
            var order = zone.Type == ZoneType.EvacuationZone ? "evacuation" : "shelter-in-place order";
            var message = $"People here are frightened about the {order}. We have elderly residents who won't open the door to a stranger, " +
                          "people asking what to do with their dogs, and a few who sleep rough by the canal. Tell me what you need and I'll help.";
            var to = To(world, incident);
            CommsNet.Chat(context, caller, $"{caller} for the {to}: {message}",
                new PoliticalCallReceived(Guid.NewGuid(), incident, PoliticalOffice.CommunityLeader, caller, message, 1, to, zone.ZoneId));
        }
    }

    /// <summary>
    /// Heads of other agencies ring about their own responsibilities (Phase 14):
    /// <list type="bullet">
    /// <item>The Garda, when command orders an evacuation without them: under the Framework for Major Emergency
    /// Management the decision to evacuate is the Garda's, with the other principal response agencies. Command
    /// consulted them if the Garda is in unified command, or was notified, by the time the zone had been in force a few
    /// minutes. Otherwise it is a legal exposure, which answering the call (consulting them now) mitigates.</item>
    /// <item>The HSE's emergency manager, once an incident has many casualties, for numbers and destinations.</item>
    /// </list>
    /// </summary>
    private void AgencyHeads(SimulationContext context)
    {
        var world = context.World;
        foreach (var zone in world.ProtectiveActions.Values.Where(a => a.Type == ZoneType.EvacuationZone))
        {
            if (context.SimTime - zone.DeclaredAt < GardaConsultationWindow || !_gardaCalled.Add(zone.ZoneId)) continue;
            var centre = GeoPoint.FromPoint(zone.Area.Centroid);
            var incident = world.Interagency.Incidents.Where(i => !i.Value.Closed)
                .OrderBy(i => GeoMath.DistanceMeters(i.Value.Location, centre)).Select(i => (Guid?)i.Key).FirstOrDefault();
            if (GardaConsulted(world, incident, zone.DeclaredAt + GardaConsultationWindow)) continue;

            var message = "I'm told an evacuation has been ordered without us. Under the Framework for Major Emergency Management the decision to " +
                          "evacuate rests with An Garda Síochána, with the other principal response agencies. Who authorised this, and on what basis?";
            var call = new PoliticalCallReceived(Guid.NewGuid(), incident, PoliticalOffice.AgencyHead, GardaChief, message, 1, "Incident Commander", zone.ZoneId);
            CommsNet.Chat(context, GardaChief, $"{GardaChief} for the Incident Commander: {message}", call);
            context.EmitTruth(new LegalExposure("Evacuation authority",
                "An evacuation was ordered without consulting An Garda Síochána, whose decision it is under the Major Emergency framework.", call.CallId));
        }

        foreach (var (incidentId, facts) in world.Interagency.Incidents)
        {
            if (facts.Closed || facts.Casualties < 8 || !_hseCalled.Add(incidentId)) continue;
            var message = $"We need casualty numbers from {facts.Place}, by priority, and where they're being taken, so the hospitals can open their major emergency plans.";
            CommsNet.Chat(context, HseLead, $"{HseLead} for the Incident Commander: {message}",
                new PoliticalCallReceived(Guid.NewGuid(), incidentId, PoliticalOffice.AgencyHead, HseLead, message, 1, "Incident Commander"));
        }
    }

    /// <summary>The Garda had a say: they are in unified command at the incident, or command notified them.</summary>
    public static bool GardaConsulted(WorldState world, Guid? incidentId, DateTimeOffset by)
    {
        if (world.Notified.Any(n => n.Value <= by && (n.Key.Contains("garda", StringComparison.OrdinalIgnoreCase)
                                                       || n.Key.Contains("police", StringComparison.OrdinalIgnoreCase))))
            return true;
        return incidentId is { } id && world.Commands.TryGetValue(id, out var command)
               && command.Unified.Any(m => m.AgencyId is { } agency && world.Agencies.TryGetValue(agency, out var owner) && owner.Type == AgencyType.Police);
    }

    /// <summary>A community leader or agency head nobody called back.</summary>
    private static void Unheard(SimulationContext context, PoliticalCallState call)
    {
        var world = context.World;
        var incidentId = call.Call.IncidentId;
        var facts = incidentId is { } id ? world.Interagency.Incidents.GetValueOrDefault(id) : null;
        if (call.Call.Office == PoliticalOffice.CommunityLeader)
        {
            context.EmitTruth(new PoliticalFallout(call.Call.CallId, incidentId, 1, $"{call.Call.Caller} went to the media: nobody from the authorities had spoken to them"));
            context.EmitPerceived(new ReportReceived(Guid.NewGuid(), incidentId, ReportSource.Media, "Dublin Live",
                $"{call.Call.Caller}: \"Nobody from the authorities has spoken to us. Elderly people here don't know what to do.\"",
                Confidence.High, VerificationStatus.Confirmed, facts?.Location, facts is null ? null : 500), EventSources.Comms);
        }
        else
            context.EmitTruth(new PoliticalFallout(call.Call.CallId, incidentId, 1, $"{call.Call.Caller}'s call went unreturned"));
    }

    private Pressure For(Guid incidentId) => _incidents.TryGetValue(incidentId, out var pressure) ? pressure : _incidents[incidentId] = new Pressure();

    /// <summary>A call returned eases the pressure a step, and the next one comes a full interval later.</summary>
    private void Answered(SimulationContext context, bool byInstructor)
    {
        var world = context.World;
        foreach (var call in world.PoliticalCalls.Values.Where(c => c.AnsweredAt is not null && _eased.Add(c.Call.CallId)))
        {
            // Phase 14: a community leader engaged gets to work; an agency head has what they needed.
            if (!OnLadder(call.Call.Office))
            {
                if (!byInstructor)
                    CommsNet.Chat(context, call.Call.Caller, call.Call.Office == PoliticalOffice.CommunityLeader
                        ? $"{call.Call.Caller}: Thank you. We'll go door to door with your people, and I'll tell you who needs help getting out."
                        : $"{call.Call.Caller}: Understood. Keep me informed.", null);
                continue;
            }
            if (call.Call.IncidentId is not { } incidentId || !world.Interagency.Incidents.TryGetValue(incidentId, out var facts)) continue;
            var pressure = For(incidentId);
            pressure.Level = Math.Max(1, call.Call.Pressure - 1);
            pressure.NextCallAt = call.AnsweredAt!.Value + Interval(world, incidentId, facts);
            if (!byInstructor)
                CommsNet.Chat(context, call.Call.Caller, $"{call.Call.Caller}: Thanks for coming back to me. Keep me posted.", null);
        }
    }

    /// <summary>A call left unanswered too long: the next office up rings, or at the top the minister goes public.</summary>
    private void Ignored(SimulationContext context, bool byInstructor)
    {
        var world = context.World;
        foreach (var call in world.PoliticalCalls.Values.Where(c => c.AnsweredAt is null && !c.Escalated && context.SimTime - c.ReceivedAt >= AnswerWindow).ToList())
        {
            call.Escalated = true;
            if (byInstructor) continue; // the instructor decides what silence costs
            if (!OnLadder(call.Call.Office))
            {
                Unheard(context, call);
                continue;
            }
            var incidentId = call.Call.IncidentId;
            var facts = incidentId is { } id ? world.Interagency.Incidents.GetValueOrDefault(id) : null;
            var pressure = incidentId is { } key ? For(key) : new Pressure();

            if (call.Call.Pressure >= 3)
            {
                pressure.Level = 3;
                pressure.QuietUntil = context.SimTime + QuietAfterFallout;
                var place = facts?.Place ?? "the incident";
                context.EmitTruth(new PoliticalFallout(call.Call.CallId, incidentId, 3, "The minister went public without a briefing"));
                context.EmitPerceived(new ReportReceived(Guid.NewGuid(), incidentId, ReportSource.Media, "RTÉ News",
                    $"The Minister says she has \"not been briefed\" about {place} and wants answers from the emergency services tonight.",
                    Confidence.High, VerificationStatus.Confirmed, facts?.Location, facts is null ? null : 500), EventSources.Comms);
                context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Critical, AlertSeverity.Warning, "Minister: \"not been briefed\"",
                    $"The minister's office went to the media after its call about {place} wasn't returned.", incidentId, null), EventSources.Comms);
                continue;
            }

            pressure.Level = call.Call.Pressure + 1;
            var next = (PoliticalOffice)pressure.Level;
            context.EmitTruth(new PoliticalFallout(call.Call.CallId, incidentId, pressure.Level, $"{Caller(next)} rings, chasing {call.Call.Caller}'s call"));
            if (incidentId is { } chased && facts is not null)
            {
                Ring(context, chased, facts, next, chasing: call.Call.Caller);
                pressure.NextCallAt = context.SimTime + Interval(world, chased, facts);
            }
        }
    }

    /// <summary>
    /// Requests to visit the scene (Phase 12). Once a serious incident has run a while, the minister (if the pressure has
    /// reached her office) or the local TD asks to come and see the response. It is an approval for the IC: a visit while
    /// the hazard is live puts a VIP party where the crews work and takes an officer to escort them; declining it then is
    /// the right call. Approved, the party turns up at the cordon a little later.
    /// </summary>
    private void Visits(SimulationContext context, bool byInstructor)
    {
        var world = context.World;

        // Command's answers.
        foreach (var (approvalId, visit) in _visits.ToList())
        {
            if (!world.ApprovalDecisions.TryGetValue(approvalId, out var approved)) continue;
            _visits.Remove(approvalId);
            var live = HazardLive(world, visit.IncidentId);
            context.EmitTruth(new PoliticalVisitDecided(approvalId, visit.IncidentId, visit.Visitor, approved, live));
            if (approved) _arrivals.Add((context.SimTime + VisitArrivesAfter, visit.IncidentId, visit.Visitor, live));
        }

        // Arrivals.
        foreach (var arrival in _arrivals.Where(a => context.SimTime >= a.At).ToList())
        {
            _arrivals.Remove(arrival);
            var live = HazardLive(world, arrival.IncidentId);
            var place = world.Interagency.Incidents.GetValueOrDefault(arrival.IncidentId)?.Place ?? "the scene";
            context.EmitPerceived(new AlertRaised(Guid.NewGuid(), live ? AlertCategory.Safety : AlertCategory.SituationChange,
                live ? AlertSeverity.Warning : AlertSeverity.Info, $"{arrival.Visitor} at the cordon",
                live
                    ? $"{arrival.Visitor}'s party has arrived at {place} and wants to go forward. The hazard is still live: they need a Garda escort and a safety briefing, and an officer to host them."
                    : $"{arrival.Visitor}'s party has arrived at {place} to meet the crews. An officer is needed to host them at the command post.",
                arrival.IncidentId, null), EventSources.Comms);
        }

        if (byInstructor) return; // the instructor can ask as an official whenever they like
        foreach (var (incidentId, facts) in world.Interagency.Incidents)
        {
            if (facts.Closed || facts.Priority < IncidentPriority.High || _visitAsked.Contains(incidentId)) continue;
            var after = facts.Priority == IncidentPriority.Critical ? VisitAfterCritical : VisitAfterHigh;
            if (context.SimTime - facts.CreatedAt < after) continue;
            _visitAsked.Add(incidentId);

            var minister = For(incidentId).Level >= 3;
            var visitor = minister ? "The Minister" : "Deputy Nolan";
            var caller = minister ? MinistersOffice : Td;
            var details = $"{visitor} would like to visit {facts.Place} within the hour to see the response for herself and thank the crews. Can you facilitate it?";
            var approval = new ApprovalRequested(Guid.NewGuid(), incidentId, $"Visit to the scene: {visitor}", details, caller);
            _visits[approval.ApprovalId] = (incidentId, visitor);
            CommsNet.Chat(context, caller, $"{caller}: {details}", approval);
        }
    }

    /// <summary>
    /// Whether the incident's hazard is still live (Phase 12): a fire, release or flood near it not yet over, or people
    /// still told to evacuate or shelter around it.
    /// </summary>
    public static bool HazardLive(WorldState world, Guid incidentId)
    {
        if (!world.Interagency.Incidents.TryGetValue(incidentId, out var facts)) return false;
        if (world.Hazards.Values.Any(h => !h.Ended && GeoMath.DistanceMeters(h.Origin, facts.Location) < 800)) return true;
        return world.Interagency.IncidentZones.TryGetValue(incidentId, out var zones)
               && world.ProtectiveActions.Values.Any(a => zones.Any(z => z.Intersects(a.Area)));
    }

    private static void Ring(SimulationContext context, Guid incidentId, IncidentFacts facts, PoliticalOffice office, string? chasing)
    {
        var caller = Caller(office);
        var place = facts.Place;
        var message = office switch
        {
            PoliticalOffice.LocalTd => $"Constituents near {place} are ringing me. What's going on, is it safe for them, and when will the roads reopen?",
            PoliticalOffice.CouncilChiefExecutive when chasing is not null =>
                $"Deputy Nolan tells me nobody has returned her call about {place}. I need a briefing for the council's crisis team now: casualties, evacuation, and what you need from us.",
            PoliticalOffice.CouncilChiefExecutive => $"I need a briefing on {place} for the council's crisis management team: casualties, evacuation, and what you need from us.",
            _ when chasing is not null =>
                $"The Minister has heard nothing from you about {place}, and the council's calls haven't been returned. She will make a statement herself within the hour unless she is briefed.",
            _ => $"The Minister wants an update on {place} before the six o'clock news: casualties, the risk to the public, and whether you have what you need.",
        };
        var to = To(context.World, incidentId);
        CommsNet.Chat(context, caller, $"{caller} for the {to}: {message}",
            new PoliticalCallReceived(Guid.NewGuid(), incidentId, office, caller, message, (int)office, to));
    }

    public static string Caller(PoliticalOffice office) => office switch
    {
        PoliticalOffice.LocalTd => Td,
        PoliticalOffice.CouncilChiefExecutive => ChiefExecutive,
        _ => MinistersOffice,
    };

    /// <summary>The PIO once command has one (on the chart, or a participant playing the role), else the Incident Commander.</summary>
    public static string To(WorldState world, Guid? incidentId) =>
        (incidentId is { } id ? world.Staffed(IcsRole.PublicInformation, id) : world.PlayedByParticipant(UserRole.PublicInformation))
            ? "Public Information Officer"
            : "Incident Commander";

    /// <summary>Time between calls: by priority, shorter with casualties or people told to evacuate or shelter.</summary>
    private static TimeSpan Interval(WorldState world, Guid incidentId, IncidentFacts facts) => Scaled(world, incidentId, facts, facts.Priority switch
    {
        IncidentPriority.Critical => TimeSpan.FromMinutes(10),
        IncidentPriority.High => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(25),
    });

    private static TimeSpan Scaled(WorldState world, Guid incidentId, IncidentFacts facts, TimeSpan basis)
    {
        var factor = 1.0;
        if (facts.Casualties >= 5) factor *= 0.7;
        if (world.Interagency.IncidentZones.TryGetValue(incidentId, out var zones)
            && world.ProtectiveActions.Values.Any(a => zones.Any(z => z.Intersects(a.Area))))
            factor *= 0.8;
        return basis * factor;
    }

    /// <summary>The office an instructor is playing, from who they say they are.</summary>
    public static PoliticalOffice OfficeOf(string caller) =>
        caller.Contains("minister", StringComparison.OrdinalIgnoreCase) || caller.Contains("taoiseach", StringComparison.OrdinalIgnoreCase)
            ? PoliticalOffice.MinistersOffice
            : caller.Contains("council", StringComparison.OrdinalIgnoreCase) || caller.Contains("chief executive", StringComparison.OrdinalIgnoreCase)
                ? PoliticalOffice.CouncilChiefExecutive
                : PoliticalOffice.LocalTd;
}
