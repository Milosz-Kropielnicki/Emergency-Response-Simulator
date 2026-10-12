namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// Which radio channels each role works on (Phase 11 M7): the same channels its workspace's comms hub shows. A role
/// talks and listens on its own channels; observers listen on every channel and never talk; the instructor works them all.
/// </summary>
public static class VoiceChannels
{
    public static IReadOnlyList<string> Radio { get; } = [.. RadioPlan.Standard.Where(c => c.Kind == ChannelKind.Radio).Select(c => c.Id)];

    public static IReadOnlyList<string> For(UserRole role) => role switch
    {
        UserRole.IncidentCommander => [RadioPlan.FireCommand, RadioPlan.InterAgency],
        UserRole.Operations => [RadioPlan.FireCommand, RadioPlan.FireTac2, RadioPlan.FireTac3],
        UserRole.Safety => [RadioPlan.FireTac2, RadioPlan.FireCommand],
        UserRole.Planning => [RadioPlan.FireCommand],
        UserRole.Logistics => [RadioPlan.FireCommand, RadioPlan.FireTac3],
        UserRole.Dispatcher => [RadioPlan.FireCommand, RadioPlan.Ambulance, RadioPlan.Garda],
        UserRole.AgencyChief => [RadioPlan.InterAgency, RadioPlan.Ambulance, RadioPlan.Garda],
        UserRole.FinanceAdmin or UserRole.Liaison or UserRole.PublicInformation => [RadioPlan.InterAgency],
        _ => Radio, // the instructor and observers
    };

    public static bool CanTalk(UserRole role, string channel) => role != UserRole.Observer && For(role).Contains(channel);

    public static bool Hears(UserRole role, string channel) => For(role).Contains(channel);
}
