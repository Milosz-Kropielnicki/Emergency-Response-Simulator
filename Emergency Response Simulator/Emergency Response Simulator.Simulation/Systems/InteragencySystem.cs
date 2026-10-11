using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The other agencies as joint partners with priorities of their own (Design Document §14):
/// <list type="bullet">
/// <item><b>Unified Command</b>: an invited agency with a stake in the incident sends its commander to the command post.
/// Once there, command can task its units at the incident, and it shares what it knows at once.</item>
/// <item><b>Resource hoarding</b>: asked for units, an agency keeps a reserve for its own area: a third of its fleet,
/// more when relations are strained, less in unified command or once the EOC has declared a major emergency.</item>
/// <item><b>Information silos</b>: what an agency knows reaches command through unified command, the liaison officer's
/// rounds, the EOC, a direct request, or (late, if at all) when the agency gets round to volunteering it.</item>
/// <item><b>Authority disputes</b>: an area authority objects to zones that reach into its estate (they stop at the
/// boundary until settled), mutual-aid officers refuse tasks outside their agreement, and investigators claim the lead.
/// Command concedes, negotiates, asserts its authority (which the agency remembers) or escalates to the EOC.</item>
/// <item><b>EOC activation</b>: seats fill over minutes; each level brings reach (reserves released, disputes decided)
/// and costs (staff, situation reports).</item>
/// <item><b>Public information</b>: the media ring with questions; a PIO answers them and passes on protective advice.
/// Unanswered, speculation spreads and worried people ring 999. Without a joint information centre another agency may
/// contradict the official line, and fewer people do as they are told.</item>
/// </list>
/// Chance outcomes are seeded from ids so runs are reproducible.
/// </summary>
public sealed class InteragencySystem : ISimulationSystem
{
    public static readonly TimeSpan InvitationDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AssistanceDelay = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan BriefingDelay = TimeSpan.FromSeconds(75);
    public static readonly TimeSpan NegotiationTime = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan EscalationTime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LiaisonRelayInterval = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan UnifiedShareDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan EocRelayDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan VolunteerAfter = TimeSpan.FromMinutes(20);

    /// <summary>An area authority left to handle its own estate warns the people there this much later.</summary>
    public static readonly TimeSpan ConcededWarningDelay = TimeSpan.FromMinutes(8);

    public static readonly TimeSpan FirstMediaEnquiry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PioAnswerDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SpeculationAfter = TimeSpan.FromMinutes(12);

    /// <summary>Without a joint information centre, another agency puts out its own line this long after a protective zone.</summary>
    public static readonly TimeSpan ContradictionAfter = TimeSpan.FromMinutes(6);

    public static readonly TimeSpan EocAdviceAfter = TimeSpan.FromMinutes(10);

    /// <summary>An objection unanswered this long comes back as an alert.</summary>
    public static readonly TimeSpan DisputeChaseAfter = TimeSpan.FromMinutes(3);

    private static readonly string[] Surnames = ["Byrne", "Doyle", "Kavanagh", "Nolan", "Fitzgerald", "Brennan", "Quinn", "Walsh", "Kinsella", "Moran", "Dunne", "Lynch"];
    private static readonly string[] Outlets = ["RTÉ News", "The Irish Times", "Newstalk", "Dublin Live", "TheJournal.ie", "Irish Independent", "Sky News Ireland"];

    private readonly Dictionary<Guid, DateTimeOffset> _nextEnquiry = [];
    private readonly Dictionary<Guid, int> _enquiries = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastSpeculation = [];
    private readonly HashSet<Guid> _contradicted = [];
    private readonly HashSet<Guid> _chased = [];
    private readonly HashSet<Guid> _mediaChased = [];
    private readonly HashSet<Guid> _disputedOrders = [];
    private readonly Dictionary<Guid, (Guid? AgencyId, string Agency)> _handovers = [];
    private readonly Dictionary<string, DateTimeOffset> _seatDue = [];
    private readonly HashSet<string> _seatsEmitted = [];
    private readonly Dictionary<Guid, DateTimeOffset> _highSince = [];
    private DateTimeOffset? _nextRelay;
    private DateTimeOffset? _sitrepRequestedAt;
    private DateTimeOffset? _overdueAlerted;
    private bool _advised;

    public int Order => 19; // after the agencies' own control rooms (18), before command's responders (20) read orders back

    public void Update(SimulationContext context)
    {
        ParticipantAnswers(context);
        Invitations(context);
        Assistance(context);
        Briefings(context);
        ShareHeld(context);
        Jurisdictions(context);
        MutualAidTerms(context);
        SettleDisputes(context);
        Handovers(context);
        Eoc(context);
        Media(context);
        Contradictions(context);
        Chase(context);
    }

    /// <summary>
    /// What the participants running agencies decided (Phase 11 M5), carried out as the instructor's decisions are: for
    /// units, or to join unified command, in their words.
    /// </summary>
    private static void ParticipantAnswers(SimulationContext context)
    {
        var world = context.World;
        foreach (var answer in world.AgencyAnswers.ToList())
        {
            world.AgencyAnswers.Remove(answer);
            var message = Guid.NewGuid();
            if (!AnswerAssistanceByHand(context, answer.ItemId, answer.Approve, answer.Quantity, answer.Text, message))
                AnswerInvitationByHand(context, answer.ItemId, answer.Approve, answer.Text, message);
        }
    }

    // ---- Unified Command ----

