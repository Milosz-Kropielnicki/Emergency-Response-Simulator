namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// Allowed unit status transitions (Design Document §6.4):
/// AVAILABLE → DISPATCHED → EN ROUTE → ON SCENE → OPERATING → TRANSPORTING → AVAILABLE,
/// with cancellation while responding and out-of-service from anywhere.
/// </summary>
public static class UnitStatusRules
{
    private static readonly Dictionary<UnitStatus, UnitStatus[]> Allowed = new()
    {
        [UnitStatus.Available] = [UnitStatus.Dispatched],
        [UnitStatus.Dispatched] = [UnitStatus.EnRoute, UnitStatus.Cancelled],
        [UnitStatus.EnRoute] = [UnitStatus.OnScene, UnitStatus.Cancelled],
        [UnitStatus.OnScene] = [UnitStatus.Operating, UnitStatus.Transporting, UnitStatus.Available],
        [UnitStatus.Operating] = [UnitStatus.Transporting, UnitStatus.Available],
        [UnitStatus.Transporting] = [UnitStatus.Available],
        [UnitStatus.Cancelled] = [UnitStatus.Available],
        [UnitStatus.OutOfService] = [UnitStatus.Available],
    };

    public static bool CanTransition(UnitStatus from, UnitStatus to) =>
        to == UnitStatus.OutOfService ? from != UnitStatus.OutOfService : Allowed[from].Contains(to);

    public static IReadOnlyList<UnitStatus> NextStatuses(UnitStatus from) =>
        [.. Allowed[from], .. from == UnitStatus.OutOfService ? [] : new[] { UnitStatus.OutOfService }];

    /// <summary>The unit is committed to an incident and expected to be in radio/data contact.</summary>
    public static bool IsCommitted(UnitStatus status) =>
        status is UnitStatus.Dispatched or UnitStatus.EnRoute or UnitStatus.OnScene
            or UnitStatus.Operating or UnitStatus.Transporting;
}
