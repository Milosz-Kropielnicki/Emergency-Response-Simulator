using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Everyone on the other end of command's orders and requests (Design Document §9, §14, §16):
/// <list type="bullet">
/// <item>Crews and supervisors read orders back. An overloaded supervisor (span of control over 7) passes
/// orders on more slowly and sometimes loses or garbles them (§8.1). A unit with a dead radio never answers.</item>
/// <item>Approvers decide on resource requests: local additional resources need no approval; mutual aid needs
/// the Regional Duty Officer; specialist teams need the National Directorate. Both judge the incident's priority.</item>
/// <item>Providers deliver approved resources: new units join the roster at the edge of the area and drive in.</item>
/// <item>Outside organisations answer notifications, and requesters acknowledge command's decisions.</item>
/// </list>
/// Chance outcomes are seeded from the event's id so replays and tests are reproducible.
/// </summary>
public sealed class CommandResponseSystem : ISimulationSystem
{
    public static readonly TimeSpan UnitReadBackDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SupervisorReadBackDelay = TimeSpan.FromSeconds(35);

    /// <summary>A radio that comes back within this time still gets the order through; after it, the order is lost.</summary>
    public static readonly TimeSpan OrderGiveUp = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MutualAidDecisionDelay = TimeSpan.FromMinutes(2.5);
    public static readonly TimeSpan SpecialistDecisionDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AdditionalLeadTime = TimeSpan.FromMinutes(8);
    public static readonly TimeSpan MutualAidLeadTime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SpecialistLeadTime = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan ApprovalReplyDelay = TimeSpan.FromSeconds(30);

    /// <summary>Where outside resources enter the operational area (western and southern approaches).</summary>
    public static readonly GeoPoint[] EntryPoints = [new(53.3468, -6.2985), new(53.3265, -6.2590)];

    private readonly Dictionary<string, Guid> _providerAgencies = [];
    private int _arrivals;

    public int Order => 20;

    public void Update(SimulationContext context)
    {
        AnswerOrders(context);
        ProgressRequests(context);
        AnswerNotifications(context);
        ReplyToDecisions(context);
    }

    // ---- Orders ----

    private static void AnswerOrders(SimulationContext context)
    {
        var world = context.World;
        foreach (var pending in world.PendingOrders.ToList())
        {
            var order = pending.Order;

            // With communications simulated, nobody can answer an order they never heard (Phase 7).
            if (world.Radio.Active && pending.HeardAt is null)
            {
                if (context.SimTime - pending.IssuedAt >= OrderGiveUp)
                    world.PendingOrders.Remove(pending);
                continue;
            }

            var span = SupervisingSpan(world, order);
            var baseDelay = order.TargetKind == OrderTargetKind.Unit ? UnitReadBackDelay : SupervisorReadBackDelay;
            var due = (pending.HeardAt ?? pending.IssuedAt) + baseDelay * (span?.DelayFactor ?? 1);
            if (context.SimTime < due) continue;

            // A unit with a dead radio can't answer; keep trying until we give up.
            if (order.TargetKind == OrderTargetKind.Unit && order.TargetId is { } unitId
                && world.Units.TryGetValue(unitId, out var unit) && unit.RadioFailed)
            {
                if (context.SimTime - pending.IssuedAt >= OrderGiveUp)
                    world.PendingOrders.Remove(pending);
                continue;
            }

            world.PendingOrders.Remove(pending);
            var roll = Roll(order.OrderId);
            var loss = span?.LossProbability ?? 0;
            if (roll < loss)
                continue; // lost in the noise of an overloaded supervisor: nobody reads it back

            // The next band of bad luck: heard, but not properly. Or it was heard badly over the radio.
            var garbled = roll < loss * 2 || pending.HeardGarbled;
            var speaker = order.TargetName;
            var readBack = garbled
                ? $"{speaker}: copy… {string.Join(' ', order.Text.Split(' ').Take(3))}… say again, you're breaking up"
                : $"{speaker}: copy, {order.Text}";
            var ack = new OrderAcknowledged(order.OrderId, readBack, garbled);
            if (order.TargetKind == OrderTargetKind.Unit && order.TargetId is { } speakingUnit && world.Units.TryGetValue(speakingUnit, out var crew))
                CommsNet.Voice(context, crew, $"Control, {readBack}", ack, priority: 4);
            else
                CommsNet.Voice(context, RadioPlan.FireCommand, speaker, $"Control, {readBack}", ack);
        }
    }

