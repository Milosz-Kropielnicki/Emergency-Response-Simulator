using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Emergency_Response_Simulator.ViewModels;
using static Emergency_Response_Simulator.Tests.TestHarness;

namespace Emergency_Response_Simulator.Tests;

/// <summary>
/// Phase 14: the public, the media, the politicians and the law. Vulnerable people who need more than a warning,
/// looting, volunteers, families looking for their own, rumours and press conferences, community leaders and agency
/// heads, and what the review makes of it all.
/// </summary>
public class PublicPressureTests
{
    private static readonly IReadOnlyList<GeoPoint> Zone = Wgs84.Circle(Dublin, 250);
    private static readonly NetTopologySuite.Geometries.Polygon Area = Wgs84.CreatePolygon(Zone);

    /// <summary>Nobody but the vulnerable group a test is about, and nobody with a car (no traffic system here).</summary>
    private static CivilianOptions Only(double elderly = 0, double mobility = 0, double homeless = 0, double pets = 0, double compliance = 0.85) => new()
    {
        PopulationPerIncident = 200, CarOwnership = 0, LimitedEnglish = 0, EvacuationCompliance = compliance,
        ElderlyShare = elderly, MobilityImpairedShare = mobility, HomelessShare = homeless, PetOwnerShare = pets,
    };

    /// <summary>The population is seeded from the incident's id: a fixed one gives every run the same people.</summary>
    private static readonly Guid Neighbourhood = new("4f6c2d9e-1b7a-4c35-9e0d-2a8b5f13c7e4");

    private static async Task<TestHarness> PopulatedAsync(CivilianOptions options, params ISimulationSystem[] more)
    {
        var harness = new TestHarness([new CivilianSystem(new UniformTerrain(0.2), options), .. more]);
        await harness.Truth(new WorldIncidentStarted(Neighbourhood, IncidentType.Other, Dublin, 0.2, 0)); // no hazard: only orders move people
        await harness.RunAsync(TimeSpan.FromSeconds(5));
        return harness;
    }