    private void Invitations(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        foreach (var invitation in ia.Invitations.ToList())
        {
            if (!world.Agencies.TryGetValue(invitation.AgencyId, out var agency))
            {
                ia.Invitations.Remove(invitation);
                continue;
            }
            if (world.PlayedByHuman(Instructor.AiRoles.Agency(invitation.AgencyId))) continue; // the instructor answers (Phase 11 M4)
            var standing = ia.StandingOf(invitation.AgencyId);
            var delay = InvitationDelay * (LiaisonAt(world, invitation.IncidentId) ? 0.5 : 1) * (standing == AgencyStanding.Strained ? 1.5 : 1);
            if (context.SimTime - invitation.At < delay) continue;
            ia.Invitations.Remove(invitation);

            var stake = Stake(world, invitation.AgencyId, agency, invitation.IncidentId);
            string? refusal = standing == AgencyStanding.Hostile
                ? "We'll not be sending anyone to your command post after the way our concerns were dealt with. Talk to us through the EOC."
                : stake is null
                    ? "We've no role at this incident. We'll keep in touch through your liaison officer."
                    : null;
            if (refusal is not null)
            {
                CommsNet.Chat(context, agency.Name, $"{agency.Name}: {refusal}", new UnifiedCommandDeclined(invitation.IncidentId, invitation.AgencyId, refusal));
                continue;
            }
            Join(context, invitation, agency, $"{stake}." + (agency.Priority is { } priority ? $" Our priority: {LowerFirst(priority)}." : ""), null);
        }
    }

    private static void Join(SimulationContext context, PendingInvitation invitation, WorldAgency agency, string why, Guid? messageId)
    {
        var commander = CommanderOf(invitation.AgencyId, agency);
        CommsNet.Chat(context, agency.Name, $"{commander} ({agency.Name}) is at the command post and joins unified command: {why}",
            new UnifiedCommandJoined(invitation.IncidentId, invitation.AgencyId, agency.Name, commander, agency.Priority), null, messageId);
    }

    /// <summary>
    /// The instructor answers an invitation to unified command as the agency (Phase 11 M4): it joins, or declines in their
    /// words. Returns false when there is no such invitation waiting.
    /// </summary>
    public static bool AnswerInvitationByHand(SimulationContext context, Guid invitationId, bool join, string? text, Guid message)
    {
        var world = context.World;
        var invitation = world.Interagency.Invitations.FirstOrDefault(i => i.Id == invitationId);
        if (invitation is null || !world.Agencies.TryGetValue(invitation.AgencyId, out var agency)) return false;
        world.Interagency.Invitations.Remove(invitation);
        if (join)
            Join(context, invitation, agency, string.IsNullOrWhiteSpace(text) ? "we'll work with you on this." : text.Trim(), message);
        else
        {
            var refusal = string.IsNullOrWhiteSpace(text) ? "We won't be joining unified command." : text.Trim();
            CommsNet.Chat(context, agency.Name, $"{agency.Name}: {refusal}", new UnifiedCommandDeclined(invitation.IncidentId, invitation.AgencyId, refusal),
                null, message);
        }
        return true;
    }

    /// <summary>Why the agency has a part in the incident, or null when it has none.</summary>
    public static string? Stake(WorldState world, Guid agencyId, WorldAgency agency, Guid incidentId)
    {
        var ia = world.Interagency;
        if (!ia.Incidents.TryGetValue(incidentId, out var incident)) return null;
        var at = incident.Location;

        if (world.Units.Values.Any(u => u.AgencyId == agencyId && (u.OrderedIncidentId == incidentId
                || (u.OrderedIncidentId is { } task && world.AgencyTasks.TryGetValue(task, out var job) && GeoMath.DistanceMeters(job.Location, at) < 600))))
            return "our units are working there";

        var truth = world.Incidents.Values.Where(i => GeoMath.DistanceMeters(i.Location, at) < 500).ToList();
        var threats = string.Join(' ', incident.Threats).ToLowerInvariant();
        switch (agency.Type)
        {
            case AgencyType.Investigation when incident.Type is IncidentType.Explosion or IncidentType.CrimeScene or IncidentType.ActiveThreat
                                               || truth.Any(t => t.Type is IncidentType.Explosion or IncidentType.CrimeScene):
                return "an explosion is a possible crime scene until we rule it out";
            case AgencyType.Regulator when incident.Type == IncidentType.HazmatRelease || threats.Contains("chemical") || threats.Contains("hazmat")
                                           || world.Sites.Values.Any(s => s.Kind == HazardSiteKind.ChemicalStore && GeoMath.DistanceMeters(s.Location, at) < 300):
                return "there is a registered hazardous site at the scene";
            case AgencyType.EmergencyManagement when incident.Priority >= IncidentPriority.High || ia.IncidentZones.ContainsKey(incidentId):
                return "the council leads on welfare, evacuation and recovery";
            case AgencyType.PublicHealth when incident.Casualties > 0 || truth.Any(t => t.ActualCasualties > 0)
                                              || world.Hazards.Values.Any(h => h.Kind == HazardKind.Plume && !h.Ended && GeoMath.DistanceMeters(h.Origin, at) < 800):
                return "there are casualties and a possible public health risk";
        }

        // An area authority whose estate is close by or inside the incident's zones.
        foreach (var jurisdiction in ia.Jurisdictions.Where(j => j.Info.AuthorityAgencyId == agencyId))
        {
            var near = jurisdiction.Area.Distance(at.ToPoint()) * 111_000 < 400;
            var zoned = ia.IncidentZones.TryGetValue(incidentId, out var zones) && zones.Any(z => z.Intersects(jurisdiction.Area));
            if (near || zoned) return $"the incident affects {jurisdiction.Info.Name}";
        }
        return null;
    }

    // ---- Resource hoarding ----

