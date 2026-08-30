using Hephaisto.Agent.Notifications;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Which incident states are worth telling a person about, pinned so the answer has to be given
/// deliberately rather than inherited.
/// </summary>
/// <remarks>
/// Adding a member to <see cref="IncidentState"/> should make somebody decide whether it wakes
/// anybody up. Without this the default answer is "no" and it is reached by nobody thinking about
/// it - which is the same shape as the failure the whole milestone is fixing.
/// </remarks>
public sealed class NotificationTaxonomyTests
{
    private static readonly IncidentState[] Notifiable =
    [
        IncidentState.AwaitingApproval,
        IncidentState.Resolved,
        IncidentState.Escalated,
    ];

    [Fact]
    public void Exactly_three_states_are_worth_a_message()
    {
        var notifiable = Enum.GetValues<IncidentState>()
            .Where(s => NotificationEnqueue.Classify(s, EscalationReason.None) is not null)
            .ToArray();

        notifiable.Should().BeEquivalentTo(Notifiable);
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Triaging)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Acting)]
    [InlineData(IncidentState.Verifying)]
    public void The_agent_working_is_not_an_event(IncidentState state)
    {
        // A channel that reported every step is one people mute, and the escalation gets muted
        // along with it.
        NotificationEnqueue.Classify(state, EscalationReason.None).Should().BeNull();
    }

    [Theory]
    [InlineData(IncidentState.Suppressed)]
    [InlineData(IncidentState.Expired)]
    public void An_incident_that_ended_without_anybody_needing_to_act_is_not_an_event(IncidentState state)
    {
        // Suppressed means dedup, flapping or a maintenance window already answered it.
        NotificationEnqueue.Classify(state, EscalationReason.None).Should().BeNull();
    }

    [Theory]
    [InlineData(EscalationReason.VerificationFailed)]
    [InlineData(EscalationReason.RollbackPerformed)]
    [InlineData(EscalationReason.Quarantined)]
    public void The_three_give_up_reasons_say_the_agent_tried_and_was_wrong(EscalationReason reason)
    {
        // A different thing to learn than "the agent declined to try", and not derivable from
        // the state, because GiveUpAsync lands on Escalated for all three.
        NotificationEnqueue.Classify(IncidentState.Escalated, reason)
            .Should().Be(NotificationEvent.VerificationFailed);
    }

    [Theory]
    [InlineData(EscalationReason.NoPlanProduced)]
    [InlineData(EscalationReason.PolicyDenied)]
    [InlineData(EscalationReason.SelfSignal)]
    [InlineData(EscalationReason.StormCircuitBreaker)]
    public void Every_other_reason_is_a_plain_escalation(EscalationReason reason)
    {
        NotificationEnqueue.Classify(IncidentState.Escalated, reason)
            .Should().Be(NotificationEvent.IncidentEscalated);
    }

    [Fact]
    public void Every_escalation_reason_produces_a_message_of_some_kind()
    {
        // The universal give-up edge is always available, so no reason may fall through to
        // silence - including ApprovalTimedOut, which has no producer yet and will.
        foreach (var reason in Enum.GetValues<EscalationReason>())
        {
            NotificationEnqueue.Classify(IncidentState.Escalated, reason)
                .Should().NotBeNull($"escalating for {reason} must reach somebody");
        }
    }
}
