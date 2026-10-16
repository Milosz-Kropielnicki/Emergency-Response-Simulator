using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Adaptive difficulty (Phase 19). Every <see cref="AssessEvery"/>, while difficulty is adaptive, it scores how the trainees
/// are coping, from 100 down, on what a controller watching over their shoulders would see:
/// <list type="bullet">
/// <item>an incident open three minutes with nobody sent: −15 each;</item>
/// <item>999 calls queuing beyond the first: −8 each (at most −24);</item>
/// <item>overdue or urgent items on the role desks: −3 each (at most −30);</item>
/// <item>alerts left unacknowledged five minutes: −4 each (at most −20);</item>
/// <item>casualties waiting more than fifteen minutes for treatment (ground truth): −2 each (at most −16).</item>
/// </list>
/// Two scores of <see cref="RaiseAt"/> or more in a row raise the level; one below <see cref="LowerAt"/> lowers it. The level
/// drives how often random events come (<see cref="RandomEventSystem"/>) and how big they are, and at 1–2 the scenario's
/// optional steps wait. Every assessment is recorded for the review.
/// </summary>
public sealed class AdaptiveDifficultySystem(ICopService cop) : ISimulationSystem
{
    public static readonly TimeSpan AssessEvery = TimeSpan.FromMinutes(5);
    public const int RaiseAt = 80;
    public const int LowerAt = 45;

    private static readonly UserRole[] CommandRoles =
    [
        UserRole.IncidentCommander, UserRole.Dispatcher, UserRole.Operations, UserRole.Planning, UserRole.Logistics, UserRole.FinanceAdmin,
        UserRole.Safety, UserRole.Liaison, UserRole.PublicInformation,
    ];

    private DateTimeOffset? _next;
    private int? _previous;

    public int Order => 98; // after the world has moved and before attention looks at the COP

    public void Update(SimulationContext context)
    {
        var training = context.World.Training;
        if (training.Settings.Mode != DifficultyMode.Adaptive)
        {
            _next = null;
            return;
        }
        _next ??= context.SimTime + AssessEvery;
        if (context.SimTime < _next) return;
        _next = context.SimTime + AssessEvery;

        var (score, reason) = Assess(context);
        var level = training.Level;
        context.EmitTruth(new DifficultyAssessed(score, level, reason));
        if (score >= RaiseAt && _previous >= RaiseAt && level < 5)
            context.EmitTruth(new DifficultyAdjusted(level, level + 1, score, "coping well" + (reason.Length > 0 ? $" ({reason})" : "")));
        else if (score < LowerAt && level > 1)
            context.EmitTruth(new DifficultyAdjusted(level, level - 1, score, $"struggling: {reason}"));
        _previous = score;
    }

    /// <summary>The score, and the two things that cost the most.</summary>
    public (int Score, string Reason) Assess(SimulationContext context)
    {
        var now = context.SimTime;
        var world = context.World;
        var costs = new List<(int Points, string What)>();
        void Cost(int count, int each, int cap, string what)
        {
            if (count > 0) costs.Add((Math.Min(cap, count * each), $"{count} {what}"));
        }

        Cost(cop.Incidents.Count(i => i.Status != IncidentStatus.Closed && now - i.ReportedAt >= TimeSpan.FromMinutes(3) && i.AssignedUnits.Count == 0),
            15, 100, "incident(s) with nobody sent");
        Cost(Math.Max(0, world.Radio.CallQueue.Count - 1), 8, 24, "999 call(s) queuing");
        var overdue = CommandRoles.SelectMany(r => RoleDesk.For(r, cop, _ => true))
            .Where(d => d.Urgency is DeskUrgency.Overdue or DeskUrgency.Urgent).Select(d => d.Text).Distinct().Count();
        Cost(overdue, 3, 30, "desk item(s) overdue");
        Cost(cop.Alerts.Count(a => a.AcknowledgedAt is null && now - a.RaisedAt >= TimeSpan.FromMinutes(5)), 4, 20, "alert(s) unacknowledged");
        Cost(world.Casualties.Values.Count(c => c is { Alive: true, State: CasualtyState.AwaitingTreatment } && now - c.InjuredAt >= TimeSpan.FromMinutes(15)),
            2, 16, "casualties waiting for treatment");

        var score = Math.Clamp(100 - costs.Sum(c => c.Points), 0, 100);
        return (score, string.Join(", ", costs.OrderByDescending(c => c.Points).Take(2).Select(c => c.What)));
    }
}