    private void Assistance(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        foreach (var pending in ia.Assistance.ToList())
        {
            if (world.PlayedByHuman(Instructor.AiRoles.Agency(pending.Request.AgencyId))) continue; // the instructor answers (Phase 11 M4)
            if (context.SimTime - pending.At < AssistanceDelay) continue;
            ia.Assistance.Remove(pending);
            var request = pending.Request;
            if (!world.Agencies.TryGetValue(request.AgencyId, out var agency)) continue;

            var free = FreeUnits(world, request);
            var fleet = world.Units.Values.Count(u => u.AgencyId == request.AgencyId && !u.BrokenDown);
            var share = ReserveShare(world, request.AgencyId, request.IncidentId);
            var reserve = share >= 1 ? free.Count : (int)Math.Ceiling(fleet * share);
            var granted = Math.Clamp(free.Count - reserve, 0, request.Quantity);
            var units = free.Take(granted).ToList();

            var reason = ia.StandingOf(request.AgencyId) == AgencyStanding.Hostile
                ? "we're not in a position to help, given how our concerns were handled."
                : granted == request.Quantity
                    ? $"sending {Callsigns(units)}. They will work to your incident commander."
                    : granted > 0
                        ? $"we can release {granted} of the {request.Quantity}: {Callsigns(units)}. We have to keep {reserve} back for cover across our own area."
                        : free.Count > 0
                            ? $"we can't release any: the {free.Count} we have free {(free.Count == 1 ? "is" : "are")} our only cover."
                            : "nobody free: all our units are committed.";
            Release(context, request, agency, units, reason, null);
        }
    }

    private static List<WorldUnit> FreeUnits(WorldState world, AgencyAssistanceRequested request) =>
        world.Units.Values.Where(u => u.AgencyId == request.AgencyId && !u.BrokenDown && u.Phase == ResponsePhase.Idle && u.OrderedIncidentId is null)
            .OrderBy(u => world.Interagency.Incidents.TryGetValue(request.IncidentId, out var facts) ? GeoMath.DistanceMeters(u.Location, facts.Location) : 0)
            .ToList();

    private static void Release(SimulationContext context, AgencyAssistanceRequested request, WorldAgency agency, List<WorldUnit> units, string reason,
        Guid? messageId)
    {
        context.EmitPerceived(new AgencyAssistanceDecided(request.RequestId, units.Count, units.Select(u => u.Id).ToList(), reason), EventSources.Comms);
        foreach (var unit in units)
            context.EmitPerceived(new UnitDispatched(unit.Id, request.IncidentId, null), EventSources.Engine);
        CommsNet.Chat(context, agency.Name, $"{agency.Name}: {reason}", null, TimeSpan.Zero, messageId);
    }

    /// <summary>
    /// The instructor answers a request for units as the agency (Phase 11 M4): sends up to <paramref name="quantity"/> of
    /// its free units, nearest first, or none. Returns false when there is no such request waiting.
    /// </summary>
    public static bool AnswerAssistanceByHand(SimulationContext context, Guid requestId, bool grant, int? quantity, string? text, Guid message)
    {
        var world = context.World;
        var pending = world.Interagency.Assistance.FirstOrDefault(a => a.Request.RequestId == requestId);
        if (pending is null || !world.Agencies.TryGetValue(pending.Request.AgencyId, out var agency)) return false;
        world.Interagency.Assistance.Remove(pending);
        var units = grant ? FreeUnits(world, pending.Request).Take(Math.Clamp(quantity ?? pending.Request.Quantity, 0, pending.Request.Quantity)).ToList() : [];
        var reason = !string.IsNullOrWhiteSpace(text) ? text.Trim()
            : units.Count > 0 ? $"sending {Callsigns(units)}. They will work to your incident commander."
            : "we can't release any units to you at the moment.";
        Release(context, pending.Request, agency, units, reason, message);
        return true;
    }

    /// <summary>The share of its fleet an agency keeps back for its own area when asked for help.</summary>
    public static double ReserveShare(WorldState world, Guid agencyId, Guid incidentId)
    {
        var ia = world.Interagency;
        var share = ia.StandingOf(agencyId) switch
        {
            AgencyStanding.Hostile => 1.0,
            AgencyStanding.Strained => 0.5,
            _ => 1 / 3.0,
        };
        if (share >= 1) return share;
        if (world.Commands.TryGetValue(incidentId, out var command) && command.InUnifiedCommand(agencyId)) share /= 2;
        share = ia.EocLevel switch
        {
            EocLevel.Full => 0,
            EocLevel.Partial => share / 2,
            _ => share,
        };
        return share;
    }

    private static string Callsigns(IReadOnlyList<WorldUnit> units) => units.Count switch
    {
        0 => "none",
        1 => units[0].Callsign,
        _ => string.Join(", ", units.Take(units.Count - 1).Select(u => u.Callsign)) + " and " + units[^1].Callsign,
    };

    // ---- Information silos ----

    private static void Briefings(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        foreach (var briefing in ia.Briefings.ToList())
        {
            if (context.SimTime - briefing.At < BriefingDelay) continue;
            ia.Briefings.Remove(briefing);
            if (!world.Agencies.TryGetValue(briefing.AgencyId, out var agency)) continue;

            var hostile = ia.StandingOf(briefing.AgencyId) == AgencyStanding.Hostile;
            var items = ia.Held.Values.Where(h => h.Info.AgencyId == briefing.AgencyId && h.SharedAt is null && h.KnownAt <= context.SimTime
                                                  && (!hostile || h.Info.Urgent)).ToList();
            if (items.Count == 0)
            {
                CommsNet.Chat(context, agency.Name, $"{agency.Name}: nothing further from us at this time.", null, TimeSpan.Zero);
                continue;
            }
            foreach (var item in items)
                Share(context, item, agency, "briefing");
        }
    }