    /// <summary>The span of control the order has to pass through, or null when there is no structure to judge.</summary>
    private static SpanEntry? SupervisingSpan(WorldState world, OrderIssued order)
    {
        if (order.IncidentId is not { } incidentId || !world.Commands.TryGetValue(incidentId, out var command))
            return null;

        var units = world.UnitsAt(incidentId);
        var spans = SpanOfControl.Assess(command, units);
        return order.TargetKind switch
        {
            OrderTargetKind.Unit when order.TargetId is { } unitId && units.Contains(unitId) =>
                SpanOfControl.SupervisorOf(unitId, command, units),
            // Orders to a group go through Operations (or the IC); orders to command/general staff come from the IC.
            OrderTargetKind.Group => spans.First(),
            _ => spans.FirstOrDefault(s => s.Key == SpanOfControl.SupervisorKey(IcsRole.IncidentCommander)),
        };
    }

    // ---- Resource requests ----

    private void ProgressRequests(SimulationContext context)
    {
        var world = context.World;
        foreach (var pending in world.PendingRequests.ToList())
        {
            var request = pending.Request;
            if (!pending.Decided)
            {
                var (approver, delay, lead) = request.Kind switch
                {
                    ResourceRequestKind.MutualAid => ("Regional Duty Officer", MutualAidDecisionDelay, MutualAidLeadTime),
                    ResourceRequestKind.SpecialistTeam => ("National Directorate for Fire & Emergency Management", SpecialistDecisionDelay, SpecialistLeadTime),
                    _ => (ControlRoomFor(request.UnitType), TimeSpan.Zero, AdditionalLeadTime),
                };
                if (context.SimTime - pending.RequestedAt < delay) continue;

                pending.Decided = true;
                var priority = world.ReportedIncidentPriorities.GetValueOrDefault(request.IncidentId, IncidentPriority.Medium);
                var approved = request.Kind == ResourceRequestKind.AdditionalResources || priority >= IncidentPriority.High;
                string? reason = approved
                    ? null
                    : $"Declined: {request.Kind switch { ResourceRequestKind.MutualAid => "mutual aid", _ => "specialist teams" }} " +
                      $"are reserved for High or Critical incidents; this incident is {priority}.";

                pending.ArriveAt = approved ? context.SimTime + lead : null;
                CommsNet.Chat(context, approver,
                    approved ? $"Request approved: {request.Description}. Expected on scene {pending.ArriveAt?.ToLocalTime():HH:mm}." : reason!,
                    new ResourceRequestDecided(request.RequestId, approved, approver, reason, pending.ArriveAt), TimeSpan.Zero);
                if (!approved) world.PendingRequests.Remove(pending);
                continue;
            }

            if (pending.ArriveAt is { } arriveAt && context.SimTime >= arriveAt)
            {
                world.PendingRequests.Remove(pending);
                Deliver(context, request);
            }
        }
    }

    /// <summary>New units join the roster at the edge of the area and are dispatched straight to the incident.</summary>
    private void Deliver(SimulationContext context, ResourceRequested request)
    {
        var unitType = request.UnitType ?? UnitType.Engine;
        var agencyType = ResourceGroups.AgencyFor(unitType);
        var provider = request.Kind switch
        {
            ResourceRequestKind.MutualAid => agencyType switch
            {
                AgencyType.Fire => "Kildare Fire Service",
                AgencyType.Ems => "National Ambulance Service (Midlands)",
                AgencyType.Police => "Garda Kildare Division",
                _ => "Kildare County Council",
            },
            ResourceRequestKind.SpecialistTeam => "National Specialist Teams",
            _ => null, // our own service, from outside the operational area
        };

        Guid? agencyId = provider is not null
            ? ProviderAgency(context, provider, agencyType)
            : context.World.Agencies.Where(a => a.Value.Type == agencyType && !a.Value.AiControlled)
                .Select(a => (Guid?)a.Key).FirstOrDefault();

        var prefix = provider is null ? "Relief" : provider.Split(' ')[0];
        var entry = EntryPoints[_arrivals % EntryPoints.Length];
        var units = new List<Guid>();
        for (var i = 0; i < request.Quantity; i++)
        {
            var unitId = Guid.NewGuid();
            var callsign = $"{prefix} {ResourceGroups.Label(unitType)} {++_arrivals}";
            var start = GeoMath.Destination(entry, 90 * i, 15 * i); // don't stack them on one point
            context.EmitPerceived(new UnitRegistered(unitId, callsign, unitType, agencyId, start,
                provider ?? "Outside operational area", unitType is UnitType.AmbulanceAls or UnitType.AmbulanceBls ? 2 : 4,
                []), EventSources.Engine);
            context.EmitPerceived(new UnitDispatched(unitId, request.IncidentId, null), EventSources.Engine);
            units.Add(unitId);
        }

        context.EmitPerceived(new ResourceRequestFulfilled(request.RequestId, units), EventSources.Engine);
    }

