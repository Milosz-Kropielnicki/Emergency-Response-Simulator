using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Simulation.Instructor;

/// <summary>
/// The instructor console's actions on a session (Phase 11 M4). Each runs on the world at once, between ticks and
/// whether or not the clock runs (<see cref="SimulationEngine.ApplyAsync{T}"/>). What it records is ground truth
/// stamped with whoever the command context names (the instructor, on the server), so the review shows it and the
/// trainees don't see it happen; what it says in the world travels through the usual comms, with their delays.
/// </summary>
public sealed class InstructorService(SimulationEngine engine, IEnumerable<ISimulationSystem> systems, ICopService cop) : IInstructorService
{
    private ScriptedInjectSystem? Script => systems.OfType<ScriptedInjectSystem>().FirstOrDefault();

    // ---- Injects ----

    public Task<IReadOnlyList<InjectInfo>> ListInjectsAsync(CancellationToken cancellationToken = default) =>
        engine.ApplyAsync<IReadOnlyList<InjectInfo>>(_ => Script?.Script() ?? [], cancellationToken);

    public Task<CommandResult> FireInjectAsync(int number, CancellationToken cancellationToken = default) =>
        ScriptAsync((script, context) => script.FireNow(context, number), $"Inject {number} fired", cancellationToken);

    public Task<CommandResult> HoldInjectAsync(int number, CancellationToken cancellationToken = default) =>
        ScriptAsync((script, context) => script.Hold(context, number), $"Inject {number} held", cancellationToken);

    public Task<CommandResult> ReleaseInjectAsync(int number, CancellationToken cancellationToken = default) =>
        ScriptAsync((script, context) => script.Release(context, number), $"Inject {number} released", cancellationToken);

    public Task<CommandResult> SkipInjectAsync(int number, CancellationToken cancellationToken = default) =>
        ScriptAsync((script, context) => script.Skip(context, number), $"Inject {number} skipped", cancellationToken);

    private async Task<CommandResult> ScriptAsync(Func<ScriptedInjectSystem, SimulationContext, string?> action, string done, CancellationToken cancellationToken)
    {
        if (Script is not { } script) return CommandResult.Fail("This session has no scripted injects.");
        var refused = await engine.ApplyAsync(context => action(script, context), cancellationToken);
        return refused is null ? new CommandResult(true, Detail: done) : CommandResult.Fail(refused);
    }

    public Task<IReadOnlyList<InjectTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(InjectTemplates.All);

    public async Task<CommandResult> FireAdHocAsync(string template, IReadOnlyDictionary<string, string>? values = null, CancellationToken cancellationToken = default)
    {
        Inject? inject;
        try
        {
            inject = InjectTemplates.Create(template, values);
        }
        catch (ArgumentException ex)
        {
            return CommandResult.Fail(ex.Message);
        }
        if (inject is null) return CommandResult.Fail($"There is no inject template '{template}'.");

        return await engine.ApplyAsync(context =>
        {
            // A template naming a unit needs one that exists.
            if (InjectTemplates.All.First(t => t.Key == template).Parameters.Any(p => p.Key == "callsign"))
            {
                var callsign = values?.GetValueOrDefault("callsign") is { Length: > 0 } given ? given
                    : InjectTemplates.All.First(t => t.Key == template).Parameters.First(p => p.Key == "callsign").Default;
                if (InjectTemplates.Unit(context, callsign) is null) return CommandResult.Fail($"There is no unit called {callsign}.");
            }
            ScriptedInjectSystem.FireAdHoc(context, template, inject);
            return new CommandResult(true, Detail: inject.Description);
        }, cancellationToken);
    }

    // ---- Role-play ----

