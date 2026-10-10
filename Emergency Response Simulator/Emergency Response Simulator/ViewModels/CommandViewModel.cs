using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// "Make it happen" (Design Document §9): orders, resource requests, approvals and notifications.
/// Forms act on the selected incident; every action goes through C2 and lands in the event stream.
/// </summary>
public sealed partial class CommandViewModel(MainViewModel owner, IC2Service c2, ICopService cop) : ObservableObject
{
    // ---- Orders ----

    public ObservableCollection<OrderTarget> OrderTargets { get; } = [];
    [ObservableProperty] private OrderTarget? _orderTarget;
    [ObservableProperty] private string _orderText = "";
    public ObservableCollection<OrderRow> Orders { get; } = [];

    [RelayCommand]
    private async Task IssueOrderAsync()
    {
        if (OrderTarget is not { } target)
        {
            owner.ShowCommandResult(CommandResult.Fail("Choose who the order is for."));
            return;
        }
        var result = await owner.RunCommandAsync(() => c2.IssueOrderAsync(owner.SelectedIncidentId, target.Kind, target.Id, OrderText, target.Role),
            $"Order sent to {target.Label}");
        if (result?.Succeeded == true) OrderText = "";
    }

    [RelayCommand]
    private Task CompleteOrderAsync(Guid orderId) => owner.RunCommandAsync(() => c2.CloseOrderAsync(orderId, completed: true), "Order completed");

    [RelayCommand]
    private Task CancelOrderAsync(Guid orderId) => owner.RunCommandAsync(() => c2.CloseOrderAsync(orderId, completed: false), "Order cancelled");

    /// <summary>Closed loop: the read-back matched the order.</summary>
    [RelayCommand]
    private Task ConfirmReadBackAsync(Guid orderId) => owner.RunCommandAsync(() => c2.ConfirmReadBackAsync(orderId, correct: true), "Read-back confirmed");

    /// <summary>Closed loop: the read-back was wrong or unclear; the order goes out again.</summary>
    [RelayCommand]
    private Task RepeatOrderAsync(Guid orderId) => owner.RunCommandAsync(() => c2.ConfirmReadBackAsync(orderId, correct: false), "Order repeated");

    // ---- Resource requests ----

    public IReadOnlyList<ResourceRequestKind> RequestKinds { get; } = Enum.GetValues<ResourceRequestKind>();
    public IReadOnlyList<UnitTypeOption> RequestableTypes { get; } =
        new[]
        {
            UnitType.Engine, UnitType.Ladder, UnitType.Hazmat, UnitType.WaterTender, UnitType.AmbulanceAls, UnitType.AmbulanceBls,
            UnitType.MassCasualtyUnit, UnitType.Patrol, UnitType.Traffic, UnitType.SearchAndRescue, UnitType.BombDisposal,
            UnitType.Drone, UnitType.Engineering, UnitType.HeavyMachinery, UnitType.Bus,
        }.Select(t => new UnitTypeOption(t, ResourceGroups.Label(t))).ToList();

    [ObservableProperty] private ResourceRequestKind _requestKind = ResourceRequestKind.AdditionalResources;
    [ObservableProperty] private UnitTypeOption? _requestType;

    partial void OnRequestKindChanged(ResourceRequestKind value) => RequestType ??= RequestableTypes[0];
    [ObservableProperty] private string _requestQuantity = "2";
    [ObservableProperty] private string _requestJustification = "";
    public ObservableCollection<RequestRow> Requests { get; } = [];

    /// <summary>Starts the request form on the first resource type.</summary>
    public void InitialiseDefaults() => RequestType ??= RequestableTypes[0];

    [RelayCommand]
    private async Task RequestResourcesAsync()
    {
        if (owner.SelectedIncidentId is not { } incidentId)
        {
            owner.ShowCommandResult(CommandResult.Fail("Select the incident the resources are for."));
            return;
        }
        if (RequestType is not { } type || !int.TryParse(RequestQuantity, out var quantity))
        {
            owner.ShowCommandResult(CommandResult.Fail("Choose a resource type and a whole-number quantity."));
            return;
        }
        var result = await owner.RunCommandAsync(() => c2.RequestResourcesAsync(incidentId, RequestKind, type.Type, quantity, RequestJustification),
            "Request sent");
        if (result?.Succeeded == true) RequestJustification = "";
    }

    // ---- Approvals ----

    public ObservableCollection<ApprovalRow> Approvals { get; } = [];
    [ObservableProperty] private string _decisionNote = "";

    [RelayCommand]
    private Task ApproveAsync(Guid approvalId) => DecideAsync(approvalId, true);

    [RelayCommand]
    private Task DenyAsync(Guid approvalId) => DecideAsync(approvalId, false);

    private async Task DecideAsync(Guid approvalId, bool approve)
    {
        var result = await owner.RunCommandAsync(() => c2.DecideApprovalAsync(approvalId, approve, DecisionNote), approve ? "Approved" : "Denied");
        if (result?.Succeeded == true) DecisionNote = "";
    }

    // ---- Notifications ----

    /// <summary>Common recipients; the box also accepts any other name.</summary>
    public IReadOnlyList<string> Recipients { get; } =
    [
        "St. James's Hospital", "Mater Misericordiae University Hospital", "St. Vincent's University Hospital",
        "ESB Networks", "Gas Networks Ireland", "Uisce Éireann (Irish Water)", "Met Éireann",
        "Dublin City Council", "HSE Public Health", "Dublin Port Company", "Transport for Ireland (Luas/DART)",
        "National Emergency Coordination Group",
    ];