    private Guid ProviderAgency(SimulationContext context, string provider, AgencyType type)
    {
        if (_providerAgencies.TryGetValue(provider, out var id)) return id;
        id = Guid.NewGuid();
        _providerAgencies[provider] = id;
        var shortName = string.Concat(provider.Split(' ').Select(w => char.IsUpper(w[0]) ? w[0].ToString() : ""));
        context.EmitPerceived(new AgencyRegistered(id, provider, shortName, type, RadioChannel: ProviderRadio(provider)), EventSources.Engine);
        return id;
    }

    /// <summary>
    /// Outside services bring their own radios (Design Document §13: interoperability). Garda divisions share the
    /// national Garda system, so they are on ours.
    /// </summary>
    public static string? ProviderRadio(string provider) => provider switch
    {
        _ when provider.StartsWith("Garda") => null,
        _ when provider.StartsWith("Kildare") => "KILDARE FIRE",
        _ when provider.Contains("Ambulance") => "NAS MIDLANDS",
        _ => "NATIONAL TEAMS",
    };

    private static string ControlRoomFor(UnitType? type) => ResourceGroups.AgencyFor(type ?? UnitType.Engine) switch
    {
        AgencyType.Fire => "Fire Control",
        AgencyType.Ems => "Ambulance Control",
        AgencyType.Police => "Garda Command & Control",
        _ => "Emergency Management Control",
    };

    // ---- Notifications and decisions ----

    private static void AnswerNotifications(SimulationContext context)
    {
        foreach (var pending in context.World.PendingNotifications.ToList())
        {
            var notification = pending.Notification;
            var delay = TimeSpan.FromSeconds(60 + Roll(notification.NotificationId) * 90);
            if (context.SimTime - pending.SentAt < delay) continue;

            context.World.PendingNotifications.Remove(pending);
            var reply = ReplyFrom(notification.Recipient);
            CommsNet.Chat(context, notification.Recipient, reply, new NotificationAnswered(notification.NotificationId, reply), TimeSpan.Zero);
        }
    }

    /// <summary>A plausible reply from an outside organisation, by who it is.</summary>
    public static string ReplyFrom(string recipient)
    {
        var who = recipient.ToLowerInvariant();
        if (who.Contains("hospital"))
            return $"{recipient}: acknowledged. Major emergency plan activated; we can receive up to 8 P1 and 15 P2 casualties.";
        if (who.Contains("esb") || who.Contains("power") || who.Contains("electric"))
            return $"{recipient}: crew dispatched; we can isolate supply to the affected block in about 30 minutes.";
        if (who.Contains("gas"))
            return $"{recipient}: emergency crew en route to isolate the gas supply.";
        if (who.Contains("water") || who.Contains("uisce"))
            return $"{recipient}: noted. We will boost mains pressure in the area for firefighting.";
        if (who.Contains("met "))
            return $"{recipient}: wind expected to back further to the south-west, 6–8 m/s for the next three hours.";
        if (who.Contains("council") || who.Contains("local authority"))
            return $"{recipient}: crisis management team convening. Buses and a rest centre can be made available for evacuees.";
        if (who.Contains("hse") || who.Contains("public health"))
            return $"{recipient}: public health on standby. Advise residents downwind to stay indoors with windows closed.";
        if (who.Contains("government") || who.Contains("necg") || who.Contains("national emergency"))
            return $"{recipient}: noted. Please provide a situation report every 30 minutes.";
        if (who.Contains("port"))
            return $"{recipient}: shipping movements in the basin suspended until further notice.";
        if (who.Contains("transport") || who.Contains("luas") || who.Contains("rail") || who.Contains("dart"))
            return $"{recipient}: services through the area suspended; diversions in place.";
        return $"{recipient}: message received.";
    }

    private static void ReplyToDecisions(SimulationContext context)
    {
        foreach (var pending in context.World.PendingApprovalReplies.ToList())
        {
            if (context.SimTime - pending.DecidedAt < ApprovalReplyDelay) continue;
            context.World.PendingApprovalReplies.Remove(pending);

            var decision = pending.Decision;
            var claim = decision.Approved ? "Understood, approved. Proceeding now." : "Understood, not approved. Holding.";
            if (decision.Note is { } note) claim += $" ({note})";
            CommsNet.Chat(context, pending.Requester, claim, new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, pending.Requester, claim,
                Confidence.High, VerificationStatus.Confirmed, null, null), TimeSpan.Zero);
        }
    }

    /// <summary>A stable pseudo-random number in [0, 1) for an id.</summary>
    public static double Roll(Guid id) => (uint)id.GetHashCode() / (double)uint.MaxValue;
}
