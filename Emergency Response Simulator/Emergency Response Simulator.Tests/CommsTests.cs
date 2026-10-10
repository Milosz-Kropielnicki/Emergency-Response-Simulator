using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 7: communications realism, the fog of war between the world and the COP.</summary>
public class CommsTests
{
    private static readonly GeoPoint Dublin = TestHarness.Dublin;

    /// <summary>Runs scripted actions inside a tick, at or after the given time from the start.</summary>
    private sealed class Script(params (TimeSpan At, Action<SimulationContext> Act)[] steps) : ISimulationSystem
    {
        private readonly List<(TimeSpan At, Action<SimulationContext> Act)> _steps = [.. steps];
        public int Order => 50;

        public void Update(SimulationContext context)
        {
            foreach (var step in _steps.Where(s => context.SimTime - TestHarness.Start >= s.At).ToList())
            {
                _steps.Remove(step);
                step.Act(context);
            }
        }
    }

    /// <summary>A comms system without background noise, so tests see only the effects they set up.</summary>
    private static CommsSystem Quiet(Action<CommsOptions>? configure = null)
    {
        var options = new CommsOptions { BackgroundGarble = 0, BackgroundBroken = 0 };
        configure?.Invoke(options);
        return new CommsSystem(options);
    }

    private static async Task<Guid> UnitAsync(TestHarness harness, string callsign, UnitType type = UnitType.Engine, GeoPoint? at = null, Guid? agency = null)
    {
        var id = Guid.NewGuid();
        await harness.Perceived(new UnitRegistered(id, callsign, type, agency, at ?? Dublin, "Station 3", 4, []));
        await harness.Truth(new RadioBatteryChanged(id, 1)); // a known, full battery
        return id;
    }

    private static ReportReceived Report(WorldUnit unit, string claim) =>
        new(Guid.NewGuid(), null, ReportSource.FieldUnit, unit.Callsign, claim, Confidence.High, VerificationStatus.Confirmed, unit.Location, 20, unit.Id);

    private static async Task<List<T>> PayloadsAsync<T>(TestHarness harness) where T : DomainEvent =>
        (await harness.EventsAsync()).Select(e => e.Payload).OfType<T>().ToList();

    // ---- Airtime and congestion ----

    [Fact]
    public async Task A_channel_carries_one_message_at_a_time_and_a_jammed_channel_loses_messages()
    {
        var harness = new TestHarness(cop => [Quiet(), new AttentionMonitor(cop, new AttentionOptions()),
            new Script((TimeSpan.Zero, context =>
            {
                foreach (var unit in context.World.Units.Values)
                    CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: on scene, two floors involved, requesting a second line of hose and a ladder",
                        Report(unit, "On scene, two floors involved"));
            }))]);
        for (var i = 1; i <= 25; i++) await UnitAsync(harness, $"Engine {i}");

