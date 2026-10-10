using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// The COP as displayed: normally the live picture, or a replayed snapshot of how it looked at an
/// earlier moment (Design Document §6.6, "How did we get here?"). Commands always go against the live
/// picture; only the display is switched.
/// </summary>
public sealed class CopView : ICopService
{
    private readonly ICopService _live;
    private ICopService _current;

    public CopView(ICopService live)
    {
        _live = _current = live;
        _live.Changed += (_, _) =>
        {
            if (ReferenceEquals(_current, _live))
                Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    /// <summary>Raised when switching between live and replay.</summary>
    public event EventHandler? ModeChanged;

    public bool IsReplay => !ReferenceEquals(_current, _live);

    /// <summary>The simulation time being shown when replaying.</summary>
    public DateTimeOffset? ReplayTime { get; private set; }

    public void ShowReplay(ICopService snapshot, DateTimeOffset at)
    {
        _current = snapshot;
        ReplayTime = at;
        ModeChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ShowLive()
    {
        if (!IsReplay) return;
        _current = _live;
        ReplayTime = null;
        ModeChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DateTimeOffset AsOf => _current.AsOf;
    public long LastSequence => _current.LastSequence;
    public IReadOnlyList<Agency> Agencies => _current.Agencies;
    public IReadOnlyList<Incident> Incidents => _current.Incidents;
    public IReadOnlyList<Unit> Units => _current.Units;
    public IReadOnlyList<Report> Reports => _current.Reports;
    public IReadOnlyList<Alert> Alerts => _current.Alerts;
    public IReadOnlyList<Zone> Zones => _current.Zones;
    public PerceivedWeather? Weather => _current.Weather;
    public IReadOnlyList<Hospital> Hospitals => _current.Hospitals;
    public IReadOnlyList<CommsEntry> CommsLog => _current.CommsLog;
    public IReadOnlyList<ChannelState> Channels => _current.Channels;
    public IReadOnlyList<ChannelPatch> Patches => _current.Patches;
    public IReadOnlyList<MissedCall> MissedCalls => _current.MissedCalls;
    public IReadOnlyList<Order> Orders => _current.Orders;
    public IReadOnlyList<ResourceRequest> ResourceRequests => _current.ResourceRequests;
    public IReadOnlyList<ApprovalRequest> Approvals => _current.Approvals;
    public IReadOnlyList<Notification> Notifications => _current.Notifications;
    public IReadOnlyList<OperationalPeriod> OperationalPeriods => _current.OperationalPeriods;
    public IReadOnlyList<IncidentActionPlan> ActionPlans => _current.ActionPlans;
    public IReadOnlyDictionary<Guid, ObjectiveStatus> ObjectiveProgress => _current.ObjectiveProgress;
    public Incident? FindIncident(Guid incidentId) => _current.FindIncident(incidentId);
    public Unit? FindUnit(Guid unitId) => _current.FindUnit(unitId);
}
