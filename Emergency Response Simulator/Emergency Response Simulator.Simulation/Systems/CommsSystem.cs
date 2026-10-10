using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>Tuning for communications (bound from the "Comms" settings section).</summary>
public sealed class CommsOptions
{
    public const string SectionName = "Comms";

    /// <summary>999 call-takers on duty.</summary>
    public int CallTakers { get; set; } = 3;

    public TimeSpan MinCallHandling { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxCallHandling { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>A crew gives up on a message that has waited this long for a gap on a busy channel.</summary>
    public TimeSpan QueueGiveUp { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Time for a technician to set up a patch between channels.</summary>
    public TimeSpan PatchSetupTime { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a mobile mast runs on its batteries in a power cut.</summary>
    public TimeSpan MastBatteryLife { get; set; } = TimeSpan.FromMinutes(45);

    /// <summary>Handheld battery used per hour by a crew working at a scene.</summary>
    public double BatteryDrainPerHour { get; set; } = 0.25;

    /// <summary>Time for a crew to swap a low battery for a charged one from the appliance.</summary>
    public TimeSpan BatterySwapTime { get; set; } = TimeSpan.FromMinutes(8);

    /// <summary>Chance that an ordinary transmission is garbled for no particular reason (noise, a bad mic).</summary>
    public double BackgroundGarble { get; set; } = 0.02;

    /// <summary>Chance that an ordinary transmission has words missing.</summary>
    public double BackgroundBroken { get; set; } = 0.06;
}

/// <summary>
/// Communications as they really work (Design Document §13): the fog of war between the world and the COP.
/// <list type="bullet">
/// <item><b>Radio</b>: each channel carries one transmission at a time (patched channels share their airtime).
/// Crews wait for a gap, emergency traffic first, and give up after 90 s on a jammed channel. Busy channels, weak
/// batteries and black spots garble or lose messages; a channel command doesn't monitor (another service's radio
/// system, unpatched) is simply not heard.</item>
/// <item><b>Command's transmissions</b>: orders go out when the channel is free; push-to-talk calls go out at once and
/// can double with someone already talking, or lose their end if the button is let go too early. Radio-discipline
/// notes are attached. Crews answer if they heard their call sign: "copy", a status, or "say again".</item>
/// <item><b>Degradation</b>: handheld batteries run down at the scene; mobile masts run on batteries in a power cut and
/// then fail, taking out mobile 999 calls and mobile data (status and AVL wait for coverage).</item>
/// <item><b>The 999 line</b>: a few call-takers; callers queue, hang up (missed calls, which command can ring back) and
/// can't get through on a dead mast. Callers with little English give little detail until an interpreter joins.</item>
/// <item><b>Agency chat</b>: other control rooms relay what their own crews say, since command can't hear them.</item>
/// </list>
/// Chance outcomes are seeded from message ids so a session replays the same way.
/// </summary>
public sealed class CommsSystem(CommsOptions? options = null, IRoutingService? routing = null) : ISimulationSystem
{
    private readonly CommsOptions _options = options ?? new CommsOptions();
    private readonly List<DateTimeOffset> _callTakersFreeAt = [];
    private readonly HashSet<Guid> _batteriesChecked = [];
    private readonly HashSet<Guid> _foreignNudged = [];
    private readonly List<(DateTimeOffset At, DomainEvent Payload, string From, string Text)> _interpreters = [];

    public int Order => 95; // after everyone has said what they want to say, before attention looks at the COP

    /// <summary>From the start, messages go through the simulated network rather than straight to the COP.</summary>
    public void Attach(WorldState world) => world.Radio.Active = true;

    public void Update(SimulationContext context)
    {
        var radio = context.World.Radio;
        ActivatePatches(context);
        Masts(context);
        Batteries(context);
        FlushData(context);
        Repeats(context);
        EmergencyLine(context);
        Callbacks(context);
        Interpreters(context);

        radio.Queue.AddRange(radio.Outbox);
        radio.Outbox.Clear();

        Chats(context);
        Keyed(context);
        Air(context);

        foreach (var used in radio.Airtime.Values)
            used.RemoveAll(a => context.SimTime - a.Start > TimeSpan.FromMinutes(5));
    }

    private static double Roll(Guid id, int salt = 0) => SimRandom.For(id, salt).NextDouble();

    private static bool Monitored(WorldState world, string channel) =>
        world.Radio.Linked(channel).Any(c => RadioPlan.Find(c)?.Monitored == true);

    // ---- Airtime ----

    /// <summary>Queued traffic goes out channel by channel, one transmission at a time, most urgent first.</summary>
    private void Air(SimulationContext context)
    {
        var radio = context.World.Radio;
        var now = context.SimTime;
        foreach (var group in radio.Queue.Where(t => t.Kind == TransmissionKind.Voice).GroupBy(t => radio.AirtimeGroup(t.Channel)).ToList())
        {
            var freeAt = radio.FreeAt.GetValueOrDefault(group.Key, DateTimeOffset.MinValue);
            var waiting = group.ToList();
            while (true)
            {
                var next = waiting.Where(t => t.NotBefore <= now).OrderByDescending(t => t.Priority).ThenBy(t => t.QueuedAt).FirstOrDefault();
                if (next is null) break;
                var start = freeAt > next.NotBefore ? freeAt : next.NotBefore;
                if (start > now) break;

                waiting.Remove(next);
                radio.Queue.Remove(next);

                // Nobody waits for ever on a jammed channel; command's own traffic always gets out eventually.
                if (!next.FromControl && start - next.QueuedAt > _options.QueueGiveUp)
                {
                    Lost(context, next, "gave up waiting for a gap on a congested channel");
                    continue;
                }

                var duration = Deliver(context, next, collisionWith: null);
                if (duration <= TimeSpan.Zero) continue;
                freeAt = start + duration;
                Record(radio, group.Key, start, duration, next.From);
            }
            radio.FreeAt[group.Key] = freeAt;
        }
    }

    /// <summary>Command keying up with push-to-talk goes out now, even over someone else.</summary>
    private void Keyed(SimulationContext context)
    {
        var radio = context.World.Radio;
        foreach (var call in radio.Queue.Where(t => t.Keyed && t.NotBefore <= context.SimTime).ToList())
        {
            radio.Queue.Remove(call);
            var group = radio.AirtimeGroup(call.Channel);
            string? doubled = radio.OnAir.TryGetValue(group, out var air) && air.Until > context.SimTime ? air.Talker : null;
            var duration = Deliver(context, call, doubled);
            var start = context.SimTime;
            Record(radio, group, start, duration, "Control");
            if (start + duration > radio.FreeAt.GetValueOrDefault(group, DateTimeOffset.MinValue))
                radio.FreeAt[group] = start + duration;
        }
    }

    private static void Record(RadioWorld radio, string group, DateTimeOffset start, TimeSpan duration, string talker)
    {
        if (!radio.Airtime.TryGetValue(group, out var used))
            radio.Airtime[group] = used = [];
        used.Add((start, duration));
        radio.OnAir[group] = (talker, start + duration);
    }

    /// <summary>One transmission on the air. Returns the airtime it used (zero if it never went out).</summary>
    private TimeSpan Deliver(SimulationContext context, Transmission transmission, string? collisionWith)
    {
        return transmission.FromControl
            ? DeliverFromControl(context, transmission, collisionWith)
            : DeliverToControl(context, transmission);
    }

    private TimeSpan DeliverToControl(SimulationContext context, Transmission transmission)
    {
        var world = context.World;
        var unit = transmission.UnitId is { } id ? world.Units.GetValueOrDefault(id) : null;
        var reach = unit is null ? Reach.Good : CommsNet.ReachOf(world, unit);
        if (reach == Reach.None)
        {
            Lost(context, transmission, unit?.RadioFailed == true ? "radio failed" : "radio battery flat");
            return TimeSpan.Zero;
        }

        var duration = RadioPlan.Airtime(transmission.Text);
        if (unit is not null) unit.Battery = Math.Max(0, unit.Battery - 0.003);
        var roll = Roll(transmission.Id, transmission.Attempts);

        // In a black spot most transmissions never make it; the crew tries again a little later.
        if (reach == Reach.BlackSpot && roll < 0.7)
        {
            Lost(context, transmission, "radio black spot");
            if (transmission.Attempts < 3)
            {
                transmission.Attempts++;
                transmission.NotBefore = context.SimTime + TimeSpan.FromSeconds(45);
                transmission.QueuedAt = transmission.NotBefore;
                world.Radio.Queue.Add(transmission);
            }
            return duration;
        }

        if (!Monitored(world, transmission.Channel))
        {
            Lost(context, transmission, $"{transmission.Channel} is not monitored by command");
            Unmonitored(context, transmission, unit);
            return duration;
        }

        var garble = _options.BackgroundGarble + (world.Radio.Utilisation(transmission.Channel, context.SimTime) > 0.75 ? 0.12 : 0)
                     + (reach == Reach.Weak ? CommsNet.WeakBatteryGarble(unit!) : 0) + (RadioPlan.Words(transmission.Text) > RadioDiscipline.MaxWords ? 0.05 : 0);
        var quality = reach == Reach.BlackSpot ? CommsQuality.Garbled
            : roll < garble ? CommsQuality.Garbled
            : Roll(transmission.Id, transmission.Attempts + 100) < _options.BackgroundBroken ? CommsQuality.Broken
            : CommsQuality.Clear;

        var heard = Distort(transmission.Text, quality, transmission.Id);
        context.EmitPerceived(new CommsLogged(transmission.Id, transmission.Channel, transmission.From, transmission.To, heard, quality,
            duration.TotalSeconds, transmission.UnitId), EventSources.Comms);
        world.Radio.Sent[transmission.Id] = transmission;

        var payload = quality switch
        {
            CommsQuality.Clear => transmission.Payload,
            CommsQuality.Broken => transmission.Payload switch
            {
                ReportReceived report => report with { Claim = heard, Confidence = Lower(report.Confidence) },
                OrderAcknowledged ack => ack with { ReadBack = heard, Garbled = true },
                var other => other,
            },
            // Garbled: command knows the crew answered, but not what they said.
            _ => transmission.Payload is OrderAcknowledged ack ? ack with { ReadBack = heard, Garbled = true } : null,
        };
        if (payload is not null)
            context.EmitPerceived(payload, EventSources.Comms);
        return duration;
    }

    private static Confidence Lower(Confidence confidence) => confidence == Confidence.High ? Confidence.Medium : Confidence.Low;

    /// <summary>
    /// A crew on another service's radio: an AI-run agency's control room hears it and relays it by phone; a
    /// mutual-aid service's control room tells command its crews can't get through and asks for a patch.
    /// </summary>
    private void Unmonitored(SimulationContext context, Transmission transmission, WorldUnit? unit)
    {
        if (unit?.AgencyId is not { } agencyId || !context.World.Agencies.TryGetValue(agencyId, out var agency)) return;

        if (agency.AiControlled)
        {
            if (transmission.Payload is null) return;
            var delay = TimeSpan.FromSeconds(60 + Roll(transmission.Id, 7) * 60);
            var said = transmission.Payload is ReportReceived report ? report.Claim : transmission.Text;
            CommsNet.Chat(context, $"{agency.Name} control", $"Relaying from {transmission.From}: {said}", transmission.Payload, delay);
            return;
        }

        if (!_foreignNudged.Add(agencyId)) return;
        var text = $"Our crews can't raise you on the radio: they are on {transmission.Channel}. " +
                   $"Please patch {transmission.Channel} to one of your channels.";
        CommsNet.Chat(context, $"{agency.Name} control", text,
            new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, $"{agency.Name} control", text, Confidence.High,
                VerificationStatus.Confirmed, null, null), TimeSpan.FromSeconds(45));
    }

    private TimeSpan DeliverFromControl(SimulationContext context, Transmission transmission, string? collisionWith)
    {
        var world = context.World;
        var needed = RadioPlan.Airtime(transmission.Text);
        var duration = needed;
        var said = transmission.Text;
        var quality = CommsQuality.Clear;
        var notes = transmission.Keyed ? RadioDiscipline.Review(transmission.Text, transmission.To).ToList() : [];
        var clipped = false;

        if (transmission.HeldSeconds is { } held && held < needed.TotalSeconds)
        {
            var words = transmission.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kept = Math.Clamp((int)((held - 1.5) * 2.5), 0, words.Length);
            said = kept == 0 ? "—" : string.Join(' ', words.Take(kept)) + " —";
            duration = TimeSpan.FromSeconds(Math.Max(0.5, held));
            quality = CommsQuality.Broken;
            clipped = true;
            notes.Add("Released push-to-talk too early: the end of the message was cut off.");
        }
        if (collisionWith is not null)
        {
            quality = CommsQuality.Garbled;
            notes.Add($"Doubled with {collisionWith}: wait for the channel to clear before keying up.");
        }

        context.EmitPerceived(new CommsLogged(transmission.Id, transmission.Channel, "Control", transmission.To, said, quality,
            duration.TotalSeconds, FromControl: true, Notes: notes.Count > 0 ? notes : null), EventSources.Comms);

        if (transmission.OrderId is { } orderId)
            OrderHeard(context, transmission, orderId, quality);
        else
            AnswerCall(context, transmission, said, quality, clipped);
        return duration;
    }

    /// <summary>Whether, and how well, the recipient heard an order (the read-back follows from then).</summary>
    private static void OrderHeard(SimulationContext context, Transmission transmission, Guid orderId, CommsQuality quality)
    {
        var world = context.World;
        var pending = world.PendingOrders.FirstOrDefault(p => p.Order.OrderId == orderId && p.HeardAt is null);
        if (pending is null) return;

        var garbled = quality != CommsQuality.Clear;
        if (pending.Order.TargetKind == OrderTargetKind.Unit && pending.Order.TargetId is { } unitId && world.Units.TryGetValue(unitId, out var unit))
        {
            if (!world.Radio.Linked(transmission.Channel).Contains(unit.Channel))
            {
                Lost(context, transmission, $"{unit.Callsign} is on {unit.Channel}");
                return;
            }
            var reach = CommsNet.ReachOf(world, unit);
            var roll = Roll(transmission.Id, 3);
            if (reach == Reach.None || (reach == Reach.BlackSpot && roll < 0.7))
            {
                Lost(context, transmission, reach == Reach.None ? $"{unit.Callsign} can't receive" : $"{unit.Callsign} in a radio black spot");
                return;
            }
            garbled |= reach == Reach.BlackSpot || (reach == Reach.Weak && roll < CommsNet.WeakBatteryGarble(unit));
        }
        pending.HeardAt = context.SimTime;
        pending.HeardGarbled = garbled;
    }

    /// <summary>A crew answers command's call if it heard its call sign: "copy", a status, or "say again".</summary>
    private void AnswerCall(SimulationContext context, Transmission call, string said, CommsQuality quality, bool clipped)
    {
        var world = context.World;
        var linked = world.Radio.Linked(call.Channel);
        var unit = world.Units.Values
            .Where(u => linked.Contains(u.Channel))
            .FirstOrDefault(u => (call.To is not null && string.Equals(u.Callsign, call.To, StringComparison.OrdinalIgnoreCase)
                                  && RadioDiscipline.Mentions(said, u.Callsign))
                                 || (call.To is null && RadioDiscipline.Mentions(said, u.Callsign)));
        if (unit is null) return; // nobody recognised their call sign: silence

        var reach = CommsNet.ReachOf(world, unit);
        if (reach == Reach.None || (reach == Reach.BlackSpot && Roll(call.Id, 4) < 0.7)) return;

        var callsign = unit.Callsign;
        string text;
        DomainEvent? payload = null;
        if (quality == CommsQuality.Garbled || reach == Reach.BlackSpot)
            text = $"Control, {callsign}, say again, you're breaking up.";
        else if (clipped)
            text = $"Control, {callsign}, you were cut off, say again.";
        else if (AsksForStatus(call.Text))
        {
            text = $"Control, {callsign}: {StatusOf(world, unit)}.";
            payload = new ReportReceived(Guid.NewGuid(), unit.OrderedIncidentId is { } i && !world.AgencyTasks.ContainsKey(i) ? i : null,
                ReportSource.FieldUnit, callsign, StatusOf(world, unit), Confidence.High, VerificationStatus.Confirmed, unit.Location, 30, unit.Id);
        }
        else
            text = $"{callsign}, copy.";

        world.Radio.Queue.Add(new Transmission
        {
            Channel = unit.Channel, From = callsign, UnitId = unit.Id, To = "Control", Text = text, Payload = payload, Priority = 3,
            QueuedAt = context.SimTime, NotBefore = context.SimTime + TimeSpan.FromSeconds(4 + Roll(call.Id, 5) * 4),
        });
    }

    private static bool AsksForStatus(string text)
    {
        var lower = text.ToLowerInvariant();
        return new[] { "status", "sitrep", "situation", "location", "where are you", "eta", "update", "report" }.Any(lower.Contains);
    }

    private string StatusOf(WorldState world, WorldUnit unit)
    {
        var where = routing?.NearestRoad(unit.Location) is { } road && road.DistanceMeters < 80 ? $" on {road.Name}" : "";
        return unit.Phase switch
        {
            ResponsePhase.Idle => $"available{where}",
            ResponsePhase.TurningOut => "turning out",
            ResponsePhase.Travelling => $"en route{where}, about {Math.Max(1, Math.Round(unit.RemainingTime.TotalMinutes))} minutes out",
            ResponsePhase.OnScene => $"on scene{where}, sizing up",
            ResponsePhase.Operating => $"working at the scene{where}",
            ResponsePhase.Transporting => $"transporting to hospital{where}",
            _ => $"at the hospital, handing over",
        } + (unit.BrokenDown ? ", vehicle broken down" : "");
    }

    /// <summary>What a broken or garbled message sounds like: words dropped, the rest fragments.</summary>
    public static string Distort(string text, CommsQuality quality, Guid seed)
    {
        if (quality == CommsQuality.Clear) return text;
        var random = SimRandom.For(seed, 9);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = quality == CommsQuality.Broken ? 0.7 : 0.3;
        var heard = words.Select(w => random.NextDouble() < keep ? w : "…").ToList();
        // Collapse runs of "…".
        var collapsed = heard.Where((w, i) => w != "…" || i == 0 || heard[i - 1] != "…");
        return string.Join(' ', collapsed);
    }

    private static void Lost(SimulationContext context, Transmission transmission, string reason)
    {
        var recent = context.World.Radio.RecentLost;
        recent.Add((context.SimTime, transmission.From, transmission.Text, reason));
        if (recent.Count > 100) recent.RemoveAt(0);
        context.EmitTruth(new TransmissionLost(transmission.Id, transmission.Channel, transmission.From, transmission.Text, reason));
    }

    // ---- Chat ----

    private static void Chats(SimulationContext context)
    {
        var radio = context.World.Radio;
        foreach (var message in radio.Queue.Where(t => t.Kind == TransmissionKind.Chat && t.NotBefore <= context.SimTime).ToList())
        {
            radio.Queue.Remove(message);
            context.EmitPerceived(new CommsLogged(message.Id, RadioPlan.Chat, message.From, message.To, message.Text, CommsQuality.Clear, 0),
                EventSources.Comms);
            if (message.Payload is not null)
                context.EmitPerceived(message.Payload, EventSources.Comms);
        }
    }

    // ---- "Say again" ----

    private static void Repeats(SimulationContext context)
    {
        var radio = context.World.Radio;
        foreach (var id in radio.RepeatRequests.ToList())
        {
            radio.RepeatRequests.Remove(id);
            if (!radio.Sent.TryGetValue(id, out var original) || original.FromControl) continue;
            radio.Queue.Add(new Transmission
            {
                Channel = original.Channel, From = original.From, UnitId = original.UnitId, To = original.To,
                Text = $"{original.Text} (repeat)", Payload = original.Payload, Priority = 2,
                QueuedAt = context.SimTime, NotBefore = context.SimTime + TimeSpan.FromSeconds(3),
            });
        }
    }

    // ---- Patches ----

    private void ActivatePatches(SimulationContext context)
    {
        foreach (var patch in context.World.Radio.Patches.Values.Where(p => !p.Active))
        {
            if (context.SimTime - patch.RequestedAt < _options.PatchSetupTime) continue;
            patch.Active = true;
            context.EmitPerceived(new ChannelsPatched(patch.Id), EventSources.Comms);
        }
    }

    // ---- Degradation: masts, batteries, data coverage ----

    /// <summary>Masts in a power cut run on batteries, then fail; they come back a few minutes after the power does.</summary>
    private void Masts(SimulationContext context)
    {
        var world = context.World;
        foreach (var site in world.Sites.Values.Where(s => s.Kind == HazardSiteKind.CellTower))
        {
            if (!world.Radio.Masts.TryGetValue(site.Id, out var mast))
                world.Radio.Masts[site.Id] = mast = new MastState { SiteId = site.Id };

            var dark = world.InOutage(site.Location);
            if (mast.Down)
            {
                if (!dark && mast.DownCause?.StartsWith("batteries") == true && mast.OnBatterySince is { } since
                    && context.SimTime - since >= _options.MastBatteryLife + TimeSpan.FromMinutes(5))
                {
                    mast.Down = false;
                    mast.OnBatterySince = null;
                    context.EmitTruth(new CellTowerRestored(site.Id));
                }
                continue;
            }

            if (!dark)
            {
                mast.OnBatterySince = null;
                continue;
            }
            mast.OnBatterySince ??= context.SimTime;
            if (context.SimTime - mast.OnBatterySince < _options.MastBatteryLife) continue;

            mast.Down = true;
            mast.DownCause = "batteries exhausted during the power cut";
            context.EmitTruth(new CellTowerFailed(site.Id, $"{site.Name}: {mast.DownCause}"));
            context.EmitTruth(new CascadeOccurred($"Power outage: {site.Name} on batteries for {_options.MastBatteryLife.TotalMinutes:F0} min",
                $"Mobile mast down: 999 calls from mobiles and mobile data lost within {site.ServiceRadiusMeters:F0} m"));
        }
    }

    /// <summary>Crews at a scene work on handhelds; batteries run down, are reported low and get swapped.</summary>
    private void Batteries(SimulationContext context)
    {
        var world = context.World;
        foreach (var unit in world.Units.Values)
        {
            // Not every battery starts the shift fully charged.
            if (_batteriesChecked.Add(unit.Id) && unit.Battery >= 1)
                unit.Battery = 0.55 + Roll(unit.Id, 21) * 0.45;

            if (unit.Phase is ResponsePhase.OnScene or ResponsePhase.Operating)
                unit.Battery = Math.Max(0, unit.Battery - _options.BatteryDrainPerHour * context.Delta.TotalHours);

            if (unit.LowBatteryReportedAt is null && unit.Battery < CommsNet.LowBattery)
            {
                unit.LowBatteryReportedAt = context.SimTime;
                var text = $"Control, {unit.Callsign}, handheld batteries low, changing over shortly.";
                if (unit.Battery > 0)
                    CommsNet.Voice(context, unit, text, new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, unit.Callsign,
                        "Handheld radio batteries low, changing over.", Confidence.High, VerificationStatus.Confirmed, unit.Location, 30, unit.Id));
            }

            if (unit.LowBatteryReportedAt is { } reported && context.SimTime - reported >= _options.BatterySwapTime)
            {
                unit.LowBatteryReportedAt = null;
                unit.Battery = 1;
                context.EmitTruth(new RadioBatteryChanged(unit.Id, 1));
            }
        }
    }

    /// <summary>Status messages held in a vehicle's terminal go once it has coverage again.</summary>
    private static void FlushData(SimulationContext context)
    {
        foreach (var unit in context.World.Units.Values.Where(u => u.PendingData.Count > 0))
        {
            if (unit.RadioFailed || !CommsNet.DataUp(context.World, unit.Location)) continue;
            foreach (var (payload, source) in unit.PendingData)
                context.EmitPerceived(payload, source);
            unit.PendingData.Clear();
        }
    }

    // ---- The 999 line ----

    private void EmergencyLine(SimulationContext context)
    {
        var world = context.World;
        var radio = world.Radio;
        var now = context.SimTime;
        while (_callTakersFreeAt.Count < _options.CallTakers)
            _callTakersFreeAt.Add(DateTimeOffset.MinValue);

        foreach (var call in radio.CallQueue.Where(c => c.QueuedAt <= now).ToList())
        {
            // A mobile under a dead mast never even rings; a few find a landline and try again.
            if (call.Mobile && call.Location is { } where && CommsNet.MastDownOver(world, where))
            {
                radio.CallQueue.Remove(call);
                context.EmitTruth(new TransmissionLost(call.Id, RadioPlan.Calls, call.Caller, call.Summary, "mobile network down"));
                if (Roll(call.Id, 31) < 0.3)
                    radio.CallQueue.Add(new PendingCall
                    {
                        Caller = "999 caller (landline)", Summary = call.Summary, Location = call.Location, AccuracyMeters = 40,
                        Language = call.Language, Mobile = false, QueuedAt = now + TimeSpan.FromMinutes(2), Patience = call.Patience,
                    });
                continue;
            }

            if (now - call.QueuedAt > call.Patience)
            {
                radio.CallQueue.Remove(call);
                radio.Missed[call.Id] = call;
                context.EmitPerceived(new CallMissed(call.Id, call.Location, call.AccuracyMeters is { } a ? a * 1.5 : null, now - call.QueuedAt),
                    EventSources.Comms);
            }
        }

        for (var taker = 0; taker < _callTakersFreeAt.Count; taker++)
        {
            if (_callTakersFreeAt[taker] > now) continue;
            var call = radio.CallQueue.Where(c => c.QueuedAt <= now).OrderBy(c => c.QueuedAt).FirstOrDefault();
            if (call is null) break;
            radio.CallQueue.Remove(call);
            _callTakersFreeAt[taker] = now + Answer(context, call, call.Id, call.Caller);
        }
    }

    /// <summary>A call-taker takes the call. Returns how long it keeps them busy.</summary>
    private TimeSpan Answer(SimulationContext context, PendingCall call, Guid callId, string caller)
    {
        var handling = _options.MinCallHandling + (_options.MaxCallHandling - _options.MinCallHandling) * Roll(callId, 32);
        var summary = call.Summary;
        var accuracy = call.AccuracyMeters;
        var quality = CommsQuality.Clear;

        if (call.Language is { } language)
        {
            // Little English: a few words and no address until an interpreter joins the line.
            var fragments = string.Join(" … ", call.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where((_, i) => i % 3 == 0).Take(4));
            summary = $"[Caller speaks {language}, very little English] \"… {fragments} … help …\"";
            accuracy = accuracy is { } m ? m * 2 : null;
            quality = CommsQuality.Broken;
            handling += TimeSpan.FromSeconds(90);
            var joins = context.SimTime + TimeSpan.FromMinutes(2 + Roll(callId, 33) * 2);
            var interpreter = $"Language line interpreter ({language})";
            var full = $"Caller (via interpreter): {call.Summary}";
            _interpreters.Add((joins, new ReportReceived(Guid.NewGuid(), null, ReportSource.EmergencyCall, interpreter, full,
                Confidence.Medium, VerificationStatus.Reported, call.Location, call.AccuracyMeters), interpreter, full));
        }

        context.EmitPerceived(new CommsLogged(Guid.NewGuid(), RadioPlan.Calls, caller, "Call-taker", summary, quality, handling.TotalSeconds),
            EventSources.Comms);
        context.EmitPerceived(new CallReceived(callId, caller, summary, call.Location, accuracy), EventSources.Comms);
        return handling;
    }

    /// <summary>Command rings a missed caller back: they answer, unless their phone has no signal.</summary>
    private void Callbacks(SimulationContext context)
    {
        var world = context.World;
        var radio = world.Radio;
        foreach (var (callId, at) in radio.Callbacks.ToList())
        {
            if (context.SimTime - at < TimeSpan.FromSeconds(40)) continue;
            radio.Callbacks.Remove(callId);
            if (!radio.Missed.Remove(callId, out var call)) continue;

            if (call.Mobile && call.Location is { } where && CommsNet.MastDownOver(world, where))
            {
                context.EmitPerceived(new CommsLogged(Guid.NewGuid(), RadioPlan.Calls, "Call-taker", call.Caller,
                    "Callback to missed caller: number unreachable.", CommsQuality.Clear, 20), EventSources.Comms);
                continue;
            }
            Answer(context, call, Guid.NewGuid(), $"{call.Caller} (callback)");
        }
    }

    private void Interpreters(SimulationContext context)
    {
        foreach (var due in _interpreters.Where(i => i.At <= context.SimTime).ToList())
        {
            _interpreters.Remove(due);
            context.EmitPerceived(new CommsLogged(Guid.NewGuid(), RadioPlan.Calls, due.From, "Call-taker", due.Text, CommsQuality.Clear, 60),
                EventSources.Comms);
            context.EmitPerceived(due.Payload, EventSources.Comms);
        }
    }
}
