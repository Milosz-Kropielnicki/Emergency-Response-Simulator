using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Power supply (Design Document §10.5, §18): the start of the classic cascade. While an outage lasts, traffic signals
/// inside it are dark (<see cref="TrafficSystem"/>) and hospitals inside it run on generators (<see cref="MedicalSystem"/>).
/// <list type="bullet">
/// <item>The utility restores supply after about 90 minutes, or 35 minutes after command notifies it. A substation
/// still in the fire or under water cannot be repaired until the hazard has passed.</item>
/// <item>Command hears about it the way a control room would: a member of the public rings in a few minutes after the
/// lights go out, then the utility reports the fault and its estimate.</item>
/// </list>
/// </summary>
public sealed class InfrastructureSystem(IRoutingService? routing = null) : ISimulationSystem
{
    public const string Utility = "ESB Networks";

    public static readonly TimeSpan PublicCallDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan UtilityReportDelay = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan NotifiedRepairTime = TimeSpan.FromMinutes(35);

    /// <summary>Customers per square kilometre in a dense urban area, for the utility's estimate.</summary>
    private const double CustomersPerSquareKm = 4000;

    private readonly HashSet<Guid> _called = [];

    public int Order => 8; // before traffic and medical systems read the outages

    public void Update(SimulationContext context)
    {
        var world = context.World;
        foreach (var outage in world.Outages.Values.ToList())
        {
            var elapsed = context.SimTime - outage.StartedAt;

            // Told early, the utility sends a crew straight away.
            if (world.Notified.Where(n => IsUtility(n.Key)).Select(n => (DateTimeOffset?)n.Value).Min() is { } notifiedAt)
            {
                var sooner = (notifiedAt > outage.StartedAt ? notifiedAt : outage.StartedAt) + NotifiedRepairTime;
                if (sooner < outage.RestoreAt) outage.RestoreAt = sooner;
            }

            // Nobody can work on a substation that is still burning or flooded.
            if (outage.SiteId is { } siteId && world.Sites.TryGetValue(siteId, out var site) && StillAffected(world, site.Location)
                && outage.RestoreAt < context.SimTime + TimeSpan.FromMinutes(20))
            {
                outage.RestoreAt = context.SimTime + TimeSpan.FromMinutes(20);
            }

            if (elapsed >= PublicCallDelay && _called.Add(outage.Id))
                PublicCall(context, outage);

            if (elapsed >= UtilityReportDelay && !outage.Reported)
            {
                outage.Reported = true;
                var customers = Math.Round(Math.PI * Math.Pow(outage.RadiusMeters / 1000, 2) * CustomersPerSquareKm / 100) * 100;
                context.EmitPerceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, Utility,
                    $"Fault: {outage.Cause}. Supply lost to about {customers:N0} customers within {outage.RadiusMeters:F0} m. " +
                    $"Estimated restoration {outage.RestoreAt.ToLocalTime():HH:mm}.",
                    Confidence.High, VerificationStatus.Confirmed, outage.Centre, outage.RadiusMeters), EventSources.Comms);
            }

            if (context.SimTime >= outage.RestoreAt)
            {
                world.Outages.Remove(outage.Id);
                context.EmitTruth(new PowerRestored(outage.Id));
                if (outage.Reported)
                {
                    context.EmitPerceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, Utility,
                        $"Supply restored {Near(outage.Centre, "around ", "to the affected area")}.", Confidence.High,
                        VerificationStatus.Confirmed, outage.Centre, outage.RadiusMeters), EventSources.Comms);
                }
            }
        }
    }

    public static bool IsUtility(string recipient)
    {
        var who = recipient.ToLowerInvariant();
        return who.Contains("esb") || who.Contains("power") || who.Contains("electric");
    }

    private static bool StillAffected(WorldState world, GeoPoint location) =>
        HazardSystem.ExposuresAt(world, location).Any(e => e.Exposure.Level >= HazardLevel.Moderate);

    private void PublicCall(SimulationContext context, PowerOutage outage)
    {
        var random = SimRandom.For(outage.Id, 1);
        var location = GeoMath.Destination(outage.Centre, random.NextDouble() * 360, random.NextDouble() * outage.RadiusMeters * 0.6);
        context.EmitPerceived(new CallReceived(Guid.NewGuid(), "999 caller (landline)",
            $"The power's gone off {Near(location, "all along ", "around here")} and the traffic lights are out, it's chaos at the junction",
            location, 300), EventSources.Comms);
    }

    /// <summary>"all along Pearse Street", or the fallback when there is no road name to give.</summary>
    private string Near(GeoPoint point, string prefix, string fallback) =>
        routing?.NearestRoad(point) is { } road && road.DistanceMeters < 150 ? prefix + road.Name : fallback;
}
