namespace Emergency_Response_Simulator.Simulation.Routing;

/// <summary>How much traffic slows vehicles on a piece of road at a given time.</summary>
public interface ITrafficModel
{
    /// <summary>Multiplier on free-flow speed: 1 = empty roads, 0.5 = half speed.</summary>
    double SpeedFactor(RoadEdge edge, DateTimeOffset at, bool emergency);
}

/// <summary>
/// Typical weekday congestion by time of day. Main roads suffer most at peak times; vehicles under
/// blue lights recover part of the delay. <see cref="LiveTraffic"/> adds simulated congestion on top.
/// </summary>
public sealed class TimeOfDayTraffic : ITrafficModel
{
    public double SpeedFactor(RoadEdge edge, DateTimeOffset at, bool emergency) => SpeedFactor(edge.Class, at, emergency);

    public double SpeedFactor(RoadClass roadClass, DateTimeOffset at, bool emergency)
    {
        var local = at.ToLocalTime();
        var hour = local.Hour + local.Minute / 60.0;
        var weekday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

        var period = weekday && (hour is >= 7 and < 9.5 || hour is >= 16.5 and < 19) ? Period.Peak
            : hour is >= 7 and < 22 ? Period.Day
            : Period.Night;

        var mainRoad = roadClass is RoadClass.Motorway or RoadClass.Trunk or RoadClass.Primary or RoadClass.Secondary;
        var factor = (period, mainRoad, roadClass) switch
        {
            (Period.Peak, true, _) => 0.45,
            (Period.Peak, false, RoadClass.Tertiary) => 0.6,
            (Period.Peak, false, _) => 0.75,
            (Period.Day, true, _) => 0.7,
            (Period.Day, false, RoadClass.Tertiary) => 0.8,
            (Period.Day, false, _) => 0.9,
            _ => 1.0,
        };

        // Traffic pulls over for blue lights, recovering part of the delay.
        return emergency ? Math.Sqrt(factor) : factor;
    }

    private enum Period
    {
        Night,
        Day,
        Peak,
    }
}

/// <summary>Empty roads: for tests and night-time what-ifs.</summary>
public sealed class FreeFlowTraffic : ITrafficModel
{
    public double SpeedFactor(RoadEdge edge, DateTimeOffset at, bool emergency) => 1.0;
}
