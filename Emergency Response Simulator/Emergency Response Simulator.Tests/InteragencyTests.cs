using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Phase 10: jurisdictions, unified command, liaison and PIO, EOC levels, friction, mutual aid and disputes.</summary>
public class InteragencyTests
{
    private static readonly GeoPoint Dublin = TestHarness.Dublin;

    private static TestHarness Harness() => new(new InteragencySystem(), new CommandResponseSystem());

    private static async Task<Guid> AgencyAsync(TestHarness harness, string name, AgencyType type, string? priority = null, params string[] terms)
    {
        var id = Guid.NewGuid();
        await harness.Perceived(new AgencyRegistered(id, name, name[..3], type, AiControlled: true, Priority: priority, PriorityTerms: terms));
        return id;
    }

    private static async Task<Guid> UnitAsync(TestHarness harness, string callsign, Guid agency, UnitType type = UnitType.Patrol)
    {
        var id = Guid.NewGuid();
        await harness.Perceived(new UnitRegistered(id, callsign, type, agency, Dublin, "Station", 2, []));
        return id;
    }

    private static async Task<Guid> IncidentAsync(TestHarness harness, IncidentType type = IncidentType.StructureFire,
        IncidentPriority priority = IncidentPriority.High) =>
        (await harness.C2.CreateIncidentAsync(type, priority, Dublin, name: "Warehouse fire")).EntityId!.Value;

    /// <summary>A square about <paramref name="halfSideMeters"/> each side of a centre.</summary>
    private static IReadOnlyList<GeoPoint> Square(GeoPoint centre, double halfSideMeters) =>
        [.. new[] { 45.0, 135, 225, 315 }.Select(b => GeoMath.Destination(centre, b, halfSideMeters * Math.Sqrt(2)))];

    // ---- Unified command ----

    [Fact]
    public async Task Unified_command_needs_a_stake_and_then_lets_command_task_the_members_units()
    {
        var harness = Harness();
        var police = await AgencyAsync(harness, "Garda Roads Policing", AgencyType.Police, "Keep traffic moving", "traffic");
        var regulator = await AgencyAsync(harness, "Health and Safety Authority", AgencyType.Regulator);
        var cordon = await UnitAsync(harness, "Garda RP 41", police);
        var spare = await UnitAsync(harness, "Garda RP 43", police);
        var incident = await IncidentAsync(harness);
        // Their own control room has already sent a car to hold a cordon at the scene.
        await harness.Perceived(new UnitTasked(cordon, Guid.NewGuid(), "Garda Roads Policing", "Outer cordon", GeoMath.Destination(Dublin, 90, 200)));

        Assert.False((await harness.C2.DispatchAsync(spare, incident)).Succeeded);
        Assert.True((await harness.C2.InviteToUnifiedCommandAsync(incident, police)).Succeeded);
        Assert.True((await harness.C2.InviteToUnifiedCommandAsync(incident, regulator)).Succeeded);
        Assert.False((await harness.C2.InviteToUnifiedCommandAsync(incident, police)).Succeeded); // still waiting for an answer

        await harness.RunAsync(InteragencySystem.InvitationDelay + TimeSpan.FromSeconds(10));

        var command = harness.Cop.FindIncident(incident)!.Command;
        var member = Assert.Single(command.Unified);
        Assert.Equal("Garda Roads Policing", member.Agency);
        Assert.Equal("Keep traffic moving", member.Priority);
        Assert.StartsWith("Inspector ", member.Commander);
        var declined = harness.Cop.UnifiedCommandInvitations.Single(i => i.AgencyId == regulator);
        Assert.False(declined.Accepted);
        Assert.Contains("no role", declined.Reason);

        // Shared command, not just coordination: their units are now ours to task at this incident.
        Assert.True((await harness.C2.DispatchAsync(spare, incident)).Succeeded);
    }

    // ---- Resource hoarding ----