        await harness.RunAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1));

        var heard = (await harness.EventsAsync()).Where(e => e.Payload is CommsLogged { FromControl: false }).ToList();
        Assert.True(heard.Count >= 5);
        // One after another: each starts after the previous one's airtime.
        Assert.True(heard.Last().SimTime - heard.First().SimTime >= TimeSpan.FromSeconds(5 * (heard.Count - 1)));
        Assert.Contains(await PayloadsAsync<TransmissionLost>(harness), l => l.Reason.Contains("congested"));
        Assert.Contains(harness.Cop.Alerts, a => a.Title == $"{RadioPlan.FireCommand} congested");
        Assert.True(heard.Count < 25, "some crews gave up");
    }

    [Fact]
    public async Task Moving_crews_to_a_tactical_channel_takes_load_off_the_main_one()
    {
        var harness = new TestHarness(Quiet(), new Script((TimeSpan.FromSeconds(1), context =>
        {
            foreach (var unit in context.World.Units.Values)
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: still working on the east side", Report(unit, "Still working"));
        })));
        var units = new List<Guid>();
        for (var i = 1; i <= 8; i++) units.Add(await UnitAsync(harness, $"Engine {i}"));
        foreach (var unit in units.Take(4))
            Assert.True((await harness.C2.AssignChannelAsync(unit, RadioPlan.FireTac2)).Succeeded);

        await harness.RunAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        var log = harness.Cop.CommsLog.Where(m => !m.FromControl).ToList();
        Assert.Equal(4, log.Count(m => m.ChannelId == RadioPlan.FireTac2));
        Assert.Equal(4, log.Count(m => m.ChannelId == RadioPlan.FireCommand));
        // Two channels clear the same traffic in about half the time.
        Assert.True(log.Max(m => m.At) - log.Min(m => m.At) < TimeSpan.FromSeconds(30));
    }

    // ---- Degraded communications ----

    [Fact]
    public async Task In_a_black_spot_most_messages_are_lost_and_status_waits_for_coverage()
    {
        var harness = new TestHarness(Quiet(), new Script(
            (TimeSpan.FromSeconds(1), context =>
            {
                var unit = context.World.Units.Values.Single();
                for (var i = 0; i < 12; i++)
                    CommsNet.Voice(context, unit, $"Control, Engine 4: message {i}", Report(unit, $"message {i}"));
                CommsNet.Data(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OnScene), EventSources.Comms);
            }),
            (TimeSpan.FromMinutes(5), context => context.World.Units.Values.Single().Location = GeoMath.Destination(Dublin, 0, 500))));
        var engine = await UnitAsync(harness, "Engine 4");
        await harness.Truth(new RadioDeadZonePlaced(Guid.NewGuid(), Dublin, 50, "basement car park"));

        await harness.RunAsync(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(2));
        var lost = await PayloadsAsync<TransmissionLost>(harness);
        Assert.True(lost.Count(l => l.Reason == "radio black spot") >= 6);
        Assert.All(harness.Cop.CommsLog.Where(m => m.UnitId == engine), m => Assert.Equal(CommsQuality.Garbled, m.Quality));
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(engine)!.Status); // the status button press is still in the terminal

        await harness.RunAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2));
        Assert.Equal(UnitStatus.OnScene, harness.Cop.FindUnit(engine)!.Status); // sent once the vehicle had coverage
    }

    [Fact]
    public async Task An_order_nobody_hears_gets_no_read_back_and_one_that_is_heard_is_read_back_over_the_radio()
    {
        var harness = new TestHarness(Quiet(), new CommandResponseSystem());
        var flat = await UnitAsync(harness, "Engine 7");
        var working = await UnitAsync(harness, "Engine 12");
        await harness.Truth(new RadioBatteryChanged(flat, 0));

        var unheard = (await harness.C2.IssueOrderAsync(null, OrderTargetKind.Unit, flat, "Move to the rear of the building")).EntityId!.Value;
        var heard = (await harness.C2.IssueOrderAsync(null, OrderTargetKind.Unit, working, "Set up a water curtain on the east side")).EntityId!.Value;
        await harness.RunAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1));

        Assert.Equal(OrderStatus.Issued, harness.Cop.Orders.Single(o => o.Id == unheard).Status);
        Assert.Contains(await PayloadsAsync<TransmissionLost>(harness), l => l.Reason == "Engine 7 can't receive");
        Assert.Equal(OrderStatus.Acknowledged, harness.Cop.Orders.Single(o => o.Id == heard).Status);
        Assert.Contains(harness.Cop.CommsLog, m => m.From == "Engine 12" && m.Text.Contains("water curtain"));
        Assert.Contains(harness.Cop.CommsLog, m => m.FromControl && m.To == "Engine 12");
    }

    [Fact]
    public async Task Handheld_batteries_run_low_are_reported_and_get_swapped()
    {
        var harness = new TestHarness(Quiet());
        var engine = await UnitAsync(harness, "Engine 4");
        await harness.Truth(new RadioBatteryChanged(engine, 0.1));

        await harness.RunAsync(TimeSpan.FromMinutes(1));
        // Heard, though on a weak battery it may well break up.
        Assert.Contains(harness.World.Radio.Sent.Values, t => t.UnitId == engine && t.Text.Contains("batteries low"));
        Assert.Contains(harness.Cop.CommsLog, m => m.UnitId == engine);

        await harness.RunAsync(TimeSpan.FromMinutes(9));
        Assert.Equal(1, harness.World.Units[engine].Battery);
    }

    [Fact]
    public async Task A_mobile_mast_on_batteries_fails_and_takes_999_calls_and_vehicle_data_with_it()
    {
        var mast = Guid.NewGuid();
        var harness = new TestHarness(Quiet(o => o.MastBatteryLife = TimeSpan.FromMinutes(10)), new Script(
            (TimeSpan.FromMinutes(11), context =>
            {
                CommsNet.EmergencyCall(context, new PendingCall { Caller = "999 caller (mobile)", Summary = "Smoke in the street", Location = Dublin, AccuracyMeters = 100 });
                var unit = context.World.Units.Values.Single();
                CommsNet.Data(context, unit, new UnitPositionReported(unit.Id, unit.Location, 0, 0, null), EventSources.Avl);
                CommsNet.Data(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OnScene), EventSources.Comms);
            })));
        var engine = await UnitAsync(harness, "Engine 4");
        await harness.Truth(new HazardSitePlaced(mast, HazardSiteKind.CellTower, "Canal mast", Dublin, ServiceRadiusMeters: 800));
        await harness.Truth(new PowerOutageStarted(Guid.NewGuid(), Dublin, 1000, "substation fault"));

        await harness.RunAsync(TimeSpan.FromMinutes(12), TimeSpan.FromSeconds(10));

        Assert.True(harness.World.Radio.Masts[mast].Down);
        Assert.Contains(harness.World.Cascades, c => c.Effect.StartsWith("Mobile mast down"));
        Assert.Contains(await PayloadsAsync<TransmissionLost>(harness), l => l.Reason == "mobile network down");
        Assert.DoesNotContain(harness.Cop.Reports, r => r.Claim == "Smoke in the street");
        Assert.Equal(UnitStatus.Available, harness.Cop.FindUnit(engine)!.Status); // held until coverage returns
        Assert.Single(harness.World.Units[engine].PendingData); // the fix was dropped, the status kept
    }

    // ---- Push-to-talk, call signs and closed loop ----

    [Fact]
    public async Task Crews_answer_their_call_sign_with_a_status_and_ignore_calls_without_one()
    {
        var harness = new TestHarness(Quiet());
        await UnitAsync(harness, "Engine 4");

        await harness.C2.TransmitAsync(RadioPlan.FireCommand, "Engine 4", "Engine 4, Control, send me your status");
        await harness.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        Assert.Contains(harness.Cop.CommsLog, m => m.From == "Engine 4" && m.Text.Contains("available"));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Engine 4" && r.Claim.StartsWith("available"));

        var before = harness.Cop.CommsLog.Count(m => m.From == "Engine 4");
        await harness.C2.TransmitAsync(RadioPlan.FireCommand, "Engine 4", "Control here, what is your status");
        await harness.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        Assert.Equal(before, harness.Cop.CommsLog.Count(m => m.From == "Engine 4")); // never heard its call sign
        Assert.Contains(harness.Cop.CommsLog, m => m.FromControl && m.Notes.Any(n => n.StartsWith("No call sign")));
    }

    [Fact]
    public async Task Letting_go_of_push_to_talk_early_cuts_the_message_off()
    {
        var harness = new TestHarness(Quiet());
        await UnitAsync(harness, "Engine 4");

        await harness.C2.TransmitAsync(RadioPlan.FireCommand, "Engine 4",
            "Engine 4, Control, proceed to the rear of the warehouse and set up a water curtain to protect unit four", heldSeconds: 3);
        await harness.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));

        var sent = harness.Cop.CommsLog.Single(m => m.FromControl);
        Assert.Equal(CommsQuality.Broken, sent.Quality);
        Assert.EndsWith("—", sent.Text);
        Assert.DoesNotContain("water curtain", sent.Text);
        Assert.Contains(harness.Cop.CommsLog, m => m.From == "Engine 4" && m.Text.Contains("cut off, say again"));
    }

    [Fact]
    public async Task Keying_up_over_a_crew_doubles_both_and_nobody_hears_command()
    {
        var harness = new TestHarness(Quiet(), new Script((TimeSpan.FromSeconds(1), context =>
        {
            var unit = context.World.Units.Values.Single();
            CommsNet.Voice(context, unit, string.Join(' ', Enumerable.Repeat("long situation report", 12)), null);
        })));
        await UnitAsync(harness, "Engine 4");
        await harness.RunAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)); // the crew is now mid-transmission

        await harness.C2.TransmitAsync(RadioPlan.FireCommand, "Engine 4", "Engine 4, Control, status?");
        await harness.RunAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        var sent = harness.Cop.CommsLog.Single(m => m.FromControl);
        Assert.Equal(CommsQuality.Garbled, sent.Quality);
        Assert.Contains(sent.Notes, n => n.StartsWith("Doubled with Engine 4"));
        Assert.Contains(harness.Cop.CommsLog, m => m.From == "Engine 4" && m.Text.Contains("say again"));
    }

    [Fact]
    public async Task Say_again_gets_a_garbled_message_repeated()
    {
        var harness = new TestHarness(Quiet(), new Script((TimeSpan.FromSeconds(1), context =>
        {
            var unit = context.World.Units.Values.Single();
            CommsNet.Voice(context, unit, "Control, Engine 4: casualty trapped on the second floor, rear window", Report(unit, "Casualty trapped, second floor"));
        })));
        var engine = await UnitAsync(harness, "Engine 4");
        await harness.Truth(new RadioBatteryChanged(engine, 0.01)); // nearly flat: it breaks up
        harness.World.Units[engine].LowBatteryReportedAt = TestHarness.Start; // (already reported; keeps this test to one message)
        await harness.RunAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1));
        var first = harness.Cop.CommsLog.Single(m => m.UnitId == engine);

        Assert.True((await harness.C2.RequestRepeatAsync(first.Id)).Succeeded);
        await harness.Truth(new RadioBatteryChanged(engine, 1)); // a fresh battery
        await harness.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));

        Assert.Contains(harness.Cop.CommsLog, m => m.UnitId == engine && m.Text.EndsWith("(repeat)"));
        Assert.False((await harness.C2.RequestRepeatAsync(first.Id)).Succeeded); // already asked
    }

    [Fact]
    public async Task An_unclear_read_back_must_be_closed_by_confirming_or_repeating_the_order()
    {
        var harness = new TestHarness(cop => [Quiet(), new CommandResponseSystem(), new AttentionMonitor(cop, new AttentionOptions())]);
        // Several crews on nearly flat batteries: most will hear the order, or read it back, badly.
        var orders = new Dictionary<Guid, Guid>();
        for (var i = 1; i <= 6; i++)
        {
            var unit = await UnitAsync(harness, $"Engine {i}");
            await harness.Truth(new RadioBatteryChanged(unit, 0.01));
            harness.World.Units[unit].LowBatteryReportedAt = TestHarness.Start; // no swap during the test
            orders[(await harness.C2.IssueOrderAsync(null, OrderTargetKind.Unit, unit, "Withdraw to the cold zone")).EntityId!.Value] = unit;
        }
        Order? Unclear() => harness.Cop.Orders.FirstOrDefault(o => o.Status == OrderStatus.Acknowledged && o.ReadBackGarbled);

        Assert.True(await harness.RunUntilAsync(() => Unclear() is not null, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1)));
        var order = Unclear()!;
        await harness.RunAsync(TimeSpan.FromMinutes(1.5));
        Assert.Contains(harness.Cop.Alerts, a => a.Title == $"Unclear read-back from {order.TargetName}");

        await harness.Truth(new RadioBatteryChanged(orders[order.Id], 1));
        Assert.True((await harness.C2.ConfirmReadBackAsync(order.Id, correct: false)).Succeeded);
        Assert.Equal(OrderStatus.Issued, harness.Cop.Orders.Single(o => o.Id == order.Id).Status);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Orders.Single(o => o.Id == order.Id) is { Status: OrderStatus.Acknowledged, ReadBackGarbled: false },
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1)));
        Assert.True((await harness.C2.ConfirmReadBackAsync(order.Id, correct: true)).Succeeded);
        Assert.NotNull(harness.Cop.Orders.Single(o => o.Id == order.Id).ReadBackConfirmedAt);
    }

    [Fact]
    public void Radio_discipline_notes_flag_missing_call_signs_long_messages_codes_and_filler()
    {
        Assert.Empty(RadioDiscipline.Review("Engine 4, Control, move to the rear of the building", "Engine 4"));

        var notes = RadioDiscipline.Review("Um, please be advised, 10-4, " + string.Join(' ', Enumerable.Repeat("word", 30)), "Engine 4");
        Assert.Contains(notes, n => n.StartsWith("No call sign"));
        Assert.Contains(notes, n => n.StartsWith("Long transmission"));
        Assert.Contains(notes, n => n.StartsWith("Codes are not plain language"));
        Assert.Contains(notes, n => n.StartsWith("Filler words"));
    }

    // ---- Interoperability ----

    [Fact]
    public async Task Mutual_aid_crews_on_their_own_radio_are_unreachable_until_command_patches_their_channel()
    {
        var harness = new TestHarness(cop => [Quiet(), new CommandResponseSystem(), new AttentionMonitor(cop, new AttentionOptions()),
            new Script((TimeSpan.FromSeconds(30), context =>
            {
                var unit = context.World.Units.Values.Single();
                CommsNet.Voice(context, unit, "Kildare control, Kildare Engine 1, on scene", Report(unit, "On scene"));
            }))]);
        var kildare = Guid.NewGuid();
        await harness.Perceived(new AgencyRegistered(kildare, "Kildare Fire Service", "KFS", AgencyType.Fire, RadioChannel: "KILDARE FIRE"));
        var engine = await UnitAsync(harness, "Kildare Engine 1", agency: kildare);
        var incident = await harness.CreateIncidentAsync();
        await harness.Perceived(new UnitDispatched(engine, incident, null));

        var refused = await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Take the north side");
        Assert.False(refused.Succeeded);
        Assert.Contains("Patch KILDARE FIRE", refused.Error);

        await harness.RunAsync(TimeSpan.FromMinutes(2));
        Assert.Contains(await PayloadsAsync<TransmissionLost>(harness), l => l.Reason == "KILDARE FIRE is not monitored by command");
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Kildare Fire Service control" && r.Claim.Contains("patch"));
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Kildare Engine 1 is on KILDARE FIRE");

        Assert.True((await harness.C2.PatchChannelsAsync("KILDARE FIRE", RadioPlan.FireCommand)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(3.5));
        Assert.NotNull(harness.Cop.Patches.Single().ActiveFrom);

        var order = await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, engine, "Take the north side");
        Assert.True(order.Succeeded);
        Assert.True(await harness.RunUntilAsync(() => harness.Cop.Orders.Single(o => o.Id == order.EntityId).Status == OrderStatus.Acknowledged,
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task An_AI_agency_relays_what_its_own_crews_say_by_phone()
    {
        var harness = new TestHarness(Quiet(), new Script((TimeSpan.FromSeconds(5), context =>
        {
            var unit = context.World.Units.Values.Single();
            CommsNet.Voice(context, unit, "North control, Engine 31: large fire, three units involved", Report(unit, "Large fire, three units involved"));
        })));
        var agency = Guid.NewGuid();
        await harness.Perceived(new AgencyRegistered(agency, "North District", "N", AgencyType.Fire, AiControlled: true, RadioChannel: "FIRE NORTH"));
        await UnitAsync(harness, "Engine 31", agency: agency);

        await harness.RunAsync(TimeSpan.FromMinutes(3));

        var relay = Assert.Single(harness.Cop.CommsLog, m => m.ChannelId == RadioPlan.Chat);
        Assert.Equal("North District control", relay.From);
        Assert.StartsWith("Relaying from Engine 31", relay.Text);
        Assert.Contains(harness.Cop.Reports, r => r.Claim == "Large fire, three units involved");
    }

    // ---- The 999 line ----

    [Fact]
    public async Task Callers_queue_for_call_takers_some_hang_up_and_command_can_ring_them_back()
    {
        var harness = new TestHarness(cop => [Quiet(o => o.CallTakers = 2), new AttentionMonitor(cop, new AttentionOptions()),
            new Script((TimeSpan.FromSeconds(1), context =>
            {
                for (var i = 0; i < 8; i++)
                    CommsNet.EmergencyCall(context, new PendingCall
                    {
                        Caller = "999 caller (mobile)", Summary = $"Fire and smoke, caller {i}", Location = Dublin, AccuracyMeters = 80,
                        Patience = TimeSpan.FromSeconds(100),
                    });
            }))]);

        await harness.RunAsync(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(2));

        var answered = harness.Cop.Reports.Count(r => r.Source == ReportSource.EmergencyCall);
        Assert.InRange(answered, 2, 6);
        var missed = harness.Cop.MissedCalls;
        Assert.Equal(8 - answered, missed.Count);
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Missed 999 call");

        Assert.True((await harness.C2.CallBackAsync(missed[0].Id)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName.EndsWith("(callback)"));
        Assert.False((await harness.C2.CallBackAsync(missed[0].Id)).Succeeded);
    }

    [Fact]
    public async Task A_caller_with_little_English_gives_fragments_until_an_interpreter_joins()
    {
        var harness = new TestHarness(Quiet(), new Script((TimeSpan.FromSeconds(1), context =>
            CommsNet.EmergencyCall(context, new PendingCall
            {
                Caller = "999 caller (mobile)", Language = "Polish", Location = Dublin, AccuracyMeters = 50,
                Summary = "Fire next to my building, my mother cannot walk, third floor",
            }))));

        await harness.RunAsync(TimeSpan.FromSeconds(10));
        var call = Assert.Single(harness.Cop.Reports);
        Assert.StartsWith("[Caller speaks Polish", call.Claim);
        Assert.Equal(100, call.LocationAccuracyMeters);
        Assert.DoesNotContain("third floor", call.Claim);

        await harness.RunAsync(TimeSpan.FromMinutes(4.5));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Language line interpreter (Polish)" && r.Claim.Contains("my mother cannot walk, third floor"));
    }

    [Fact]
    public async Task People_with_little_English_follow_an_English_only_evacuation_order_less_than_a_multilingual_one()
    {
        async Task<int> StayingAsync(bool multilingual)
        {
            var options = new CivilianOptions { PopulationPerIncident = 300, CarOwnership = 0, LimitedEnglish = 1.0 };
            var harness = new TestHarness(new CivilianSystem(new UniformTerrain(0.2), options));
            if (multilingual)
                await harness.C2.NotifyAsync("Community translation service", "Evacuation warnings needed in Polish, Portuguese and Romanian");
            await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.Other, Dublin, 0.2, 0));
            await harness.RunAsync(TimeSpan.FromSeconds(5));
            var zone = Wgs84.Circle(Dublin, 300);
            await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", zone);
            await harness.RunAsync(TimeSpan.FromMinutes(25));
            var area = Wgs84.CreatePolygon(zone);
            return harness.World.Civilians.Count(c => area.Contains(c.Location.ToPoint()));
        }

        var englishOnly = await StayingAsync(multilingual: false);
        var multilingualStaying = await StayingAsync(multilingual: true);
        // Expected about 50 % against 15 % staying; allow for sampling spread.
        Assert.True(englishOnly > 1.5 * multilingualStaying, $"{englishOnly} stayed after English-only warnings, {multilingualStaying} after multilingual ones");
    }

    // ---- Truth and perception ----

    [Fact]
    public async Task Lost_transmissions_are_ground_truth_that_command_never_sees()
    {
        var harness = new TestHarness(Quiet(), new Script((TimeSpan.FromSeconds(1), context =>
        {
            var unit = context.World.Units.Values.Single();
            CommsNet.Voice(context, unit, "Control, Engine 7: roof collapsing, pulling crews out", Report(unit, "Roof collapsing"));
        })));
        var engine = await UnitAsync(harness, "Engine 7");
        await harness.Truth(new UnitRadioFailed(engine, true));

        await harness.RunAsync(TimeSpan.FromSeconds(30));

        var lost = (await harness.EventsAsync()).Single(e => e.Payload is TransmissionLost);
        Assert.Equal(EventVisibility.Truth, lost.Visibility);
        Assert.Empty(harness.Cop.CommsLog);
        Assert.Empty(harness.Cop.Reports);
    }
}