    [ObservableProperty] private string _recipient = "";
    [ObservableProperty] private string _notificationText = "";
    public ObservableCollection<NotificationRow> Notifications { get; } = [];

    [RelayCommand]
    private async Task NotifyAsync()
    {
        var result = await owner.RunCommandAsync(() => c2.NotifyAsync(Recipient, NotificationText, owner.SelectedIncidentId),
            $"Notification sent to {Recipient}");
        if (result?.Succeeded == true) NotificationText = "";
    }

    // ---- Refresh ----

    public void Refresh()
    {
        RefreshTargets();

        Orders.Clear();
        foreach (var order in cop.Orders.OrderBy(o => o.Status is OrderStatus.Completed or OrderStatus.Cancelled).ThenByDescending(o => o.IssuedAt))
            Orders.Add(new OrderRow(order));

        Requests.Clear();
        foreach (var request in cop.ResourceRequests.OrderByDescending(r => r.RequestedAt))
            Requests.Add(new RequestRow(request));

        Approvals.Clear();
        foreach (var approval in cop.Approvals.OrderBy(a => a.Status != ApprovalStatus.Pending).ThenByDescending(a => a.RequestedAt))
            Approvals.Add(new ApprovalRow(approval));
        PendingApprovals = cop.Approvals.Count(a => a.Status == ApprovalStatus.Pending);

        Notifications.Clear();
        foreach (var notification in cop.Notifications.OrderByDescending(n => n.SentAt))
            Notifications.Add(new NotificationRow(notification));
    }

    [ObservableProperty] private int _pendingApprovals;

    /// <summary>Who can receive orders for the selected incident: its units, groups and staffed positions.</summary>
    private void RefreshTargets()
    {
        var selected = OrderTarget;
        OrderTargets.Clear();
        if (owner.SelectedIncidentId is { } id && cop.FindIncident(id) is { } incident)
        {
            foreach (var (role, name) in incident.Command.Positions.OrderBy(p => p.Key))
                OrderTargets.Add(new OrderTarget(OrderTargetKind.Position, null, role, $"{EventDescriber.Humanize(role)} — {name}"));
            foreach (var group in incident.Command.Groups)
                OrderTargets.Add(new OrderTarget(OrderTargetKind.Group, group.Id, null, $"{group.Name}" + (group.Supervisor is { } s ? $" — {s}" : "")));
            foreach (var unit in incident.AssignedUnits.OrderBy(u => u.Callsign, StringComparer.Ordinal))
                OrderTargets.Add(new OrderTarget(OrderTargetKind.Unit, unit.Id, null, unit.Callsign));
        }
        OrderTarget = OrderTargets.FirstOrDefault(t => t == selected) ?? OrderTargets.FirstOrDefault();
    }
}

public sealed record OrderTarget(OrderTargetKind Kind, Guid? Id, IcsRole? Role, string Label);

public sealed record UnitTypeOption(UnitType Type, string Label);

public sealed class OrderRow(Order order)
{
    public Guid Id { get; } = order.Id;
    public string Time { get; } = MainViewModel.Time(order.IssuedAt);
    public string Target { get; } = order.TargetName;
    public string Text { get; } = order.Text;
    public string Status { get; } = order.Status.ToString();
    public string? ReadBack { get; } = order.ReadBack;
    public bool IsOpen { get; } = order.Status is OrderStatus.Issued or OrderStatus.Acknowledged;
    public bool AwaitingReadBack { get; } = order.Status == OrderStatus.Issued;
    public bool ReadBackGarbled { get; } = order.ReadBackGarbled;

    /// <summary>A read-back is in and nobody has closed the loop on it yet.</summary>
    public bool CanConfirm { get; } = order.Status == OrderStatus.Acknowledged && order.ReadBackConfirmedAt is null;

    public bool Confirmed { get; } = order.ReadBackConfirmedAt is not null;
}

public sealed class RequestRow(ResourceRequest request)
{
    public string Time { get; } = MainViewModel.Time(request.RequestedAt);
    public string Description { get; } = request.Description;
    public string Status { get; } = request.Status.ToString();
    public string Detail { get; } = request.Status switch
    {
        ResourceRequestStatus.Approved => $"{request.DecidedBy} · expected {request.ExpectedAt?.ToLocalTime():HH:mm}",
        ResourceRequestStatus.Denied => request.DecisionReason ?? $"Denied by {request.DecidedBy}",
        ResourceRequestStatus.Fulfilled => $"{request.FulfilledBy.Count} unit(s) arrived and dispatched",
        _ => request.Kind == ResourceRequestKind.AdditionalResources ? "With control" : "Awaiting approval",
    };
}

public sealed class ApprovalRow(ApprovalRequest approval)
{
    public Guid Id { get; } = approval.Id;
    public string Time { get; } = MainViewModel.Time(approval.RequestedAt);
    public string Subject { get; } = approval.Subject;
    public string Details { get; } = approval.Details;
    public string RequestedBy { get; } = approval.RequestedBy;
    public bool IsPending { get; } = approval.Status == ApprovalStatus.Pending;
    public string Status { get; } = approval.Status + (approval.Note is { } note ? $" — {note}" : "");
}

public sealed class NotificationRow(Notification notification)
{
    public string Time { get; } = MainViewModel.Time(notification.SentAt);
    public string Recipient { get; } = notification.Recipient;
    public string Message { get; } = notification.Message;
    public string Reply { get; } = notification.Reply ?? "Awaiting reply…";
}