    public async Task<CommandResult> RolePlayAsync(RolePlayRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.As)) return CommandResult.Fail("Say who you are speaking as.");
        if (string.IsNullOrWhiteSpace(request.Text)) return CommandResult.Fail("Say what they say.");
        var who = request.As.Trim();
        var text = request.Text.Trim();

        return await engine.ApplyAsync(context =>
        {
            var world = context.World;
            var message = Guid.NewGuid();
            List<Guid> refers = [message];
            string? channel = null;
            Action send;
            switch (request.Kind)
            {
                case RolePlayKind.Radio:
                    var unit = world.Units.Values.FirstOrDefault(u => string.Equals(u.Callsign, who, StringComparison.OrdinalIgnoreCase));
                    channel = request.Channel is { Length: > 0 } named ? named : unit?.Channel;
                    if (channel is null) return CommandResult.Fail("Choose the radio channel to speak on.");
                    var report = new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, unit?.Callsign ?? who, text, Confidence.Medium,
                        VerificationStatus.Reported, unit?.Location, unit is null ? null : 50, unit?.Id);
                    refers.Add(report.ReportId);
                    var on = channel;
                    send = () => CommsNet.Voice(context, on, unit?.Callsign ?? who, $"Control, {unit?.Callsign ?? who}: {text}", report, message);
                    break;

                case RolePlayKind.Agency:
                    var said = new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, who, text, Confidence.Medium, VerificationStatus.Reported, null, null);
                    refers.Add(said.ReportId);
                    send = () => CommsNet.Chat(context, who, $"{who}: {text}", said, null, message);
                    break;

                case RolePlayKind.Caller:
                    refers = [message]; // the call's own id
                    send = () => CommsNet.EmergencyCall(context, new PendingCall
                    {
                        Id = message, Caller = who, Summary = text, Location = request.Location ?? Near(world), AccuracyMeters = request.Location is null ? 400 : 80,
                        Patience = TimeSpan.FromMinutes(3),
                    });
                    break;

                case RolePlayKind.Journalist:
                    var enquiry = new MediaEnquiryReceived(Guid.NewGuid(), request.IncidentId ?? LatestIncident(world), who, text);
                    refers.Add(enquiry.EnquiryId);
                    send = () => CommsNet.Chat(context, who, $"{who}: {text}", enquiry, null, message);
                    break;

                case RolePlayKind.Politician:
                    var incident = request.IncidentId ?? LatestIncident(world);
                    var office = PoliticalPressureSystem.OfficeOf(who);
                    var to = PoliticalPressureSystem.To(world, incident);
                    var call = new PoliticalCallReceived(Guid.NewGuid(), incident, office, who, text, (int)office, to);
                    refers.Add(call.CallId);
                    send = () => CommsNet.Chat(context, who, $"{who} for the {to}: {text}", call, null, message);
                    break;

                case RolePlayKind.Official:
                    var subject = text.Split(['.', '?', '!'], 2)[0];
                    var approval = new ApprovalRequested(Guid.NewGuid(), request.IncidentId ?? LatestIncident(world),
                        subject.Length > 80 ? subject[..80] + "…" : subject, text, who);
                    refers.Add(approval.ApprovalId);
                    send = () => CommsNet.Chat(context, who, $"{who}: {text}", approval, null, message);
                    break;

                default:
                    return CommandResult.Fail("Unknown kind of role-play.");
            }
            // Recorded first, so the review knows what follows in the world is role-play.
            context.EmitTruth(new InstructorRolePlay(Guid.NewGuid(), request.Kind.ToString(), who, channel, text, refers), EventSources.Instructor);
            send();
            return new CommandResult(true, EntityId: message, Detail: $"Said as {who}");
        }, cancellationToken);
    }

    private static Guid? LatestIncident(WorldState world) =>
        world.Interagency.Incidents.Where(i => !i.Value.Closed).OrderByDescending(i => i.Value.CreatedAt).Select(i => (Guid?)i.Key).FirstOrDefault();

    private static GeoPoint Near(WorldState world) =>
        world.Incidents.Values.Where(i => !i.Extinguished).OrderByDescending(i => i.Severity).Select(i => (GeoPoint?)i.Location).FirstOrDefault()
        ?? BarrowStreetScenario.FireLocation;

    // ---- Roles the simulation plays (the role-player registry) ----

    private static IEnumerable<(string Key, string Name, string System, string Covers)> Roles(WorldState world) =>
    [
        (AiRoles.RegionalDutyOfficer, CommandResponseSystem.RegionalDutyOfficer, nameof(CommandResponseSystem), "Approves or declines mutual aid"),
        (AiRoles.NationalDirectorate, "National Directorate", nameof(CommandResponseSystem), "Specialist teams, and mutual aid outside the agreements"),
        (AiRoles.OutsideOrganisations, "Outside organisations", nameof(CommandResponseSystem), "Replies to command's notifications: hospitals, utilities, the council…"),
        (AiRoles.Media, "The media", nameof(InteragencySystem), "Journalists ringing with questions (taken over: they ring only when you play one)"),
        (AiRoles.Politicians, "Politicians", nameof(PoliticalPressureSystem),
            "The local TD, the council chief executive and the minister's office ringing command (taken over: only when you play them)"),
        .. world.Agencies.Where(a => a.Value.AiControlled).OrderBy(a => a.Value.Name)
            .Select(a => (AiRoles.Agency(a.Key), $"{a.Value.Name} control room", $"{nameof(AgencyAiSystem)}, {nameof(InteragencySystem)}",
                "Its own jobs and initiative; answers to invitations to unified command and requests for units")),
    ];

    private string? Participant(Guid? id) => id is { } who ? cop.Participants.FirstOrDefault(p => p.Id == who)?.DisplayName ?? "a participant" : null;

    public Task<IReadOnlyList<AiRoleInfo>> ListAiRolesAsync(CancellationToken cancellationToken = default) =>
        engine.ApplyAsync<IReadOnlyList<AiRoleInfo>>(context =>
        {
            var world = context.World;
            var waiting = Waiting(world).GroupBy(i => i.RoleKey).ToDictionary(g => g.Key, g => g.Count());
            return [.. Roles(world).Select(r => new AiRoleInfo(r.Key, r.Name, r.System, r.Covers, world.PlayedByInstructor(r.Key), waiting.GetValueOrDefault(r.Key),
                world.HumanRoles.TryGetValue(r.Key, out var by) ? by is null ? "you" : Participant(by) : null, AiRoles.AgencyOf(r.Key) is not null))];
        }, cancellationToken);

    public Task<CommandResult> TakeOverAsync(string roleKey, CancellationToken cancellationToken = default) =>
        engine.ApplyAsync(context =>
        {
            if (Roles(context.World).FirstOrDefault(r => r.Key == roleKey) is not { Key: not null } role) return CommandResult.Fail("Unknown role.");
            if (context.World.PlayedByInstructor(roleKey)) return CommandResult.Fail($"You already play {role.Name}.");
            if (context.World.HumanRoles.TryGetValue(roleKey, out var by)) return CommandResult.Fail($"{Participant(by)} runs {role.Name}: hand it back first.");
            context.EmitTruth(new AiRoleTakenOver(roleKey, role.Name), EventSources.Instructor);
            return new CommandResult(true, Detail: $"You play {role.Name}");
        }, cancellationToken);

    public Task<CommandResult> HandBackAsync(string roleKey, CancellationToken cancellationToken = default) =>
        engine.ApplyAsync(context =>
        {
            if (Roles(context.World).FirstOrDefault(r => r.Key == roleKey) is not { Key: not null } role) return CommandResult.Fail("Unknown role.");
            if (!context.World.HumanRoles.TryGetValue(roleKey, out var by)) return CommandResult.Fail($"The simulation already plays {role.Name}.");
            if (by is not null && AiRoles.AgencyOf(roleKey) is { } agency)
                // A participant's agency: everyone sees who runs it, so this is a session fact rather than ground truth.
                context.EmitPerceived(new AgencyControlChanged(agency, AgencyController.Simulation), EventSources.Session);
            else
                context.EmitTruth(new AiRoleHandedBack(roleKey, role.Name), EventSources.Instructor);
            return new CommandResult(true, Detail: $"The simulation plays {role.Name} again");
        }, cancellationToken);

    public Task<CommandResult> GiveToParticipantAsync(string roleKey, Guid participantId, CancellationToken cancellationToken = default) =>
        engine.ApplyAsync(context =>
        {
            var world = context.World;
            if (Roles(world).FirstOrDefault(r => r.Key == roleKey) is not { Key: not null } role) return CommandResult.Fail("Unknown role.");
            if (AiRoles.AgencyOf(roleKey) is not { } agency) return CommandResult.Fail($"Only an agency's control room can be given to a participant; you can take over {role.Name} yourself.");
            if (cop.Participants.FirstOrDefault(p => p.Id == participantId && p.Present) is not { } participant)
                return CommandResult.Fail("That person isn't in the session.");
            if (participant.Role != UserRole.AgencyChief)
                return CommandResult.Fail($"{participant.DisplayName} is {Core.Model.Roles.Name(participant.Role)}: make them an agency chief first.");
            if (world.HumanRoles.TryGetValue(roleKey, out var by))
                return CommandResult.Fail(by is null ? $"You play {role.Name}: hand it back first." : $"{Participant(by)} already runs {role.Name}.");
            if (world.HumanRoles.Any(r => r.Value == participantId))
                return CommandResult.Fail($"{participant.DisplayName} already runs an agency.");
            context.EmitPerceived(new AgencyControlChanged(agency, AgencyController.Participant, participantId, participant.DisplayName), EventSources.Session);
            return new CommandResult(true, Detail: $"{participant.DisplayName} runs {role.Name}");
        }, cancellationToken);

    public Task<IReadOnlyList<AiRoleItem>> ListWaitingAsync(CancellationToken cancellationToken = default) =>
        engine.ApplyAsync<IReadOnlyList<AiRoleItem>>(context => Waiting(context.World), cancellationToken);

    private static List<AiRoleItem> Waiting(WorldState world)
    {
        var names = Roles(world).ToDictionary(r => r.Key, r => r.Name);
        var items = new List<AiRoleItem>();
        foreach (var pending in world.PendingRequests.Where(p => !p.Decided))
            if (CommandResponseSystem.TermsOf(world, pending.Request).Role is { } role && world.PlayedByInstructor(role))
                items.Add(new(pending.Request.RequestId, role, names[role], AiRoleItemKind.ResourceRequest,
                    $"{EventDescriber.Humanize(pending.Request.Kind)}: {pending.Request.Description}" +
                    (pending.Request.Justification is { Length: > 0 } why ? $" ({why})" : ""), pending.RequestedAt, pending.Request.Quantity));
        if (world.PlayedByInstructor(AiRoles.OutsideOrganisations))
            foreach (var pending in world.PendingNotifications)
                items.Add(new(pending.Notification.NotificationId, AiRoles.OutsideOrganisations, names[AiRoles.OutsideOrganisations], AiRoleItemKind.Notification,
                    $"To {pending.Notification.Recipient}: {pending.Notification.Message}", pending.SentAt));
        foreach (var invitation in world.Interagency.Invitations)
            if (AiRoles.Agency(invitation.AgencyId) is var key && world.PlayedByInstructor(key))
                items.Add(new(invitation.Id, key, names.GetValueOrDefault(key, "An agency"), AiRoleItemKind.Invitation,
                    $"Invited to unified command{IncidentName(world, invitation.IncidentId)}", invitation.At));
        foreach (var pending in world.Interagency.Assistance)
            if (AiRoles.Agency(pending.Request.AgencyId) is var key && world.PlayedByInstructor(key))
                items.Add(new(pending.Request.RequestId, key, names.GetValueOrDefault(key, "An agency"), AiRoleItemKind.Assistance,
                    $"Asked for {pending.Request.Quantity} unit(s){IncidentName(world, pending.Request.IncidentId)}: {pending.Request.Task}", pending.At, pending.Request.Quantity));
        return [.. items.OrderBy(i => i.Since)];
    }

    private static string IncidentName(WorldState world, Guid incidentId) =>
        world.Interagency.Incidents.TryGetValue(incidentId, out var facts) ? $" at {facts.Place}" : "";

    public Task<CommandResult> DecideAsync(AiRoleDecision decision, CancellationToken cancellationToken = default) =>
        engine.ApplyAsync(context =>
        {
            if (Waiting(context.World).FirstOrDefault(i => i.Id == decision.ItemId) is not { } item)
                return CommandResult.Fail("That isn't waiting for you any more (decided, withdrawn, or the role was handed back).");
            if (item.Kind == AiRoleItemKind.Notification && string.IsNullOrWhiteSpace(decision.Text))
                return CommandResult.Fail("Write the reply.");

            var verdict = item.Kind == AiRoleItemKind.Notification ? "Replied"
                : decision.Approve ? (decision.Quantity is { } n && n < (item.Quantity ?? n) ? $"Approved {n} of {item.Quantity}" : "Approved") : "Declined";
            var said = $"{verdict}: {item.Summary}" + (string.IsNullOrWhiteSpace(decision.Text) ? "" : $" — \"{decision.Text.Trim()}\"");
            var message = Guid.NewGuid();
            // Recorded first, so the review knows the answer that follows in the world is the instructor's.
            context.EmitTruth(new InstructorRolePlay(Guid.NewGuid(), "Decision", item.Role, null, said, [message, item.Id]), EventSources.Instructor);

            var done = item.Kind switch
            {
                AiRoleItemKind.ResourceRequest => CommandResponseSystem.DecideByHand(context, item.Id, decision.Approve, decision.Quantity, decision.Text, item.Role, message),
                AiRoleItemKind.Notification => CommandResponseSystem.ReplyByHand(context, item.Id, decision.Text!.Trim(), message),
                AiRoleItemKind.Invitation => InteragencySystem.AnswerInvitationByHand(context, item.Id, decision.Approve, decision.Text, message),
                _ => InteragencySystem.AnswerAssistanceByHand(context, item.Id, decision.Approve, decision.Quantity, decision.Text, message),
            };
            return done ? new CommandResult(true, EntityId: message, Detail: $"{verdict} as {item.Role}") : CommandResult.Fail("That couldn't be decided.");
        }, cancellationToken);

    // ---- Debrief ----

    public async Task<CommandResult> BookmarkAsync(string note, Guid? participantId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(note)) return CommandResult.Fail("Write a note for the bookmark.");
        var recorded = await engine.PublishAsync(new InstructorBookmarked(Guid.NewGuid(), note.Trim(), participantId), EventVisibility.Truth, EventSources.Instructor,
            cancellationToken);
        return CommandResult.Ok(recorded.Sequence);
    }

    // ---- Exercise control (Phase 12) ----

    public async Task<CommandResult> SendExerciseMessageAsync(string text, Guid? participantId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return CommandResult.Fail("Write the message.");
        string? to = null;
        if (participantId is { } id)
        {
            if (cop.Participants.FirstOrDefault(p => p.Id == id && p.Present) is not { } participant)
                return CommandResult.Fail("That person isn't in the session.");
            to = participant.DisplayName;
        }
        // Perceived, so the players' streams carry it; it is a session fact, not something in the world.
        var recorded = await engine.PublishAsync(new ExerciseControlMessage(Guid.NewGuid(), text.Trim(), participantId), EventVisibility.Perceived,
            EventSources.Instructor, cancellationToken);
        return CommandResult.Ok(recorded.Sequence) with { Detail = to is null ? "Sent to everyone" : $"Sent to {to}" };
    }

    // ---- Scenario design & adaptive training (Phase 19) ----

    public async Task<CommandResult> LoadScenarioAsync(ScenarioDefinition scenario, CancellationToken cancellationToken = default)
    {
        var problems = ScenarioDesign.Validate(scenario, InjectTemplates.All);
        if (problems.Count > 0) return CommandResult.Fail(string.Join(" ", problems.Take(3)) + (problems.Count > 3 ? $" (and {problems.Count - 3} more)" : ""));
        if (Script is not { } script) return CommandResult.Fail("This session can't take a scenario: it has no script runner.");
        return await engine.ApplyAsync(context =>
        {
            var first = script.NextNumber;
            script.Append(ScenarioCompiler.Compile(scenario, first), script.Elapsed(context), scenario.Name);
            return new CommandResult(true, Detail: $"{scenario.Name}: {scenario.Steps.Count} step(s) added to the script as injects {first}–{script.NextNumber - 1}, timed from now");
        }, cancellationToken);
    }

    public Task<TrainingStatus> GetTrainingAsync(CancellationToken cancellationToken = default) =>
        engine.ApplyAsync(context =>
        {
            var training = context.World.Training;
            return new TrainingStatus(training.Settings.Mode, training.Level, training.Settings.RandomEvents, training.Settings.MeanMinutes,
                training.LastScore, training.LastAssessedAt, [.. training.History]);
        }, cancellationToken);

    public async Task<CommandResult> SetTrainingAsync(TrainingSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Level is < 1 or > 5) return CommandResult.Fail("Difficulty runs from 1 to 5.");
        if (settings.RandomEvents && settings.MeanMinutes < 2) return CommandResult.Fail("Random events can't come more often than every 2 minutes on average.");
        var recorded = await engine.PublishAsync(new TrainingSettingsChanged(settings), EventVisibility.Truth, EventSources.Instructor, cancellationToken);
        return CommandResult.Ok(recorded.Sequence) with
        {
            Detail = $"{(settings.Mode == DifficultyMode.Adaptive ? "Adaptive" : "Fixed")} difficulty from level {settings.Level}" +
                     (settings.RandomEvents ? $"; random events about every {settings.MeanMinutes:0} min" : "; no random events"),
        };
    }
}
