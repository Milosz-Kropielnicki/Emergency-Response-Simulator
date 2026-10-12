using System.Text.Json;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Server;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using static Emergency_Response_Simulator.Tests.InProcessServer;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 11 M6: observer mode, over a real in-process server.</summary>
public class ObserverTests
{
    private static IC2Service C2Of(SimulationConnection client) => RemoteProxy.Create<IC2Service>(client, SimulationHubProtocol.Services.C2);

    private static IAssessmentService AssessorOf(SimulationConnection client) =>
        RemoteProxy.Create<IAssessmentService>(client, SimulationHubProtocol.Services.Assessment);

    private static List<SimEvent> MirrorOf(SimulationConnection client) => [.. client.Mirror.ReadAsync(client.Session!.SessionId).ToBlockingEnumerable()];

    private static void Ok(CommandResult result) => Assert.True(result.Succeeded, result.Error);

    [Fact]
    public async Task The_server_refuses_an_observer_every_command_whatever_the_client_sends()
    {
        await using var server = await StartAsync();
        var olly = await server.JoinAsync("Olly", UserRole.Observer);
        await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var record = server.Session.Find(olly.Ticket!.ParticipantId)!;

        var refused = 0;
        foreach (var (service, method) in CommandDispatcher.Callable.Where(c => c.Service is SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap
                     or SimulationHubProtocol.Services.Control or SimulationHubProtocol.Services.Instructor))
        {
            var args = CommandDispatcher.WireParameters(method).Select(_ => default(JsonElement)).ToArray();
            var ex = await Assert.ThrowsAsync<HubException>(() =>
                server.Dispatcher.InvokeAsync(server.Session, record, service, method.Name, args, CancellationToken.None));
            Assert.Equal("Observers watch: they don't give commands.", ex.Message);
            refused++;
        }
        Assert.True(refused > 60);

        // Through the client too, and the session's own controls.
        Assert.False((await C2Of(olly).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation)).Succeeded);
        await Assert.ThrowsAsync<HubException>(() => olly.HandOverRoleAsync(server.Instructor.Ticket!.ParticipantId, UserRole.Observer));
        await Assert.ThrowsAsync<HubException>(() => olly.StartSessionAsync());
        Assert.DoesNotContain(server.Store.All(), e => e.ActorId == record.Id && e.Source is EventSources.C2 or EventSources.Iap);
    }

    [Fact]
    public async Task Ground_truth_is_granted_per_observer_backfilled_and_taken_away()
    {
        await using var server = await StartAsync();
        var granted = await server.JoinAsync("Olly", UserRole.Observer);
        var plain = await server.JoinAsync("Pam", UserRole.Observer);
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        await server.StepAsync(TimeSpan.FromMinutes(1)); // the fire starts: truth events
        await server.CaughtUpAsync(granted);
        Assert.DoesNotContain(MirrorOf(granted), e => e.Visibility == EventVisibility.Truth);
        Assert.False(granted.Session!.Truth);

        // Only the instructor grants it, and only to observers.
        await Assert.ThrowsAsync<HubException>(() => ic.SetObserverTruthAsync(granted.Ticket!.ParticipantId, true));
        await Assert.ThrowsAsync<HubException>(() => server.Instructor.SetObserverTruthAsync(ic.Ticket!.ParticipantId, true));

        await server.Instructor.SetObserverTruthAsync(granted.Ticket!.ParticipantId, true);
        await Until(() => granted.Session!.Truth, TimeSpan.FromSeconds(10));
        var firstTruth = server.Store.All().First(e => e.Visibility == EventVisibility.Truth).Sequence;
        await Until(() => MirrorOf(granted).Any(e => e.Sequence == firstTruth), TimeSpan.FromSeconds(10)); // backfilled
        await server.StepAsync(TimeSpan.FromMinutes(1));
        await server.CaughtUpAsync(granted);
        var held = MirrorOf(granted);
        Assert.Equal(server.Store.All().Select(e => e.Sequence), held.Select(e => e.Sequence)); // the whole stream, in order, no gaps
        await Until(() => granted.Snapshot is { } world && world.Incidents.Count > 0, TimeSpan.FromSeconds(10)); // and the world itself

        await server.CaughtUpAsync(plain);
        Assert.DoesNotContain(MirrorOf(plain), e => e.Visibility == EventVisibility.Truth && e.Payload is not AssessmentNoteRecorded);
        await server.CaughtUpAsync(ic);
        Assert.Contains(CopOf(ic).Participants, p => p.DisplayName == "Olly" && p.TruthGranted);

        // Taken away: nothing new from then on.
        await server.Instructor.SetObserverTruthAsync(granted.Ticket.ParticipantId, false);
        await Until(() => !granted.Session!.Truth, TimeSpan.FromSeconds(10));
        var before = MirrorOf(granted).Count(e => e.Visibility == EventVisibility.Truth);
        await server.StepAsync(TimeSpan.FromMinutes(1));
        await server.CaughtUpAsync(granted);
        Assert.Equal(before, MirrorOf(granted).Count(e => e.Visibility == EventVisibility.Truth));

        // A grant lasts only while they observe.
        await server.Instructor.SetObserverTruthAsync(plain.Ticket!.ParticipantId, true);
        await server.Instructor.AssignRoleAsync(plain.Ticket.ParticipantId, UserRole.Planning);
        await Until(() => !plain.Session!.Truth, TimeSpan.FromSeconds(10));
        Assert.False(server.Session.Find(plain.Ticket.ParticipantId)!.TruthGranted);
    }

    [Fact]
    public async Task Notes_are_tagged_to_a_participant_and_an_event_shared_by_assessors_and_hidden_from_trainees()
    {
        await using var server = await StartAsync();
        var olly = await server.JoinAsync("Olly", UserRole.Observer);
        var pam = await server.JoinAsync("Pam", UserRole.Observer);
        var ic = await server.JoinAsync("Alex", UserRole.IncidentCommander);
        var ops = await server.JoinAsync("Bea", UserRole.Operations);
        var alex = ic.Ticket!.ParticipantId;
        var created = await C2Of(ic).CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.High, BarrowStreetScenario.FireLocation, "Barrow Street");
        var about = created.EventSequence!.Value;

        Ok(await AssessorOf(olly).RecordNoteAsync(alex, "Opened the incident quickly on the second call", AssessmentRating.Competent, about));
        Ok(await AssessorOf(server.Instructor).RecordNoteAsync(alex, "Didn't ask where the casualties were", AssessmentRating.Developing));
        Ok(await AssessorOf(pam).RecordNoteAsync(ops.Ticket!.ParticipantId, "", AssessmentRating.NotYetCompetent)); // a rating alone will do

        // What isn't allowed.
        Assert.False((await AssessorOf(ic).RecordNoteAsync(ops.Ticket.ParticipantId, "Good work", AssessmentRating.Competent)).Succeeded); // players don't assess
        Assert.False((await AssessorOf(olly).RecordNoteAsync(pam.Ticket!.ParticipantId, "Watching well")).Succeeded);                   // observers aren't assessed
        Assert.False((await AssessorOf(olly).RecordNoteAsync(alex, "Something", null, 999_999)).Succeeded);                                 // no such event
        Assert.False((await AssessorOf(olly).RecordNoteAsync(alex, "  ")).Succeeded);

        var notes = server.Store.All().Where(e => e.Payload is AssessmentNoteRecorded).ToList();
        Assert.Equal(3, notes.Count);
        Assert.All(notes, e => Assert.Equal(EventVisibility.Truth, e.Visibility));
        Assert.Equal(olly.Ticket!.ParticipantId, notes[0].ActorId);
        Assert.Equal(about, ((AssessmentNoteRecorded)notes[0].Payload).AboutSequence);

        // Every assessor sees every note (Pam has no truth); the trainees never do.
        await server.CaughtUpAsync(pam);
        await Until(() => MirrorOf(pam).Count(e => e.Payload is AssessmentNoteRecorded) == 3, TimeSpan.FromSeconds(10));
        await Until(() => MirrorOf(olly).Count(e => e.Payload is AssessmentNoteRecorded) == 3, TimeSpan.FromSeconds(10));
        await server.CaughtUpAsync(ic);
        await server.CaughtUpAsync(ops);
        Assert.DoesNotContain(MirrorOf(ic), e => e.Payload is AssessmentNoteRecorded);
        Assert.DoesNotContain(MirrorOf(ops), e => e.Payload is AssessmentNoteRecorded);

        // The review: notes on the timeline (and in the participant's own), and in their score.
        var report = await server.Session.Services.GetRequiredService<IAarService>().BuildReportAsync(server.Session.Id);
        var note = report.Notes.First();
        Assert.Equal(("Alex", AssessmentRating.Competent, about), (note.Participant, note.Rating, note.AboutSequence));
        Assert.Contains("INC-", note.About);
        Assert.Contains("Olly", note.By);
        Assert.Contains(report.TimelineOf(alex), t => t.Category == "ASSESSMENT" && t.Text.Contains("Competent"));
        Assert.Equal(2, report.TimelineOf(alex).Count(t => t.Category == "ASSESSMENT"));

        var scored = report.Participants.Single(p => p.Name == "Alex");
        Assert.Equal((1, 1, 0, 75), (scored.Competent, scored.Developing, scored.NotYetCompetent, scored.ObserverScore!.Value));
        Assert.NotNull(scored.Combined);
        Assert.Equal(scored.Score is { } decisions ? (int)Math.Round((decisions + 75) / 2.0) : 75, scored.Combined);
        Assert.Equal(Rubric.Level(scored.Combined!.Value), scored.Level);
        var bea = report.Participants.Single(p => p.Name == "Bea");
        Assert.Equal((0, AssessmentRating.NotYetCompetent), (bea.ObserverScore, bea.Level)); // no decisions of her own: the rating stands alone
    }

    [Fact]
    public void The_rubric_maps_scores_both_ways()
    {
        Assert.Equal(100, Rubric.Score(AssessmentRating.Competent));
        Assert.Equal(AssessmentRating.Competent, Rubric.Level(75));
        Assert.Equal(AssessmentRating.Developing, Rubric.Level(74));
        Assert.Equal(AssessmentRating.Developing, Rubric.Level(40));
        Assert.Equal(AssessmentRating.NotYetCompetent, Rubric.Level(39));
        Assert.Equal("Not yet competent", Rubric.Name(AssessmentRating.NotYetCompetent));
    }
}
