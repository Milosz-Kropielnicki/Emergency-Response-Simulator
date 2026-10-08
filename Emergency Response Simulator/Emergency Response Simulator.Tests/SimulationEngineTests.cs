using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Tests;

public class SimulationEngineTests
{
    /// <summary>
    /// A fire starts in the real world; a caller only reports it 90 seconds later.
    /// Exercises the core of the design: truth and perception are separate and can lag.
    /// </summary>
    private sealed class DelayedCallerSystem : ISimulationSystem
    {
        private readonly Guid _fire = Guid.NewGuid();
        private bool _started;
        private bool _reported;

        public void Update(SimulationContext context)
        {
            if (!_started)
            {
                context.EmitTruth(new WorldIncidentStarted(_fire, IncidentType.StructureFire, TestHarness.Dublin, 0.3, 4));
                _started = true;
            }
            else if (!_reported && context.SimTime - context.World.Incidents[_fire].StartedAt >= TimeSpan.FromSeconds(90))
            {
                context.EmitPerceived(new CallReceived(Guid.NewGuid(), "911 caller", "Smoke from a warehouse", TestHarness.Dublin, 150));
                _reported = true;
            }
        }
    }

    [Fact]
    public async Task Truth_reaches_the_world_but_not_the_cop_until_someone_reports_it()
    {
        var harness = new TestHarness(new DelayedCallerSystem());

        await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));
        Assert.Single(harness.World.Incidents);
        Assert.Empty(harness.Cop.Reports);

        await harness.Engine.StepAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(harness.Cop.Reports);

        await harness.Engine.StepAsync(TimeSpan.FromSeconds(30));
        var report = Assert.Single(harness.Cop.Reports);
        Assert.Equal(Confidence.Low, report.Confidence);
        Assert.Equal(VerificationStatus.Reported, report.Verification);
        Assert.Equal(TestHarness.Start.AddSeconds(91), report.ReceivedAt);
    }

    [Fact]
    public async Task Tick_does_nothing_while_paused_and_scales_time_while_running()
    {
        var harness = new TestHarness();

        await harness.Engine.TickAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TestHarness.Start, harness.Engine.SimTime);

        await harness.Engine.StartAsync();
        await harness.Engine.SetTimeScaleAsync(10);
        await harness.Engine.TickAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TestHarness.Start.AddSeconds(20), harness.Engine.SimTime);
    }

    [Fact]
    public async Task Events_are_sequenced_in_the_order_they_happened()
    {
        var harness = new TestHarness(new DelayedCallerSystem());
        await harness.RegisterUnitAsync();
        await harness.Engine.StepAsync(TimeSpan.FromSeconds(1));   // fire starts
        await harness.Engine.StepAsync(TimeSpan.FromSeconds(100)); // caller reports it

        var timeline = await harness.Aar.GetTimelineAsync(harness.Engine.SessionId, includeTruth: true);

        Assert.Equal(timeline.Select(e => e.Sequence).Order(), timeline.Select(e => e.Sequence));
        Assert.Equal(timeline.Select(e => e.SimTime).Order(), timeline.Select(e => e.SimTime));
        Assert.Equal(["UnitRegistered", "WorldIncidentStarted", "CallReceived"], timeline.Select(e => e.Type));
    }
}
