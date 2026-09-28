using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Which transitions are worth telling a person about, pinned so the answer has to be given
/// deliberately rather than inherited.
/// </summary>
/// <remarks>
/// <para>
/// Adding a member to <see cref="IncidentState"/> should make somebody decide whether it wakes
/// anybody up. Without this the default answer is "no" and it is reached by nobody thinking about
/// it - which is the same shape as the failure the whole milestone is fixing.
/// </para>
/// <para>
/// Over edges since v0.10.0 (#133): the incident OPENING is an event, and it is the edge out of
/// Triaging - into an investigation or straight to a person. Until then the first message was the
/// investigation's end, as late as ten minutes of a model's wall clock after the fault.
/// </para>
/// </remarks>
public sealed class NotificationTaxonomyTests
{
    [Theory]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Escalated)]
    [InlineData(IncidentState.AwaitingApproval)]
    public void The_edge_out_of_triage_is_the_incident_opening(IncidentState to)
    {
        NotificationEnqueue.Classify(IncidentState.Triaging, to, EscalationReason.None)
            .Should().Be(NotificationEvent.IncidentOpened);
    }

    [Theory]
    [InlineData(EscalationReason.SelfSignal)]
    [InlineData(EscalationReason.NotInvestigated)]
    [InlineData(EscalationReason.Flapping)]
    public void Escalating_straight_out_of_triage_is_still_the_opening(EscalationReason reason)
    {
        // A person hears it opened; there is no investigation to end later.
        NotificationEnqueue.Classify(IncidentState.Triaging, IncidentState.Escalated, reason)
            .Should().Be(NotificationEvent.IncidentOpened);
    }

    [Theory]
    [InlineData(IncidentState.Escalated)]
    [InlineData(IncidentState.Closed)]
    [InlineData(IncidentState.Expired)]
    public void A_human_asking_for_another_look_opens_nothing(IncidentState from)
    {
        // Reinvestigate: they already know, and they asked.
        NotificationEnqueue.Classify(from, IncidentState.Investigating, EscalationReason.None).Should().BeNull();
    }

    [Theory]
    [InlineData(null, IncidentState.Detected)]
    [InlineData(IncidentState.Detected, IncidentState.Triaging)]
    [InlineData(IncidentState.Closed, IncidentState.Triaging)]
    [InlineData(IncidentState.AwaitingApproval, IncidentState.Acting)]
    [InlineData(IncidentState.Acting, IncidentState.Verifying)]
    public void The_agent_working_is_not_an_event(IncidentState? from, IncidentState to)
    {
        // A channel that reported every step is one people mute, and the escalation gets muted
        // along with it. A reopen is told by the edge out of Triaging that follows it.
        NotificationEnqueue.Classify(from, to, EscalationReason.None).Should().BeNull();
    }

    [Theory]
    [InlineData(IncidentState.Suppressed)]
    [InlineData(IncidentState.Expired)]
    [InlineData(IncidentState.Closed)]
    public void An_incident_that_ended_without_anybody_needing_to_act_is_not_an_event(IncidentState to)
    {
        // Closed is a person's decision or the alert clearing; the Teams card is edited to say
        // which, and an edit rings nobody.
        NotificationEnqueue.Classify(IncidentState.Escalated, to, EscalationReason.None).Should().BeNull();
    }

    [Theory]
    [InlineData(EscalationReason.VerificationFailed)]
    [InlineData(EscalationReason.RollbackPerformed)]
    [InlineData(EscalationReason.Quarantined)]
    public void The_three_give_up_reasons_say_the_agent_tried_and_was_wrong(EscalationReason reason)
    {
        NotificationEnqueue.Classify(IncidentState.Verifying, IncidentState.Escalated, reason)
            .Should().Be(NotificationEvent.VerificationFailed);
    }

    [Theory]
    [InlineData(EscalationReason.NoPlanProduced)]
    [InlineData(EscalationReason.PolicyDenied)]
    [InlineData(EscalationReason.LowConfidence)]
    [InlineData(EscalationReason.StormCircuitBreaker)]
    public void An_investigation_ending_in_a_person_is_a_plain_escalation(EscalationReason reason)
    {
        NotificationEnqueue.Classify(IncidentState.Investigating, IncidentState.Escalated, reason)
            .Should().Be(NotificationEvent.IncidentEscalated);
    }

    [Fact]
    public void Every_escalation_reason_produces_a_message_of_some_kind()
    {
        // The universal give-up edge is always available, so no reason may fall through to
        // silence - from anywhere it can be taken.
        foreach (var reason in Enum.GetValues<EscalationReason>())
        {
            foreach (var from in new[] { IncidentState.Triaging, IncidentState.Investigating, IncidentState.AwaitingApproval })
            {
                NotificationEnqueue.Classify(from, IncidentState.Escalated, reason)
                    .Should().NotBeNull($"escalating from {from} for {reason} must reach somebody");
            }
        }
    }

    [Theory]
    [InlineData(NotificationEvent.IncidentOpened, true)]
    [InlineData(NotificationEvent.ApprovalRequired, true)]
    [InlineData(NotificationEvent.VerificationFailed, true)]
    [InlineData(NotificationEvent.IncidentEscalated, false)]
    [InlineData(NotificationEvent.IncidentResolved, false)]
    public void An_update_to_what_a_person_was_told_is_an_edit_not_a_ring(NotificationEvent evt, bool rings)
    {
        TeamsBotNotificationChannel.Rings(evt).Should().Be(rings);
    }
}