    [Fact]
    public async Task Agencies_keep_a_reserve_for_their_own_area_until_the_EOC_is_fully_activated()
    {
        var harness = Harness();
        var police = await AgencyAsync(harness, "Garda Roads Policing", AgencyType.Police);
        var units = new List<Guid>();
        for (var i = 1; i <= 3; i++) units.Add(await UnitAsync(harness, $"Garda RP 4{i}", police));
        var incident = await IncidentAsync(harness);

        Assert.True((await harness.C2.RequestAgencyAssistanceAsync(police, incident, 3, "Traffic control")).Succeeded);
        await harness.RunAsync(InteragencySystem.AssistanceDelay + TimeSpan.FromSeconds(10));
        var first = Assert.Single(harness.Cop.AssistanceRequests);
        Assert.Equal(2, first.Granted); // a third of the fleet kept back
        Assert.Contains("keep 1 back", first.Reason);
        Assert.All(first.UnitIds, u => Assert.True(harness.Cop.FindUnit(u)!.AttachedToCommand));
        Assert.All(first.UnitIds, u => Assert.Equal(incident, harness.Cop.FindUnit(u)!.AssignedIncidentId));

        Assert.True((await harness.C2.RequestAgencyAssistanceAsync(police, incident, 1, "Traffic control")).Succeeded);
        await harness.RunAsync(InteragencySystem.AssistanceDelay + TimeSpan.FromSeconds(10));
        Assert.Equal(0, harness.Cop.AssistanceRequests.Single(r => r.Id != first.Id).Granted);

        Assert.True((await harness.C2.SetEocLevelAsync(EocLevel.Full, "Major fire")).Succeeded);
        await harness.RunAsync(EocLevels.StaffingTime(EocLevel.Full) + TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
        Assert.Equal(EocLevel.Full, harness.Cop.Eoc.Level);

        Assert.True((await harness.C2.RequestAgencyAssistanceAsync(police, incident, 1, "Traffic control")).Succeeded);
        await harness.RunAsync(InteragencySystem.AssistanceDelay + TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Cop.AssistanceRequests.OrderBy(r => r.RequestedAt).Last().Granted);
    }

    // ---- Information silos ----

    [Fact]
    public async Task What_an_agency_knows_reaches_command_only_when_it_is_shared()
    {
        var harness = Harness();
        var council = await AgencyAsync(harness, "Dublin City Council", AgencyType.EmergencyManagement);
        var waterways = await AgencyAsync(harness, "Waterways Ireland", AgencyType.AreaAuthority);
        var incident = await IncidentAsync(harness);
        await harness.Truth(new InformationHeld(Guid.NewGuid(), council, "The rest centre takes 120, not 200.", null));
        await harness.Truth(new InformationHeld(Guid.NewGuid(), waterways, "15 people live on boats in the basin.", Dublin, Urgent: true));

        await harness.RunAsync(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(harness.Cop.Reports, r => r.Source == ReportSource.Agency);

        // Ask the council directly.
        Assert.True((await harness.C2.RequestAgencyBriefingAsync(council)).Succeeded);
        await harness.RunAsync(InteragencySystem.BriefingDelay + TimeSpan.FromSeconds(10));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Dublin City Council" && r.Claim.Contains("120"));
        Assert.DoesNotContain(harness.Cop.Reports, r => r.SourceName == "Waterways Ireland");

        // A liaison officer does the rounds and brings back the rest.
        Assert.True((await harness.C2.AssignIcsPositionAsync(incident, IcsRole.Liaison, "Stn Off. Kelly")).Succeeded);
        await harness.RunAsync(InteragencySystem.LiaisonRelayInterval + TimeSpan.FromSeconds(15));
        Assert.Contains(harness.Cop.Reports, r => r.SourceName == "Waterways Ireland" && r.Claim.Contains("boats"));
        var events = await harness.EventsAsync();
        Assert.Contains(events, e => e.Payload is InformationShared { Via: "liaison officer", Agency: "Waterways Ireland" });
        Assert.Contains(events, e => e.Payload is InformationShared { Via: "briefing", Agency: "Dublin City Council" });
    }

    [Fact]
    public async Task A_hostile_agency_keeps_routine_information_to_itself()
    {
        var harness = Harness();
        var investigators = await AgencyAsync(harness, "Garda National Bureau of Criminal Investigation", AgencyType.Investigation);
        var incident = await IncidentAsync(harness);
        await harness.Perceived(new AgencyRelationChanged(investigators, AgencyStanding.Hostile, "overruled"));
        await harness.Truth(new InformationHeld(Guid.NewGuid(), investigators, "The tenant is under investigation.", null));
        await harness.C2.AssignIcsPositionAsync(incident, IcsRole.Liaison, "Stn Off. Kelly");

        await harness.RunAsync(TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(harness.Cop.Reports, r => r.Source == ReportSource.Agency);
        Assert.Equal(AgencyStanding.Hostile, harness.Cop.Agencies.Single(a => a.Id == investigators).Standing);
    }

    // ---- Jurisdictions and authority disputes ----

    [Fact]
    public async Task A_zone_reaching_into_an_area_authoritys_estate_is_disputed_and_stops_at_its_boundary()
    {
        var harness = Harness();
        var waterways = await AgencyAsync(harness, "Waterways Ireland", AgencyType.AreaAuthority);
        var basinCentre = GeoMath.Destination(Dublin, 0, 300);
        await harness.Perceived(new JurisdictionDeclared("basin", "The basin", JurisdictionLevel.National, "Waterways Ireland", waterways,
            Square(basinCentre, 100), RequiresConsent: true, Powers: "the basin and its moorings"));
        var incident = await IncidentAsync(harness);
        Assert.Contains(harness.Cop.Jurisdictions, j => j.Id == "basin" && j.RequiresConsent);
        Assert.Single(harness.Cop.JurisdictionsAt(basinCentre));

        // An evacuation zone big enough to take in half the basin.
        Assert.True((await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Square(Dublin, 300), incident)).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(10));

        var dispute = Assert.Single(harness.Cop.Disputes);
        Assert.Equal(DisputeKind.Jurisdiction, dispute.Kind);
        Assert.Equal("Waterways Ireland", dispute.Agency);
        var zone = dispute.ZoneId!.Value;
        var inBasin = GeoMath.Destination(Dublin, 0, 250).ToPoint();
        var outside = GeoMath.Destination(Dublin, 180, 150).ToPoint();
        Assert.False(harness.World.Interagency.Enforced(zone, inBasin, harness.Engine.SimTime));
        Assert.True(harness.World.Interagency.Enforced(zone, outside, harness.Engine.SimTime));

        // Escalating needs the EOC; asserting works at once but costs relations.
        Assert.False((await harness.C2.ResolveDisputeAsync(dispute.Id, DisputeResolution.Escalate)).Succeeded);
        Assert.True((await harness.C2.ResolveDisputeAsync(dispute.Id, DisputeResolution.Assert)).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.True(harness.World.Interagency.Enforced(zone, inBasin, harness.Engine.SimTime));
        Assert.False(harness.Cop.Disputes.Single().IsOpen);
        Assert.Equal(AgencyStanding.Strained, harness.Cop.Agencies.Single(a => a.Id == waterways).Standing);

        // Overruling doesn't win agreement: the next zone is disputed too. Negotiating does.
        await harness.C2.DeclareZoneAsync(ZoneType.ShelterInPlace, "Shelter zone", Square(Dublin, 320), incident);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        var second = harness.Cop.Disputes.Single(d => d.IsOpen);
        Assert.True((await harness.C2.ResolveDisputeAsync(second.Id, DisputeResolution.Negotiate)).Succeeded);
        Assert.False((await harness.C2.ResolveDisputeAsync(second.Id, DisputeResolution.Assert)).Succeeded); // talks under way
        await harness.RunAsync(InteragencySystem.NegotiationTime * 1.5 + TimeSpan.FromSeconds(10));
        Assert.Equal(DisputeResolution.Negotiate, harness.Cop.Disputes.Single(d => d.Id == second.Id).Resolution);
        Assert.False(harness.Cop.Disputes.Single(d => d.Id == second.Id).IsOpen);

        await harness.C2.DeclareZoneAsync(ZoneType.PoliceCordon, "Cordon", Square(Dublin, 350), incident);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, harness.Cop.Disputes.Count);
    }

    [Fact]
    public async Task An_area_authority_in_unified_command_raises_no_dispute()
    {
        var harness = Harness();
        var waterways = await AgencyAsync(harness, "Waterways Ireland", AgencyType.AreaAuthority);
        await harness.Perceived(new JurisdictionDeclared("basin", "The basin", JurisdictionLevel.National, "Waterways Ireland", waterways,
            Square(GeoMath.Destination(Dublin, 0, 300), 100), RequiresConsent: true));
        var incident = await IncidentAsync(harness);
        await harness.C2.InviteToUnifiedCommandAsync(incident, waterways); // the basin is within 400 m: a stake
        await harness.RunAsync(InteragencySystem.InvitationDelay + TimeSpan.FromSeconds(10));
        Assert.True(harness.Cop.FindIncident(incident)!.Command.InUnifiedCommand(waterways));

        await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Square(Dublin, 300), incident);
        await harness.RunAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(harness.Cop.Disputes);
    }

    [Fact]
    public async Task Conceding_the_lead_while_the_fire_burns_sends_operations_defensive()
    {
        var harness = Harness();
        var investigators = await AgencyAsync(harness, "Garda National Bureau of Criminal Investigation", AgencyType.Investigation);
        var incident = await IncidentAsync(harness, IncidentType.Explosion);
        var disputeId = Guid.NewGuid();
        await harness.Perceived(new DisputeRaised(disputeId, incident, DisputeKind.LeadAgency, investigators,
            "Garda National Bureau of Criminal Investigation", "Who leads", "Garda to lead: crime scene", "Investigation of crime"));

        Assert.True((await harness.C2.ResolveDisputeAsync(disputeId, DisputeResolution.Concede)).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Garda National Bureau of Criminal Investigation", harness.Cop.FindIncident(incident)!.Command.LeadAgency);
        Assert.Contains(incident, harness.World.DefensiveIncidents);
        Assert.Contains(harness.Cop.Disputes, d => d.Id == disputeId && d.Outcome!.Contains("defensive"));
    }

    [Fact]
    public async Task A_negotiated_lead_passes_to_the_investigators_once_the_fire_is_out()
    {
        var harness = Harness();
        var investigators = await AgencyAsync(harness, "Garda National Bureau of Criminal Investigation", AgencyType.Investigation);
        var incident = await IncidentAsync(harness, IncidentType.Explosion);
        var disputeId = Guid.NewGuid();
        await harness.Perceived(new DisputeRaised(disputeId, incident, DisputeKind.LeadAgency, investigators,
            "Garda National Bureau of Criminal Investigation", "Who leads", "Garda to lead", "Investigation of crime"));
        // With the EOC up, the dispute can be escalated instead; the EOC decides on a staged handover.
        await harness.C2.SetEocLevelAsync(EocLevel.Partial);
        await harness.RunAsync(EocLevels.StaffingTime(EocLevel.Partial) + TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
        Assert.True((await harness.C2.ResolveDisputeAsync(disputeId, DisputeResolution.Escalate)).Succeeded);
        await harness.RunAsync(InteragencySystem.EscalationTime + TimeSpan.FromSeconds(30));

        var settled = harness.Cop.Disputes.Single();
        Assert.StartsWith("EOC decision", settled.Outcome);
        // No fire in this world, so the agreed handover happens straight away.
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Garda National Bureau of Criminal Investigation", harness.Cop.FindIncident(incident)!.Command.LeadAgency);
        Assert.DoesNotContain(incident, harness.World.DefensiveIncidents);
    }

    // ---- EOC ----

    [Fact]
    public async Task The_EOC_staffs_up_over_time_and_wants_regular_situation_reports()
    {
        var harness = Harness();
        Assert.False((await harness.C2.SubmitSitrepAsync("All quiet")).Succeeded); // nobody to send it to
        Assert.True((await harness.C2.SetEocLevelAsync(EocLevel.Partial, "Chemical fire")).Succeeded);
        Assert.False((await harness.C2.SetEocLevelAsync(EocLevel.Partial)).Succeeded);

        await harness.RunAsync(TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(30));
        var eoc = harness.Cop.Eoc;
        Assert.Equal(EocLevel.Normal, eoc.Level);
        Assert.Equal(EocLevel.Partial, eoc.Requested);
        Assert.InRange(eoc.Seats.Count, 1, EocLevels.SeatsAt(EocLevel.Partial).Count - 1);

        await harness.RunAsync(TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(30));
        eoc = harness.Cop.Eoc;
        Assert.Equal(EocLevel.Partial, eoc.Level);
        Assert.Equal(EocLevels.SeatsAt(EocLevel.Partial).Count, eoc.Seats.Count);
        Assert.NotNull(eoc.SitrepDue);

        await harness.RunAsync(TimeSpan.FromMinutes(31), TimeSpan.FromSeconds(30));
        Assert.Contains(harness.Cop.Alerts, a => a.Title.Contains("situation report overdue"));

        var due = harness.Cop.Eoc.SitrepDue;
        Assert.True((await harness.C2.SubmitSitrepAsync("Fire contained; 3 casualties; shelter in place continues.")).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(30));
        Assert.True(harness.Cop.Eoc.SitrepDue > due);
        Assert.Equal(1, harness.Cop.Eoc.SitrepsSubmitted);

        // Standing down is immediate.
        await harness.C2.SetEocLevelAsync(EocLevel.Monitoring);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EocLevel.Monitoring, harness.Cop.Eoc.Level);
        Assert.Equal(EocLevels.SeatsAt(EocLevel.Monitoring).Count, harness.Cop.Eoc.Seats.Count);
    }

    // ---- Mutual aid ----

    [Fact]
    public async Task Mutual_aid_follows_the_agreements_cap_terms_and_major_emergency_condition()
    {
        var harness = Harness();
        await harness.Perceived(new MutualAidAgreementRegistered(Guid.NewGuid(), "Kildare Fire Service", AgencyType.Fire, "County Kildare",
            [UnitType.Engine], 3, "Kildare Chief Fire Officer", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), false,
            "Firefighting only.", ["hazmat", "decontamination"], "KILDARE FIRE"));
        await harness.Perceived(new MutualAidAgreementRegistered(Guid.NewGuid(), "National Ambulance Service (Midlands)", AgencyType.Ems, null,
            [UnitType.AmbulanceAls], 4, "NAS Control", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), true, "Major emergencies only.", []));
        var incident = await IncidentAsync(harness);

        await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.MutualAid, UnitType.AmbulanceAls, 2, "Many casualties");
        await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.MutualAid, UnitType.Engine, 5, "Fire spreading");
        await harness.C2.RequestResourcesAsync(incident, ResourceRequestKind.MutualAid, UnitType.Hazmat, 1, "Chlorine store");
        await harness.RunAsync(TimeSpan.FromMinutes(5));

        var requests = harness.Cop.ResourceRequests.ToDictionary(r => r.UnitType!.Value);
        Assert.Equal(ResourceRequestStatus.Denied, requests[UnitType.AmbulanceAls].Status);
        Assert.Contains("major emergency", requests[UnitType.AmbulanceAls].DecisionReason);
        Assert.Equal(ResourceRequestStatus.Approved, requests[UnitType.Engine].Status);
        Assert.Equal("Kildare Chief Fire Officer", requests[UnitType.Engine].DecidedBy);
        Assert.InRange(requests[UnitType.Engine].Granted!.Value, 2, 3); // capped at 3; perhaps one kept back for cover
        Assert.Equal(ResourceRequestStatus.Denied, requests[UnitType.Hazmat].Status); // no agreement, and not Critical
        Assert.Contains("no mutual-aid agreement", requests[UnitType.Hazmat].DecisionReason);

        await harness.RunAsync(TimeSpan.FromMinutes(10));
        var kildare = harness.Cop.Units.Where(u => u.Agency?.Name == "Kildare Fire Service").ToList();
        Assert.Equal(requests[UnitType.Engine].Granted, kildare.Count);
        Assert.Equal("County Kildare", kildare[0].Agency!.Jurisdiction);

        // They bring their own radios: patch them through, then order one of their crews outside the agreement.
        var patch = Guid.NewGuid();
        await harness.Perceived(new ChannelPatchRequested(patch, "KILDARE FIRE", RadioPlan.FireCommand));
        await harness.Perceived(new ChannelsPatched(patch));
        Assert.True((await harness.C2.IssueOrderAsync(incident, OrderTargetKind.Unit, kildare[0].Id, "Begin hazmat decontamination of casualties")).Succeeded);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        var dispute = Assert.Single(harness.Cop.Disputes);
        Assert.Equal(DisputeKind.MutualAidTerms, dispute.Kind);
        Assert.Equal(OrderStatus.Declined, harness.Cop.Orders.Single().Status);

        await harness.C2.ResolveDisputeAsync(dispute.Id, DisputeResolution.Concede);
        await harness.RunAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OrderStatus.Cancelled, harness.Cop.Orders.Single().Status);
        Assert.False(harness.Cop.Disputes.Single().IsOpen);
    }

    // ---- Public information ----

    [Fact]
    public async Task Without_a_PIO_the_media_go_unanswered_speculation_spreads_and_another_agency_contradicts_the_shelter_order()
    {
        var harness = Harness();
        await AgencyAsync(harness, "Dublin City Council", AgencyType.EmergencyManagement);
        var incident = await IncidentAsync(harness);
        await harness.C2.DeclareZoneAsync(ZoneType.ShelterInPlace, "Shelter zone", Square(Dublin, 300), incident);

        await harness.RunAsync(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(10));

        Assert.NotEmpty(harness.Cop.MediaEnquiries);
        Assert.All(harness.Cop.MediaEnquiries, m => Assert.Null(m.AnsweredAt));
        Assert.Contains(harness.Cop.Reports, r => r.Source == ReportSource.Media && r.Claim.Contains("No word from the authorities"));
        Assert.Contains(harness.Cop.Reports, r => r.Source == ReportSource.EmergencyCall && r.Claim.Contains("toxic gas"));
        var contradiction = Assert.Single(harness.Cop.PublicStatements, s => s.Conflicting);
        Assert.Equal("Dublin City Council", contradiction.Issuer);
        Assert.NotNull(harness.World.Interagency.ConflictingSince);
    }

    [Fact]
    public async Task A_PIO_with_a_joint_information_centre_answers_the_media_and_keeps_every_agency_on_one_message()
    {
        var harness = Harness();
        var council = await AgencyAsync(harness, "Dublin City Council", AgencyType.EmergencyManagement);
        var incident = await IncidentAsync(harness);
        await harness.C2.AssignIcsPositionAsync(incident, IcsRole.PublicInformation, "Ms. Daly");
        await harness.C2.InviteToUnifiedCommandAsync(incident, council); // High priority: the council has a stake
        await harness.RunAsync(InteragencySystem.InvitationDelay + TimeSpan.FromSeconds(10));
        Assert.True(harness.Cop.JointInformationCentre(incident));
        await harness.C2.DeclareZoneAsync(ZoneType.ShelterInPlace, "Shelter zone", Square(Dublin, 300), incident);

        await harness.RunAsync(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(10));

        Assert.NotEmpty(harness.Cop.MediaEnquiries);
        var answerable = harness.Cop.MediaEnquiries.Where(m => m.ReceivedAt < harness.Engine.SimTime - InteragencySystem.PioAnswerDelay - TimeSpan.FromSeconds(10)).ToList();
        Assert.NotEmpty(answerable);
        Assert.All(answerable, m => Assert.Equal("PIO Ms. Daly", m.AnsweredBy));
        var statement = harness.Cop.PublicStatements.First(s => s.Ours);
        Assert.True(statement.Joint);
        Assert.Contains("stay indoors", statement.Text); // the PIO passes on the protective advice
        Assert.DoesNotContain(harness.Cop.PublicStatements, s => s.Conflicting);
        Assert.NotNull(harness.World.Interagency.LastWarningAt);
    }

    [Fact]
    public async Task A_statement_without_a_joint_information_centre_goes_out_as_ours_alone()
    {
        var harness = Harness();
        var incident = await IncidentAsync(harness);
        Assert.False((await harness.C2.IssuePublicStatementAsync("  ")).Succeeded);
        Assert.True((await harness.C2.IssuePublicStatementAsync("Residents near the warehouse should stay indoors and keep windows closed.", incident)).Succeeded);

        var statement = Assert.Single(harness.Cop.PublicStatements);
        Assert.Equal("Incident Commander", statement.Issuer);
        Assert.False(statement.Joint);
        Assert.NotNull(harness.World.Interagency.LastWarningAt);
    }

    // ---- After-action review ----

    [Fact]
    public async Task The_AAR_scores_the_multi_agency_side_of_a_session()
    {
        var harness = Harness();
        var waterways = await AgencyAsync(harness, "Waterways Ireland", AgencyType.AreaAuthority);
        await harness.Perceived(new JurisdictionDeclared("basin", "The basin", JurisdictionLevel.National, "Waterways Ireland", waterways,
            Square(GeoMath.Destination(Dublin, 0, 300), 100), RequiresConsent: true));
        var incident = await IncidentAsync(harness);
        await harness.Truth(new InformationHeld(Guid.NewGuid(), waterways, "15 people live on boats in the basin.", Dublin, Urgent: true));

        // No liaison officer, no unified command: the evacuation zone is disputed and overruled.
        await harness.RunAsync(TimeSpan.FromMinutes(2));
        await harness.C2.DeclareZoneAsync(ZoneType.EvacuationZone, "Evacuation zone", Square(Dublin, 300), incident);
        await harness.RunAsync(TimeSpan.FromMinutes(1));
        await harness.C2.ResolveDisputeAsync(harness.Cop.Disputes.Single().Id, DisputeResolution.Assert);
        await harness.RunAsync(TimeSpan.FromMinutes(22), TimeSpan.FromSeconds(10));

        var report = await harness.Aar.BuildReportAsync(harness.Engine.SessionId);
        var metrics = report.Metrics.Where(m => m.Category == "Multi-agency").ToDictionary(m => m.Name);

        Assert.Equal(Rating.Poor, metrics["Liaison officer appointed"].Rating);
        Assert.Equal(Rating.Poor, metrics["Unified command"].Rating);
        Assert.Equal(Rating.Fair, metrics["Authority disputes"].Rating);
        Assert.Equal(Rating.Poor, metrics["Other agencies' information reaching command"].Rating); // volunteered 20 min late
        Assert.Equal(Rating.Poor, metrics["EOC activation"].Rating); // an evacuation zone made it a major emergency
        var overruled = Assert.Single(report.Decisions, d => d.Category == "Multi-agency");
        Assert.Equal(Rating.Fair, overruled.Rating);
        Assert.Contains("Overruled", overruled.Note);
        Assert.Contains(report.Branches, b => b.Topic == "Information held by other agencies" && b.Rating == Rating.Poor);
    }

    // ---- Shared command of the plan ----

    [Fact]
    public void Unified_command_members_will_not_sign_a_plan_that_leaves_out_their_priority()
    {
        var police = new Agency { Id = Guid.NewGuid(), Name = "Garda Roads Policing", ShortName = "RP", PriorityTerms = ["traffic", "diversion"] };
        var council = new Agency { Id = Guid.NewGuid(), Name = "Dublin City Council", ShortName = "DCC", PriorityTerms = ["rest centre", "welfare"] };
        var command = new IncidentCommand();
        command.Apply(new UnifiedCommandJoined(Guid.NewGuid(), police.Id, police.Name, "Inspector Byrne", "Keep traffic moving"));
        command.Apply(new UnifiedCommandJoined(Guid.NewGuid(), council.Id, council.Name, "Senior Executive Officer Nolan", "Look after residents"));
        var content = new IapContent
        {
            Objectives = [new StrategicObjective { Statement = "Protect life", Operational = [new OperationalObjective { Statement = "Open a rest centre for evacuees" }] }],
        };

        var missing = Interagency.UncoveredPriorities(content, command, [police, council]);

        Assert.Equal("Garda Roads Policing", Assert.Single(missing).Agency);
        content.Objectives[0].Operational.Add(new OperationalObjective { Statement = "Set up traffic diversions around the cordon" });
        Assert.Empty(Interagency.UncoveredPriorities(content, command, [police, council]));
    }
}
