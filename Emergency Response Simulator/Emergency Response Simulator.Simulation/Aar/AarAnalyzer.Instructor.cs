using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// How the instructor ran the exercise (Phase 11 M4): the injects, the role-play, the roles they took over, and their
/// bookmarks. What their role-play produced in the world (the message, the call, the enquiry, the decision) is marked as
/// instructor role-play in the timeline, so the debrief can tell the scenario from the hand that steered it.
/// </summary>
public sealed partial class AarAnalyzer
{
    private readonly List<AarInstructorAction> _instructor = [];

    /// <summary>Ids of what role-play produced, and who the instructor was playing.</summary>
    private readonly Dictionary<Guid, string> _rolePlayed = [];

    private void TrackInstructor(SimEvent simEvent)
    {
        var by = simEvent.ActorId is not null ? ActorLabel(simEvent) : null;
        void Add(string kind, string text) => _instructor.Add(new AarInstructorAction(simEvent.SimTime, kind, text, by));
        switch (simEvent.Payload)
        {
            case InjectFired e when e.Number == 0:
                Add("Ad-hoc inject", e.Description);
                break;
            case InjectFired e:
                Add(e.Early ? "Inject fired early" : simEvent.Source == EventSources.Instructor ? "Inject fired" : "Inject (on time)", $"{e.Number}. {e.Description}");
                break;
            case InjectHeld e:
                Add("Inject held", $"{e.Number}. {e.Description}");
                break;
            case InjectReleased e:
                Add("Inject released", $"{e.Number}. {e.Description}");
                break;
            case InjectSkipped e:
                Add("Inject skipped", $"{e.Number}. {e.Description}");
                break;
            case InstructorRolePlay e:
                foreach (var id in e.Refers) _rolePlayed[id] = e.As;
                Add(e.Kind == "Decision" ? $"Decision as {e.As}" : $"Role-play ({e.Kind.ToLowerInvariant()})",
                    e.Kind == "Decision" ? e.Text : $"As {e.As}{(e.Channel is { } channel ? $" on {channel}" : "")}: \"{e.Text}\"");
                break;
            case AiRoleTakenOver e:
                Add("Took over", e.Name);
                break;
            case AiRoleHandedBack e:
                Add("Handed back", e.Name);
                break;
            case InstructorBookmarked e:
                Add("Bookmark", e.ParticipantId is { } who ? $"{_people.GetValueOrDefault(who)?.Name ?? "A participant"}: {e.Note}" : e.Note);
                break;
        }
    }

    /// <summary>Who the instructor was playing when they produced this event in the world, or null if they didn't.</summary>
    private string? RolePlayOf(DomainEvent payload) => (payload switch
    {
        CommsLogged e => e.MessageId,
        CallReceived e => e.CallId,
        MediaEnquiryReceived e => e.EnquiryId,
        PoliticalCallReceived e => e.CallId,
        ReportReceived e => e.ReportId,
        ApprovalRequested e => e.ApprovalId,
        ResourceRequestDecided e => e.RequestId,
        NotificationAnswered e => e.NotificationId,
        AgencyAssistanceDecided e => e.RequestId,
        _ => (Guid?)null,
    }) is { } id ? _rolePlayed.GetValueOrDefault(id) : null;
}