    private void ShareHeld(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;

        // The liaison officer does the rounds of the agencies every few minutes.
        var liaison = world.Commands.Values.Any(c => c.IsStaffed(IcsRole.Liaison));
        if (!liaison) _nextRelay = null;
        else _nextRelay ??= context.SimTime + LiaisonRelayInterval;
        var relayNow = _nextRelay is { } relay && context.SimTime >= relay;
        if (relayNow) _nextRelay = context.SimTime + LiaisonRelayInterval;

        foreach (var item in ia.Held.Values.Where(h => h.SharedAt is null && h.KnownAt <= context.SimTime).ToList())
        {
            var agencyId = item.Info.AgencyId;
            if (!world.Agencies.TryGetValue(agencyId, out var agency)) continue;
            var standing = ia.StandingOf(agencyId);

            var joined = world.Commands.Values.SelectMany(c => c.Unified).Where(m => m.AgencyId == agencyId).Select(m => (DateTimeOffset?)m.JoinedAt).Min();
            if (joined is { } since && context.SimTime >= Later(item.KnownAt, since) + UnifiedShareDelay)
            {
                Share(context, item, agency, "unified command");
                continue;
            }
            if (standing == AgencyStanding.Hostile)
            {
                // A hostile agency still passes on what matters to life, eventually.
                if (item.Info.Urgent && context.SimTime >= item.KnownAt + VolunteerAfter * 1.75)
                    Share(context, item, agency, "volunteered");
                continue;
            }
            if (relayNow && context.SimTime - item.KnownAt >= TimeSpan.FromMinutes(1))
            {
                Share(context, item, agency, "liaison officer");
                continue;
            }
            if (ia.MajorEmergency && ia.EocLevelSince is { } eocSince && context.SimTime >= Later(item.KnownAt, eocSince) + EocRelayDelay)
            {
                Share(context, item, agency, "EOC");
                continue;
            }
            var volunteer = standing == AgencyStanding.Strained && !item.Info.Urgent ? VolunteerAfter * 1.75 : VolunteerAfter;
            if (context.SimTime >= item.KnownAt + volunteer)
                Share(context, item, agency, "volunteered");
        }
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static void Share(SimulationContext context, HeldItem item, WorldAgency agency, string via)
    {
        var reportId = Guid.NewGuid();
        context.EmitPerceived(new InformationShared(item.Info.ItemId, reportId, agency.Name, via), EventSources.Comms);
        var claim = item.Info.Claim;
        CommsNet.Chat(context, agency.Name, $"{agency.Name}: {claim}",
            new ReportReceived(reportId, null, ReportSource.Agency, agency.Name, claim, Confidence.High, VerificationStatus.Reported,
                item.Info.Location, item.Info.Location is null ? null : 50));
    }

    // ---- Authority disputes ----

    /// <summary>A zone reaching into an area another authority answers for, without its agreement.</summary>
    private static void Jurisdictions(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        if (ia.NewZones.Count == 0) return;
        var zones = ia.NewZones.ToList();
        ia.NewZones.Clear();

        foreach (var (zone, _) in zones)
        {
            if (!Interagency.NeedsConsent(zone.Type) || zone.Boundary.Count < 3) continue;
            var area = Wgs84.CreatePolygon(zone.Boundary);
            foreach (var jurisdiction in ia.Jurisdictions.Where(j => j.Info.RequiresConsent && j.Info.AuthorityAgencyId is not null))
            {
                var authorityId = jurisdiction.Info.AuthorityAgencyId!.Value;
                if (!area.Intersects(jurisdiction.Area)) continue;
                var overlap = area.Intersection(jurisdiction.Area);
                if (overlap.IsEmpty || overlap.Area <= 0) continue;
                if (zone.IncidentId is { } incidentId && world.Commands.TryGetValue(incidentId, out var command) && command.InUnifiedCommand(authorityId))
                    continue; // agreed among unified command: shared command, not just coordination
                if (ia.Agreed.Contains((zone.IncidentId ?? Guid.Empty, authorityId))) continue;

                var disputeId = Guid.NewGuid();
                if (!ia.Exclusions.TryGetValue(zone.ZoneId, out var cut)) ia.Exclusions[zone.ZoneId] = cut = [];
                cut.Add(new ZoneExclusion(overlap, disputeId));

                var info = jurisdiction.Info;
                var measure = EventDescriber.Humanize(zone.Type).ToLowerInvariant();
                var position = $"{info.Authority} must agree any {measure} on {info.Name}. Until then it stops at our boundary, and we won't clear our people.";
                var basis = $"{info.Authority} answers for {info.Powers ?? info.Name} ({EventDescriber.Humanize(info.Level).ToLowerInvariant()} jurisdiction).";
                context.EmitPerceived(new DisputeRaised(disputeId, zone.IncidentId, DisputeKind.Jurisdiction, authorityId, info.Authority,
                    $"{zone.Name} reaches into {info.Name}", position, basis, ZoneId: zone.ZoneId), EventSources.Comms);
                CommsNet.Chat(context, info.Authority, $"{info.Authority}: {position}", null, TimeSpan.Zero);
            }
        }
    }

    /// <summary>Mutual-aid crews ordered to do something their agreement doesn't cover: their officer refuses.</summary>
    private void MutualAidTerms(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        if (ia.Agreements.Count == 0) return;
        foreach (var pending in world.PendingOrders.ToList())
        {
            var order = pending.Order;
            if (order.TargetKind != OrderTargetKind.Unit || order.TargetId is not { } unitId || _disputedOrders.Contains(order.OrderId)) continue;
            if (!world.Units.TryGetValue(unitId, out var unit) || unit.AgencyId is not { } agencyId
                || !world.Agencies.TryGetValue(agencyId, out var agency)) continue;
            var agreement = ia.Agreements.Values.FirstOrDefault(a => a.Provider == agency.Name);
            var excluded = agreement?.ExcludedTasks.FirstOrDefault(t => order.Text.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (agreement is null || excluded is null) continue;

            _disputedOrders.Add(order.OrderId);
            world.PendingOrders.Remove(pending);
            var officer = $"{agency.Name} officer";
            var reason = $"our agreement doesn't cover {excluded} work. {agreement.Terms}";
            context.EmitPerceived(new OrderDeclined(order.OrderId, reason), EventSources.Comms);
            context.EmitPerceived(new DisputeRaised(Guid.NewGuid(), order.IncidentId, DisputeKind.MutualAidTerms, agencyId, agency.Name,
                $"{unit.Callsign}: \"{order.Text}\"", $"Our crews won't take on {excluded} work: it is outside the mutual-aid agreement.",
                $"Mutual-aid agreement with {agency.Name}: {agreement.Terms}", OrderId: order.OrderId), EventSources.Comms);
            CommsNet.Chat(context, officer, $"{officer}: {unit.Callsign} can't take that order: {reason}", null, TimeSpan.Zero);
        }
    }

    private void SettleDisputes(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        foreach (var dispute in ia.Disputes.Values.Where(d => d.Open && d.Chosen is not null).ToList())
        {
            var raised = dispute.Raised;
            if (dispute.SettleAt is null)
            {
                var standing = raised.AgencyId is { } id ? ia.StandingOf(id) : AgencyStanding.Good;
                dispute.SettleAt = dispute.Chosen switch
                {
                    DisputeResolution.Negotiate => dispute.ChosenAt!.Value + NegotiationTime * NegotiationFactor(world, raised, standing),
                    DisputeResolution.Escalate => dispute.ChosenAt!.Value + EscalationTime * (SitrepOverdue(world, context.SimTime) ? 1.5 : 1),
                    _ => dispute.ChosenAt!.Value,
                };
                if (dispute.Chosen is DisputeResolution.Negotiate or DisputeResolution.Escalate && raised.ZoneId is { } zoneId)
                    SetExclusion(ia, zoneId, raised.DisputeId, dispute.SettleAt);
            }
            if (context.SimTime < dispute.SettleAt) continue;
            Resolve(context, dispute);
        }
    }

    private static double NegotiationFactor(WorldState world, DisputeRaised raised, AgencyStanding standing)
    {
        var factor = standing switch { AgencyStanding.Hostile => 2.0, AgencyStanding.Strained => 1.5, _ => 1.0 };
        if (raised.IncidentId is { } incidentId)
        {
            if (world.Commands.TryGetValue(incidentId, out var command) && command.InUnifiedCommand(raised.AgencyId)) factor *= 0.4;
            else if (LiaisonAt(world, incidentId)) factor *= 0.5;
        }
        return factor;
    }

    private static void SetExclusion(WorldInteragency ia, Guid zoneId, Guid disputeId, DateTimeOffset? until)
    {
        if (!ia.Exclusions.TryGetValue(zoneId, out var cut)) return;
        foreach (var exclusion in cut.Where(x => x.DisputeId == disputeId))
            exclusion.Until = until;
    }

    private void Resolve(SimulationContext context, WorldDispute dispute)
    {
        var world = context.World;
        var ia = world.Interagency;
        var raised = dispute.Raised;
        var resolution = dispute.Chosen!.Value;
        var now = context.SimTime;
        string outcome;

        switch (raised.Kind)
        {
            case DisputeKind.LeadAgency:
                switch (resolution)
                {
                    case DisputeResolution.Concede:
                        outcome = $"{raised.Agency} takes the lead and the site is a crime scene: firefighting is defensive from now on.";
                        if (raised.IncidentId is { } handed)
                            context.EmitPerceived(new LeadAgencyChanged(handed, raised.Agency, "Conceded: crime scene to be preserved"), EventSources.Comms);
                        Relation(context, raised, better: true, "pleased to have the lead");
                        break;
                    case DisputeResolution.Assert:
                        outcome = $"You keep the lead. {raised.Agency} accepts it, but will not forget being overruled.";
                        Relation(context, raised, better: false, "overruled on the lead");
                        break;
                    default:
                        outcome = (resolution == DisputeResolution.Escalate ? "EOC decision: " : "Agreed at the command post: ") +
                                  $"fire leads until the fire is out and casualties are clear, then the lead passes to {raised.Agency}. " +
                                  "Crews will avoid disturbing the seat of the explosion where they can.";
                        if (raised.IncidentId is { } staged) _handovers[staged] = (raised.AgencyId, raised.Agency);
                        break;
                }
                break;

            case DisputeKind.Jurisdiction:
                var agreedKey = (raised.IncidentId ?? Guid.Empty, raised.AgencyId ?? Guid.Empty);
                switch (resolution)
                {
                    case DisputeResolution.Concede:
                        outcome = $"{raised.Agency} will clear and warn its own area itself, about {ConcededWarningDelay.TotalMinutes:F0} minutes from now.";
                        if (raised.ZoneId is { } zone) SetExclusion(ia, zone, raised.DisputeId, now + ConcededWarningDelay);
                        Relation(context, raised, better: true, "left to manage its own estate");
                        break;
                    case DisputeResolution.Assert:
                        outcome = $"The zone applies across the boundary now. {raised.Agency} complies under protest.";
                        if (raised.ZoneId is { } asserted) SetExclusion(ia, asserted, raised.DisputeId, now);
                        Relation(context, raised, better: false, "overruled on its own estate");
                        break;
                    default:
                        outcome = (resolution == DisputeResolution.Escalate ? "EOC decision: " : "Agreed: ") +
                                  $"a joint measure across the boundary; {raised.Agency} staff will help clear their area.";
                        if (raised.ZoneId is { } joint) SetExclusion(ia, joint, raised.DisputeId, now);
                        ia.Agreed.Add(agreedKey);
                        break;
                }
                break;

            default: // MutualAidTerms
                var order = raised.OrderId is { } orderId ? world.Orders.GetValueOrDefault(orderId) : null;
                var proceed = resolution is DisputeResolution.Negotiate or DisputeResolution.Assert;
                if (proceed && order is not null)
                    world.PendingOrders.Add(new PendingOrder(order, now)); // their crews will now read it back
                else if (order is not null)
                    context.EmitPerceived(new OrderClosed(order.OrderId, Completed: false), EventSources.Engine);
                outcome = resolution switch
                {
                    DisputeResolution.Negotiate => "Agreed with their officer: the crews will do it under their own officer, from the warm zone.",
                    DisputeResolution.Assert => $"{raised.Agency} crews will do it under protest; their service will be slower to help next time.",
                    DisputeResolution.Escalate => "EOC decision: the task is outside the agreement, so the order is withdrawn.",
                    _ => "Order withdrawn; the crews stay within their agreement.",
                };
                if (resolution == DisputeResolution.Assert)
                {
                    ia.Reluctant.Add(raised.Agency);
                    Relation(context, raised, better: false, "crews made to work outside their agreement");
                }
                else if (resolution == DisputeResolution.Concede)
                    Relation(context, raised, better: true, "agreement respected");
                break;
        }

        context.EmitPerceived(new DisputeResolved(raised.DisputeId, resolution, outcome), EventSources.Comms);
        CommsNet.Chat(context, resolution == DisputeResolution.Escalate ? "EOC" : raised.Agency, outcome, null, TimeSpan.Zero);
    }

    private static void Relation(SimulationContext context, DisputeRaised raised, bool better, string reason)
    {
        if (raised.AgencyId is not { } agencyId) return;
        var current = context.World.Interagency.StandingOf(agencyId);
        var next = better ? Interagency.Better(current) : Interagency.Worse(current);
        if (next != current)
            context.EmitPerceived(new AgencyRelationChanged(agencyId, next, reason), EventSources.Comms);
    }

    /// <summary>A staged handover of the lead: once the fire is out, the lead passes as agreed.</summary>
    private void Handovers(SimulationContext context)
    {
        var world = context.World;
        foreach (var (incidentId, (_, agency)) in _handovers.ToList())
        {
            if (!world.Interagency.Incidents.TryGetValue(incidentId, out var facts)) continue;
            var burning = world.Hazards.Values.Any(h => h.Kind == HazardKind.Fire && !h.Ended && h.Model is FireSpreadModel { IsActive: true }
                                                        && GeoMath.DistanceMeters(h.Origin, facts.Location) < 500);
            if (burning) continue;
            _handovers.Remove(incidentId);
            var reason = $"Fire out and casualties clear: the lead passes to {agency} for the investigation, as agreed.";
            CommsNet.Chat(context, agency, $"{agency}: {reason}", new LeadAgencyChanged(incidentId, agency, reason));
        }
    }

    private static bool LiaisonAt(WorldState world, Guid incidentId) =>
        world.Commands.TryGetValue(incidentId, out var command) && command.IsStaffed(IcsRole.Liaison);

    // ---- Emergency Operations Centre ----

    private void Eoc(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;

        if (ia.EocRequested is { } target)
        {
            if (target < ia.EocLevel)
            {
                context.EmitPerceived(new EocLevelReached(target), EventSources.Comms);
                CommsNet.Chat(context, "EOC", $"EOC: standing down to {EocLevels.Label(target).ToLowerInvariant()}.", null, TimeSpan.Zero);
                _seatDue.Clear();
                _seatsEmitted.Clear();
            }
            else
            {
                StaffUp(context, target);
            }
        }

        // Situation reports: the EOC wants one at intervals while it is watching.
        if (EocLevels.SitrepInterval(ia.EocLevel) is { } interval)
        {
            var submittedSinceAsk = ia.LastSitrepAt is { } last && (_sitrepRequestedAt is null || last >= _sitrepRequestedAt);
            if (ia.SitrepDue is null || (submittedSinceAsk && _sitrepRequestedAt is not null))
            {
                var due = (ia.LastSitrepAt is { } sent && submittedSinceAsk ? sent : context.SimTime) + interval;
                _sitrepRequestedAt = context.SimTime;
                context.EmitPerceived(new SitrepRequested(due), EventSources.Comms);
                CommsNet.Chat(context, "EOC", $"EOC: situation report please by {due.ToLocalTime():HH:mm}.", null, TimeSpan.Zero);
            }
            else if (SitrepOverdue(world, context.SimTime) && _overdueAlerted != ia.SitrepDue)
            {
                _overdueAlerted = ia.SitrepDue;
                context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Planning, AlertSeverity.Warning, "EOC: situation report overdue",
                    $"The EOC asked for a situation report by {ia.SitrepDue?.ToLocalTime():HH:mm}. Without it, its decisions are slower and less informed.",
                    null, null), EventSources.Comms);
            }
        }
        else
        {
            _sitrepRequestedAt = null;
        }

        Advise(context);
    }