    private static async Task<Guid> EvacuateAsync(TestHarness harness)
    {
        var result = await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Zone);
        Assert.True(result.Succeeded, result.Error);
        return harness.Cop.Zones.Single(z => z.Type == ZoneType.EvacuationZone).Id;
    }

    private static List<Civilian> Inside(TestHarness harness, Vulnerability who) =>
        [.. harness.World.Civilians.Where(c => c.Is(who) && Area.Contains(c.Home.ToPoint()))];

    private static async Task<List<DomainEvent>> PayloadsAsync(TestHarness harness) => [.. (await harness.EventsAsync()).Select(e => e.Payload)];

    // ---- Vulnerable people ----

    [Fact]
    public async Task People_who_cannot_leave_unaided_wait_for_help_and_an_assisted_evacuation_gets_them_out()
    {
        var harness = await PopulatedAsync(Only(mobility: 0.3, compliance: 1.0));
        var zone = await EvacuateAsync(harness);
        var unable = Inside(harness, Vulnerability.MobilityImpaired);
        Assert.True(unable.Count > 10);

        // Warned, and still at home: willing and waiting for help, or (they are elderly too) unwilling to go at all.
        await harness.RunAsync(TimeSpan.FromMinutes(12));
        Assert.All(unable, p => Assert.True(Area.Contains(p.Location.ToPoint()), "they can't walk out"));
        Assert.All(unable, p => Assert.True(p.WaitingForHelp || p.IgnoredOrder == zone));
        Assert.True(unable.Count(p => p.WaitingForHelp) >= unable.Count * 0.5);

        Assert.True((await harness.C2.RequestAssistedEvacuationAsync(zone)).Succeeded);
        Assert.False((await harness.C2.RequestAssistedEvacuationAsync(zone)).Succeeded); // once is enough
        await harness.RunAsync(CivilianOptions_LeadTime + TimeSpan.FromMinutes(15));

        // Picked up and driven out (the drive itself is the traffic system's).
        Assert.All(unable, p => Assert.True(p.VehicleId is not null || p.State == CivilianState.Safe, $"person {p.Id} was left: {p.State}"));
        var progress = (await PayloadsAsync(harness)).OfType<AssistedEvacuationProgress>().ToList();
        Assert.NotEmpty(progress);
        Assert.Equal(unable.Count, progress[^1].Helped);
        Assert.Equal(0, progress[^1].Remaining);
    }

    private static readonly TimeSpan CivilianOptions_LeadTime = new CivilianOptions().AssistanceLeadTime;

    [Fact]
    public async Task Left_alone_they_are_still_there_half_an_hour_later_and_the_truth_records_it()
    {
        var harness = await PopulatedAsync(Only(mobility: 0.3, compliance: 1.0));
        var zone = await EvacuateAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(31));

        var stranded = Assert.Single((await harness.EventsAsync()).Where(e => e.Payload is VulnerableStranded));
        Assert.Equal(EventVisibility.Truth, stranded.Visibility);
        Assert.Equal(zone, ((VulnerableStranded)stranded.Payload).ZoneId);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Vulnerable people left behind" && !l.Mitigated);
    }

    [Fact]
    public async Task Rough_sleepers_are_not_reached_by_door_knocking_but_outreach_finds_them()
    {
        var harness = await PopulatedAsync(Only(homeless: 0.25, compliance: 1.0));
        var zone = await EvacuateAsync(harness);
        var sleepers = Inside(harness, Vulnerability.Homeless);
        Assert.True(sleepers.Count > 5);

        await harness.RunAsync(TimeSpan.FromMinutes(12));
        Assert.All(sleepers, p => Assert.Null(p.WarnedAt));
        // Everyone else was told, and has gone.
        var others = harness.World.Civilians.Where(c => !c.Is(Vulnerability.Homeless) && Area.Contains(c.Home.ToPoint())).ToList();
        Assert.True(others.Count(c => c.WarnedAt is not null) >= others.Count * 0.95);

        Assert.True((await harness.C2.RequestAssistedEvacuationAsync(zone)).Succeeded);
        await harness.RunAsync(CivilianOptions_LeadTime + TimeSpan.FromMinutes(10));
        Assert.All(sleepers, p => Assert.True(p.WarnedAt is not null || p.VehicleId is not null || p.State == CivilianState.Safe));
    }

    [Fact]
    public async Task Pet_owners_stay_or_go_back_in_unless_somewhere_takes_animals()
    {
        async Task<(double Left, int Returned)> Run(bool arrangePets)
        {
            var harness = await PopulatedAsync(Only(pets: 1.0, compliance: 1.0));
            if (arrangePets) Assert.True((await harness.C2.NotifyAsync("DSPCA animal welfare", "Evacuees' pets: please open your shelter.")).Succeeded);
            await EvacuateAsync(harness);
            var owners = Inside(harness, Vulnerability.PetOwner);
            await harness.RunAsync(TimeSpan.FromMinutes(45));
            var returned = (await PayloadsAsync(harness)).OfType<PetOwnersReturned>().Sum(r => r.Count);
            return (owners.Count(p => p.ReturnForPetAt is not null || p.State is CivilianState.Safe or CivilianState.Evacuating) / (double)owners.Count, returned);
        }

        var without = await Run(arrangePets: false);
        var with = await Run(arrangePets: true);
        Assert.True(with.Left > without.Left + 0.2, $"{with.Left:P0} left with a pet shelter, {without.Left:P0} without");
        Assert.True(without.Returned > 0, "some went back in for their animals");
        Assert.Equal(0, with.Returned);
    }

    [Fact]
    public async Task The_council_register_reports_most_of_the_vulnerable_and_none_of_the_rough_sleepers()
    {
        var harness = await PopulatedAsync(Only(elderly: 0.2, mobility: 0.1, homeless: 0.1), new PublicOrderSystem());
        var zone = await EvacuateAsync(harness);
        await harness.RunAsync(PublicOrderSystem.RegisterDelay + TimeSpan.FromSeconds(10));

        var register = Assert.Single(harness.Cop.Public.Vulnerable);
        Assert.Equal(zone, register.ZoneId);
        Assert.Contains("register", register.Source);
        var truly = Inside(harness, Vulnerability.MobilityImpaired).Count;
        Assert.InRange(register.MobilityImpaired, 1, truly);             // most, not all
        Assert.Equal(0, register.Homeless);                               // on no register
        // On the desk of whoever runs assisted evacuation.
        Assert.Contains(RoleDesk.For(UserRole.Operations, harness.Cop, r => r != UserRole.Logistics), d => d.Function == "Evacuation support");
    }

    // ---- Public order ----

    [Fact]
    public async Task Empty_homes_with_nobody_guarding_them_are_looted_and_a_garda_cordon_stops_it()
    {
        async Task<List<LootingOccurred>> Run(bool cordon)
        {
            var harness = await PopulatedAsync(Only(compliance: 1.0), new PublicOrderSystem());
            await EvacuateAsync(harness);
            if (cordon) Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.PoliceCordon, "Garda cordon", Wgs84.Circle(Dublin, 300))).Succeeded);
            await harness.RunAsync(TimeSpan.FromHours(3));
            return [.. (await PayloadsAsync(harness)).OfType<LootingOccurred>()];
        }

        var open = await Run(cordon: false);
        Assert.Contains(open, l => !l.Policed);
        Assert.InRange(open.Count, 1, 3);
        Assert.All(await Run(cordon: true), l => Assert.True(l.Policed, "with a cordon, gardaí stop any attempt"));
    }

    [Fact]
    public async Task Volunteers_left_alone_go_past_the_cordon_and_registered_they_do_not()
    {
        async Task<TestHarness> Incident(bool register)
        {
            var harness = new TestHarness(new PublicOrderSystem(), new HazardSystem());
            var incident = (await harness.C2.CreateIncidentAsync(IncidentType.StructureFire, IncidentPriority.Critical, Dublin)).EntityId!.Value;
            await harness.Truth(new WorldIncidentStarted(Guid.NewGuid(), IncidentType.StructureFire, Dublin, 0.6, 0)); // a real fire near them
            await harness.RunAsync(PublicOrderSystem.VolunteersAfter + TimeSpan.FromMinutes(1));
            Assert.Single(harness.Cop.Public.Volunteers);
            Assert.Contains(RoleDesk.For(UserRole.Logistics, harness.Cop, _ => true), d => d.Function == "Volunteers");
            if (register) Assert.True((await harness.C2.ManageVolunteersAsync(incident, VolunteerDecision.Register)).Succeeded);
            await harness.RunAsync(PublicOrderSystem.SelfDeployAfter + TimeSpan.FromMinutes(15));
            return harness;
        }

        var unmanaged = await Incident(register: false);
        Assert.True(unmanaged.Cop.Public.Volunteers.Single().SelfDeployed > 0);
        Assert.True(unmanaged.Cop.Public.Volunteers.Single().Count > 15, "more arrive the longer nobody takes charge");

        var managed = await Incident(register: true);
        Assert.Equal(0, managed.Cop.Public.Volunteers.Single().SelfDeployed);
        Assert.False((await managed.C2.ManageVolunteersAsync(managed.Cop.Incidents.Single().Id, VolunteerDecision.TurnAway)).Succeeded);
    }

    // ---- Families ----

    [Fact]
    public async Task Relatives_ring_and_nothing_is_traced_until_a_family_assistance_centre_opens()
    {
        var harness = await PopulatedAsync(Only(elderly: 0.3, compliance: 0.6), new ReunificationSystem());
        await harness.C2.CreateIncidentAsync(IncidentType.Other, IncidentPriority.High, Dublin);
        await EvacuateAsync(harness);
        await harness.RunAsync(TimeSpan.FromMinutes(30));

        var enquiries = harness.Cop.Public.MissingPersons;
        Assert.True(enquiries.Count >= 3);
        Assert.All(enquiries, e => Assert.True(e.Waiting));
        Assert.Contains(RoleDesk.For(UserRole.Liaison, harness.Cop, _ => true), d => d.Function == "Family assistance");

        Assert.True((await harness.C2.OpenFamilyAssistanceCentreAsync("Ringsend Community Centre", GeoMath.Destination(Dublin, 270, 1000))).Succeeded);
        Assert.False((await harness.C2.OpenFamilyAssistanceCentreAsync("A second one", Dublin)).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(25));

        enquiries = harness.Cop.Public.MissingPersons;
        Assert.True(enquiries.Count(e => !e.Waiting) >= 3);
        // People who would not leave are traced to where they really are: still at home, inside the zone.
        foreach (var traced in enquiries.Where(e => !e.Waiting))
            Assert.Contains(traced.Outcome!.Value, new[] { TraceOutcome.Safe, TraceOutcome.StillInArea });
        if (enquiries.Any(e => e.Outcome == TraceOutcome.StillInArea))
            Assert.Contains(harness.Cop.Alerts, a => a.Title == "Person believed still in the zone");
    }

    // ---- Rumours and the press ----

    private static TestHarness RumourHarness() => new(new RumourSystem(), new MediaCoverageSystem());

    [Fact]
    public async Task A_rumour_spreads_until_answered_and_a_false_one_rebutted_wins_trust_back()
    {
        var harness = RumourHarness();
        await harness.C2.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.High, Dublin, "Barrow Street");
        await harness.RunAsync(RumourSystem.FirstRumourAfter + TimeSpan.FromMinutes(1));

        var rumour = Assert.Single(harness.Cop.Public.Rumours);
        Assert.True(rumour.Spreading);
        var truth = (await harness.EventsAsync()).Single(e => e.Payload is RumourTruth);
        Assert.Equal(EventVisibility.Truth, truth.Visibility); // command never sees whether it is true
        Assert.False(((RumourTruth)truth.Payload).IsTrue);     // one incident: no "second explosion"

        var reach = rumour.ReachThousands;
        await harness.RunAsync(RumourSystem.SpreadInterval * 2);
        Assert.True(harness.Cop.Public.Rumours.Single().ReachThousands > reach);
        Assert.Contains(RoleDesk.For(UserRole.PublicInformation, harness.Cop, _ => true), d => d.EntityId == rumour.Id);

        Assert.False((await harness.C2.RebutRumourAsync(rumour.Id, " ")).Succeeded);
        Assert.True((await harness.C2.RebutRumourAsync(rumour.Id, "There has been one explosion at Barrow Street. There is no second explosion.")).Succeeded);
        var stopped = harness.Cop.Public.Rumours.Single().ReachThousands;
        await harness.RunAsync(RumourSystem.SpreadInterval * 2);
        Assert.Equal(stopped, harness.Cop.Public.Rumours.Single(r => r.Id == rumour.Id).ReachThousands);
        Assert.Contains((await PayloadsAsync(harness)).OfType<PublicConfidenceChanged>(), c => c.Change > 0 && c.Reason.Contains("record straight"));
    }

    [Fact]
    public async Task Denying_a_rumour_that_is_true_comes_out_and_costs_far_more()
    {
        var harness = RumourHarness();
        await harness.C2.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.High, Dublin);
        var inject = Simulation.Scenarios.InjectTemplates.Create("rumour",
            new Dictionary<string, string> { ["text"] = "Gas leak spreading, they're not telling us", ["topic"] = "gas", ["true"] = "Yes" })!;
        await harness.Engine.ApplyAsync(context =>
        {
            foreach (var (visibility, payload) in inject.Produce(context)) context.Emit(visibility, payload, EventSources.Instructor);
            return true;
        });
        var rumour = harness.Cop.Public.Rumours.Single(r => r.Topic == "gas");

        Assert.True((await harness.C2.RebutRumourAsync(rumour.Id, "There is no gas leak.")).Succeeded);
        Assert.True((await harness.C2.RecordDecisionAsync("Denied the gas rumour", "No reports of gas from crews on scene")).Succeeded);
        await harness.RunAsync(RumourSystem.DenialExposedAfter + TimeSpan.FromMinutes(1));

        Assert.NotNull(harness.Cop.Public.Rumours.Single(r => r.Id == rumour.Id).DenialExposedAt);
        Assert.Contains(harness.Cop.Alerts, a => a.Title == "Denial contradicted");
        Assert.Contains((await PayloadsAsync(harness)).OfType<PublicConfidenceChanged>(), c => c.Change == -15);

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Misinformation");
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.Contains("Denied a rumour that was true (gas)"));
        Assert.Equal((1, 1), (report.Documentation.Significant, report.Documentation.Documented)); // the denial was logged, with reasons
    }

    [Fact]
    public async Task A_press_conference_answers_the_journalists_and_deals_with_the_rumours_it_speaks_to()
    {
        var harness = new TestHarness(new RumourSystem(), new MediaCoverageSystem(), new InteragencySystem());
        var incident = (await harness.C2.CreateIncidentAsync(IncidentType.Explosion, IncidentPriority.Critical, Dublin, "Barrow Street")).EntityId!.Value;
        await harness.RunAsync(RumourSystem.FirstRumourAfter + TimeSpan.FromMinutes(1));
        Assert.NotEmpty(harness.Cop.MediaEnquiries.Where(m => m.AnsweredAt is null));
        var rumour = harness.Cop.Public.Rumours.Single();

        var held = await harness.C2.HoldPressConferenceAsync(
            $"There has been one explosion at Barrow Street, and no {rumour.Topic} beyond it. Residents nearby should stay indoors.", incident);
        Assert.True(held.Succeeded, held.Error);
        Assert.Contains("dealt with 1 rumour", held.Detail);
        Assert.All(harness.Cop.MediaEnquiries, m => Assert.NotNull(m.AnsweredAt));
        Assert.False(harness.Cop.Public.Rumours.Single().Spreading);
        Assert.False((await harness.C2.HoldPressConferenceAsync("Again", incident)).Succeeded); // not straight away

        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Contains((await PayloadsAsync(harness)).OfType<PublicConfidenceChanged>(), c => c.Change == 10);
        // A press conference that speaks to a rumour is not a denial: nothing comes out later.
        await harness.RunAsync(RumourSystem.DenialExposedAfter + TimeSpan.FromMinutes(1));
        Assert.DoesNotContain((await PayloadsAsync(harness)).OfType<DenialExposed>(), _ => true);
    }

    // ---- Community leaders and agency heads ----

    [Fact]
    public async Task A_community_leader_rings_about_their_area_and_engaged_they_get_people_to_listen()
    {
        var harness = await PopulatedAsync(Only(elderly: 0.3, homeless: 0.05, pets: 0.3), new PoliticalPressureSystem(), new PublicOrderSystem());
        await harness.C2.CreateIncidentAsync(IncidentType.Other, IncidentPriority.Medium, Dublin);
        var zone = await EvacuateAsync(harness);
        await harness.RunAsync(PoliticalPressureSystem.CommunityCallAfter + TimeSpan.FromSeconds(10));

        var call = Assert.Single(harness.Cop.PoliticalCalls, c => c.Office == PoliticalOffice.CommunityLeader);
        Assert.True((await harness.C2.AnswerPoliticalCallAsync(call.Id, "Please come to the command post: we need your help reaching people.")).Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(6));

        Assert.Contains(zone, harness.World.Public.EngagedZones);
        var leader = Assert.Single(harness.Cop.Public.Vulnerable, v => v.Source == "Community leader");
        Assert.Equal(Inside(harness, Vulnerability.Homeless).Count, leader.Homeless); // they know who sleeps rough
        Assert.True(leader.PetOwners > 0);
    }

    [Fact]
    public async Task An_evacuation_ordered_without_the_garda_brings_a_call_and_a_legal_exposure()
    {
        async Task<TestHarness> Run(bool consult)
        {
            var harness = new TestHarness(new PoliticalPressureSystem());
            await harness.C2.CreateIncidentAsync(IncidentType.HazmatRelease, IncidentPriority.High, Dublin);
            if (consult) Assert.True((await harness.C2.NotifyAsync("Garda Síochána, Pearse Street", "We intend to evacuate 250 m around the release.")).Succeeded);
            await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Zone);
            await harness.RunAsync(PoliticalPressureSystem.GardaConsultationWindow + TimeSpan.FromSeconds(10));
            return harness;
        }

        var unilateral = await Run(consult: false);
        var call = Assert.Single(unilateral.Cop.PoliticalCalls, c => c.Office == PoliticalOffice.AgencyHead);
        Assert.Equal(PoliticalPressureSystem.GardaChief, call.Caller);
        Assert.Contains((await PayloadsAsync(unilateral)).OfType<LegalExposure>(), l => l.Kind == "Evacuation authority");
        // Consulting them as soon as they ring mitigates it.
        Assert.True((await unilateral.C2.AnswerPoliticalCallAsync(call.Id, "Apologies: we're bringing your inspector into unified command now.")).Succeeded);
        var report = await unilateral.Aar.BuildReportAsync(unilateral.Engine.SessionId, "Test");
        Assert.Contains(report.Liabilities, l => l.Kind == "Evacuation authority" && l.Mitigated);

        var consulted = await Run(consult: true);
        Assert.DoesNotContain(consulted.Cop.PoliticalCalls, c => c.Office == PoliticalOffice.AgencyHead);
        Assert.DoesNotContain((await PayloadsAsync(consulted)).OfType<LegalExposure>(), _ => true);
    }

    // ---- The decision log ----

    [Fact]
    public async Task The_review_checks_significant_decisions_were_logged_with_their_reasons()
    {
        var harness = new TestHarness();
        Assert.False((await harness.C2.RecordDecisionAsync("Evacuate", "  ")).Succeeded); // reasons are the point

        await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Zone);
        var logged = await harness.C2.RecordDecisionAsync("Evacuated 250 m around the release",
            "Chlorine reported by crews; wind towards the flats; Garda inspector agreed");
        Assert.True(logged.Succeeded);
        await harness.RunAsync(TimeSpan.FromMinutes(20));
        await harness.C2.DeclareZoneAsync(ZoneType.ShelterInPlace, "Shelter zone", Wgs84.Circle(GeoMath.Destination(Dublin, 90, 800), 300));

        Assert.Equal("Evacuated 250 m around the release", harness.Cop.Public.DecisionLog.Single().Decision);
        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId, "Test");
        Assert.Equal((2, 1, 50), (report.Documentation.Significant, report.Documentation.Documented, report.Documentation.Percent));
        Assert.Contains("shelter-in-place zone declared", Assert.Single(report.Documentation.Undocumented));
        Assert.Contains(report.Findings, f => !f.Strength && f.Text.StartsWith("Decision log: 1 of 2"));
        Assert.DoesNotContain(report.Decisions, d => d.Decision.StartsWith("Decision:")); // the log entry itself isn't judged
    }

    // ---- Workspaces ----

    [Fact]
    public void The_pio_starts_on_the_public_tab_and_the_roles_that_work_it_have_it()
    {
        Assert.Equal(WorkspacePanel.Public, RoleWorkspaces.For(UserRole.PublicInformation).Focus);
        foreach (var role in new[] { UserRole.IncidentCommander, UserRole.Operations, UserRole.Logistics, UserRole.Liaison, UserRole.Observer })
            Assert.Contains(WorkspacePanel.Public, RoleWorkspaces.For(role).Panels);
    }
}
