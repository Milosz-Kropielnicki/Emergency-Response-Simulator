using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Comms;

/// <summary>How well a crew's radio can be heard where it is.</summary>
public enum Reach
{
    Good,

    /// <summary>Weak battery: the signal breaks up.</summary>
    Weak,

    /// <summary>In a black spot: mostly lost, the rest garbled.</summary>
    BlackSpot,

    /// <summary>Radio failed or battery flat: nothing gets through.</summary>
    None,
}

/// <summary>
/// How everyone in the world talks to command (Design Document §13). Systems say what they want to say through
/// these, and <c>CommsSystem</c> decides when (and whether) it is heard:
/// <list type="bullet">
/// <item><see cref="Voice"/>: crews on the radio (reports, read-backs). Takes airtime; can queue, collide, garble or be lost.</item>
/// <item><see cref="Data"/>: status buttons and AVL over mobile data. Needs coverage; status messages wait for it, fixes don't.</item>
/// <item><see cref="Chat"/>: other control rooms, utilities and hospitals, by phone or message. A short delay.</item>
/// <item><see cref="EmergencyCall"/>: the public ringing 999, answered by a limited number of call-takers.</item>
/// </list>
/// Without a comms system running (tests of other systems, the scripted-only world) messages go straight to the
/// COP as they did before, except from a unit whose radio has failed.
/// </summary>
public static class CommsNet
{
    /// <summary>Handheld battery below this breaks up transmissions.</summary>
    public const double LowBattery = 0.15;

    public static void Voice(SimulationContext context, WorldUnit unit, string text, DomainEvent? payload, int priority = 0)
    {
        var radio = context.World.Radio;
        if (!radio.Active)
        {
            if (!unit.RadioFailed && payload is not null) context.EmitPerceived(payload, EventSources.Comms);
            return;
        }
        radio.Outbox.Add(new Transmission
        {
            Channel = unit.Channel, From = unit.Callsign, UnitId = unit.Id, To = "Control", Text = text, Payload = payload,
            Priority = priority, QueuedAt = context.SimTime, NotBefore = context.SimTime,
        });
    }

    /// <summary>A crew's spoken report: "Control, Engine 4: …", without repeating a call sign the report already starts with.</summary>
    public static void Report(SimulationContext context, WorldUnit unit, ReportReceived report, int priority = 1) =>
        Voice(context, unit, report.Claim.StartsWith(unit.Callsign, StringComparison.Ordinal)
            ? $"Control, {report.Claim}"
            : $"Control, {unit.Callsign}: {report.Claim}", report, priority);

    /// <summary>Someone without a tracked unit on the radio, e.g. a group supervisor reading back an order.</summary>
    public static void Voice(SimulationContext context, string channel, string from, string text, DomainEvent? payload)
    {
        var radio = context.World.Radio;
        if (!radio.Active)
        {
            if (payload is not null) context.EmitPerceived(payload, EventSources.Comms);
            return;
        }
        radio.Outbox.Add(new Transmission
        {
            Channel = channel, From = from, To = "Control", Text = text, Payload = payload,
            QueuedAt = context.SimTime, NotBefore = context.SimTime,
        });
    }

    /// <summary>Mobile data from a vehicle: status messages are stored and forwarded; position fixes are just lost.</summary>
    public static void Data(SimulationContext context, WorldUnit unit, DomainEvent payload, string source)
    {
        if (unit.RadioFailed) return; // the whole comms fit has failed, as before Phase 7
        if (!context.World.Radio.Active || DataUp(context.World, unit.Location))
        {
            context.EmitPerceived(payload, source);
            return;
        }
        if (payload is not (UnitPositionReported or RouteReported))
            unit.PendingData.Add((payload, source));
    }

    /// <summary>A message from another organisation, arriving after the time it takes to phone or type it.</summary>
    public static void Chat(SimulationContext context, string from, string text, DomainEvent? payload, TimeSpan? delay = null)
    {
        var radio = context.World.Radio;
        if (!radio.Active)
        {
            if (payload is not null) context.EmitPerceived(payload, EventSources.Comms);
            return;
        }
        radio.Outbox.Add(new Transmission
        {
            Kind = TransmissionKind.Chat, Channel = RadioPlan.Chat, From = from, To = "Control", Text = text, Payload = payload,
            QueuedAt = context.SimTime, NotBefore = context.SimTime + (delay ?? TimeSpan.FromSeconds(20)),
        });
    }

    /// <summary>A member of the public rings 999.</summary>
    public static void EmergencyCall(SimulationContext context, PendingCall call)
    {
        var radio = context.World.Radio;
        if (!radio.Active)
        {
            context.EmitPerceived(new CallReceived(call.Id, call.Caller, call.Summary, call.Location, call.AccuracyMeters), EventSources.Comms);
            return;
        }
        call.QueuedAt = context.SimTime;
        radio.CallQueue.Add(call);
    }

    /// <summary>Mobile coverage at a point: not in a black spot and not under a mast that is down.</summary>
    public static bool DataUp(WorldState world, GeoPoint point) =>
        !world.Radio.DeadZones.Any(z => z.Covers(point)) && !MastDownOver(world, point);

    /// <summary>Whether the mobile mast serving this point is down.</summary>
    public static bool MastDownOver(WorldState world, GeoPoint point) =>
        world.Radio.Masts.Values.Any(m => m.Down && world.Sites.TryGetValue(m.SiteId, out var site)
                                                 && GeoMath.DistanceMeters(site.Location, point) <= site.ServiceRadiusMeters);

    /// <summary>How likely a weak battery breaks a transmission up: from 20 % just under the threshold to 80 % nearly flat.</summary>
    public static double WeakBatteryGarble(WorldUnit unit) =>
        0.2 + 0.6 * Math.Clamp(1 - unit.Battery / LowBattery, 0, 1);

    public static Reach ReachOf(WorldState world, WorldUnit unit)
    {
        if (unit.RadioFailed || unit.Battery <= 0) return Reach.None;
        if (world.Radio.DeadZones.Any(z => z.Covers(unit.Location))) return Reach.BlackSpot;
        return unit.Battery < LowBattery ? Reach.Weak : Reach.Good;
    }
}