    private void StaffUp(SimulationContext context, EocLevel target)
    {
        var ia = context.World.Interagency;
        var requestedAt = ia.EocRequestedAt ?? context.SimTime;
        var needed = EocLevels.SeatsAt(target).Where(s => !ia.StaffedSeats.Contains(s.Function)).ToList();
        foreach (var function in _seatDue.Keys.Where(f => needed.All(s => s.Function != f)).ToList())
            _seatDue.Remove(function);
        for (var i = 0; i < needed.Count; i++)
            _seatDue.TryAdd(needed[i].Function, requestedAt + EocLevels.StaffingTime(target) * ((i + 1.0) / needed.Count));

        var staffedAll = true;
        foreach (var seat in needed)
        {
            if (_seatsEmitted.Contains(seat.Function)) continue;
            if (context.SimTime < _seatDue[seat.Function])
            {
                staffedAll = false;
                continue;
            }
            _seatsEmitted.Add(seat.Function);
            context.EmitPerceived(new EocSeatStaffed(seat.Function, seat.Agency), EventSources.Comms);
        }
        if (!staffedAll) return;

        context.EmitPerceived(new EocLevelReached(target), EventSources.Comms);
        _seatDue.Clear();
        _seatsEmitted.Clear();
        var text = target >= EocLevel.Partial
            ? $"EOC: at {EocLevels.Label(target).ToLowerInvariant()}. A major emergency is declared; agency representatives are in, and reserves can be released through us."
            : "EOC: monitoring. We're watching the incident; hourly situation reports please.";
        CommsNet.Chat(context, "EOC", text, null, TimeSpan.Zero);
    }

    public static bool SitrepOverdue(WorldState world, DateTimeOffset now)
    {
        var ia = world.Interagency;
        return ia.SitrepDue is { } due && now > due && (ia.LastSitrepAt is null || ia.LastSitrepAt < due - (EocLevels.SitrepInterval(ia.EocLevel) ?? TimeSpan.Zero));
    }

    /// <summary>The council's duty officer suggests standing the EOC up once an incident is clearly a major one.</summary>
    private void Advise(SimulationContext context)
    {
        var ia = context.World.Interagency;
        foreach (var (id, facts) in ia.Incidents)
            if (facts.Priority >= IncidentPriority.High && !facts.Closed) _highSince.TryAdd(id, context.SimTime);
        if (_advised || ia.EocLevel >= EocLevel.Partial || ia.EocRequested >= EocLevel.Partial) return;
        var major = _highSince.Values.Any(t => context.SimTime - t >= EocAdviceAfter)
                    || context.World.ProtectiveActions.Values.Any(a => a.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace);
        if (!major) return;
        _advised = true;
        CommsNet.Chat(context, "Duty emergency management officer",
            "Duty emergency management officer, Dublin City Council: we're hearing a lot about this incident. Do you want the EOC stood up? " +
            "Partial activation declares a major emergency and lets us pull in the other agencies' reserves.", null, TimeSpan.Zero);
    }

    // ---- Public information ----

    private void Media(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        foreach (var (incidentId, facts) in ia.Incidents)
        {
            if (facts.Closed || facts.Priority < IncidentPriority.Medium) continue;
            if (!_nextEnquiry.TryGetValue(incidentId, out var next))
            {
                _nextEnquiry[incidentId] = Later(facts.CreatedAt + FirstMediaEnquiry, context.SimTime);
                continue;
            }

            var count = _enquiries.GetValueOrDefault(incidentId);
            // Taken over by the instructor (Phase 11 M4): journalists ring only when they play one.
            if (context.SimTime >= next && count < 8 && !world.PlayedByHuman(Instructor.AiRoles.Media))
            {
                _enquiries[incidentId] = count + 1;
                _nextEnquiry[incidentId] = context.SimTime + facts.Priority switch
                {
                    IncidentPriority.Critical => TimeSpan.FromMinutes(4),
                    IncidentPriority.High => TimeSpan.FromMinutes(6),
                    _ => TimeSpan.FromMinutes(9),
                };
                var random = SimRandom.For(incidentId, 40 + count);
                var outlet = Outlets[random.Next(Outlets.Length)];
                var place = facts.Place;
                string[] questions =
                [
                    $"What's happening at {place}? Is anyone hurt?",
                    $"We're hearing about an explosion near {place}. Is there a danger to the public?",
                    $"Is it true there's a toxic cloud over the docklands? Should people stay indoors?",
                    $"How many casualties are there at {place}, and where are they being taken?",
                    $"Are the roads around {place} closed? When will they reopen?",
                    $"Was the explosion deliberate? Are gardaí investigating?",
                ];
                var question = questions[count < questions.Length ? count : random.Next(questions.Length)];
                CommsNet.Chat(context, outlet, $"{outlet}: {question}", new MediaEnquiryReceived(Guid.NewGuid(), incidentId, outlet, question));
            }

            var waiting = ia.Enquiries.Values.Where(e => e.Enquiry.IncidentId == incidentId).ToList();
            if (waiting.Count == 0) continue;
            var command = world.Commands.GetValueOrDefault(incidentId);

            // The PIO answers with a holding statement, passing on any protective advice in force.
            if (command?.Positions.GetValueOrDefault(IcsRole.PublicInformation) is { } pio
                && waiting.Min(e => e.At) + PioAnswerDelay <= context.SimTime)
            {
                var joint = command.IsUnified || ia.MajorEmergency;
                context.EmitPerceived(new PublicStatementIssued(Guid.NewGuid(), incidentId, $"PIO {pio}", HoldingStatement(world, incidentId, facts), joint),
                    EventSources.Engine);
                continue;
            }

            // Nobody answering: speculation fills the gap, and worried people ring 999.
            var oldest = waiting.Min(e => e.At);
            if (context.SimTime - oldest < SpeculationAfter) continue;
            if (_lastSpeculation.TryGetValue(incidentId, out var last) && context.SimTime - last < TimeSpan.FromMinutes(15)) continue;
            _lastSpeculation[incidentId] = context.SimTime;
            var rumourAt = GeoMath.Destination(facts.Location, 45, 600);
            context.EmitPerceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Media, "Social media post",
                $"No word from the authorities about what is going on at {facts.Place}. People saying there's a second explosion and toxic gas spreading towards Ringsend!!",
                Confidence.Low, VerificationStatus.Suspected, rumourAt, 800), EventSources.Comms);
            CommsNet.EmergencyCall(context, new PendingCall
            {
                Caller = "999 caller (worried resident)", Summary = "I've seen online there's toxic gas coming this way. Is it safe to stay here? Nobody is telling us anything.",
                Location = rumourAt, AccuracyMeters = 900, Patience = TimeSpan.FromMinutes(2),
            });
        }
    }

    private static string HoldingStatement(WorldState world, Guid incidentId, IncidentFacts facts)
    {
        var where = facts.Address is { } address ? $" at {address}" : "";
        var text = $"Emergency services are dealing with a {EventDescriber.Humanize(facts.Type).ToLowerInvariant()}{where}. " +
                   "Please keep clear so crews can work.";
        var zones = world.ProtectiveActions.Values.Where(a => world.Interagency.IncidentZones.TryGetValue(incidentId, out var own)
                                                               && own.Any(z => z.Intersects(a.Area))).ToList();
        if (zones.Any(z => z.Type == ZoneType.EvacuationZone))
            text += " Residents inside the evacuation zone should evacuate now and follow the directions of gardaí.";
        if (zones.Any(z => z.Type == ZoneType.ShelterInPlace))
            text += " People inside the shelter zone should stay indoors and keep windows and doors closed.";
        return text + " Further updates will follow.";
    }

    /// <summary>
    /// Without a joint information centre, another agency puts out its own line a few minutes after a protective zone,
    /// contradicting it.
    /// </summary>
    private void Contradictions(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        foreach (var action in world.ProtectiveActions.Values)
        {
            if (action.Type is not (ZoneType.EvacuationZone or ZoneType.ShelterInPlace) || _contradicted.Contains(action.ZoneId)) continue;
            if (context.SimTime - action.DeclaredAt < ContradictionAfter) continue;
            _contradicted.Add(action.ZoneId);

            var jointSince = ia.Statements.Any(s => s.Statement.Joint && !s.Statement.Conflicting && s.At >= action.DeclaredAt - TimeSpan.FromMinutes(5));
            var jic = world.Commands.Values.Any(c => c.IsStaffed(IcsRole.PublicInformation)) && (ia.MajorEmergency || world.Commands.Values.Any(c => c.IsUnified));
            if (jointSince || jic) continue; // the agencies agree their messages: nobody goes off on their own

            var speaker = world.Agencies.Where(a => a.Value.AiControlled)
                .OrderBy(a => a.Value.Type switch { AgencyType.EmergencyManagement => 0, AgencyType.Police => 1, _ => 2 })
                .ThenBy(a => a.Value.Name, StringComparer.Ordinal)
                .Select(a => a.Value.Name)
                .FirstOrDefault();
            if (speaker is null) continue;
            var text = action.Type == ZoneType.ShelterInPlace
                ? $"{speaker}: there is no danger to the public at this time. Roads in the area remain open and people can go about their business."
                : $"{speaker}: residents near the incident do not need to leave their homes at this time.";
            context.EmitPerceived(new PublicStatementIssued(Guid.NewGuid(), null, speaker, text, Joint: false, Conflicting: true), EventSources.Comms);
        }
    }

    // ---- Pressure ----

    /// <summary>An objection left unanswered, or the media left without answers, comes back as an alert.</summary>
    private void Chase(SimulationContext context)
    {
        var ia = context.World.Interagency;
        foreach (var dispute in ia.Disputes.Values.Where(d => d.Open && d.Chosen is null && context.SimTime - d.RaisedAt >= DisputeChaseAfter))
        {
            if (!_chased.Add(dispute.Raised.DisputeId)) continue;
            context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.SituationChange, AlertSeverity.Warning,
                $"{dispute.Raised.Agency} is waiting for an answer",
                $"{dispute.Raised.Subject}. Concede, negotiate, assert your authority or escalate to the EOC: until then, {(dispute.Raised.ZoneId is null ? "the question stays open" : "the zone stops at their boundary")}.",
                dispute.Raised.IncidentId, null), EventSources.Comms);
        }
        foreach (var group in ia.Enquiries.Values.GroupBy(e => e.Enquiry.IncidentId))
        {
            if (group.Count() < 2 || context.SimTime - group.Min(e => e.At) < TimeSpan.FromMinutes(8)) continue;
            if (!_mediaChased.Add(group.Key ?? Guid.Empty)) continue;
            context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.SituationChange, AlertSeverity.Warning,
                "Media enquiries unanswered",
                $"{group.Count()} journalists are waiting for answers. Appoint a PIO or issue a statement before speculation fills the gap.",
                group.Key, null), EventSources.Comms);
        }
    }

    // ---- Names ----

    public static string CommanderOf(Guid agencyId, WorldAgency agency)
    {
        var surname = Surnames[SimRandom.For(agencyId, 11).Next(Surnames.Length)];
        var rank = agency.Type switch
        {
            AgencyType.Police => "Inspector",
            AgencyType.Fire => "District Officer",
            AgencyType.Ems => "Advanced Paramedic Officer",
            AgencyType.EmergencyManagement => "Senior Executive Officer",
            AgencyType.PublicHealth => "Dr",
            AgencyType.Investigation => "Detective Superintendent",
            AgencyType.Regulator => "Inspector",
            AgencyType.AreaAuthority => "Estate Manager",
            AgencyType.CoastGuard => "Coast Guard Officer",
            _ => "Officer",
        };
        return $"{rank} {surname}";
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
