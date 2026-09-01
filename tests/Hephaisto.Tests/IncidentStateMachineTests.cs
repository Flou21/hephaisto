using Hephaisto.Core;
using Hephaisto.Core.Domain;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests;

public sealed class IncidentStateMachineTests
{
    private static readonly IncidentState[] OpenStates =
    [
        IncidentState.Detected,
        IncidentState.Triaging,
        IncidentState.Investigating,
        IncidentState.AwaitingApproval,
        IncidentState.Acting,
        IncidentState.Verifying,
    ];

    private static IncidentStateMachine Machine() => new(Given.Clock());

    // --- legal edges ------------------------------------------------------------------------

    [Fact]
    public void Reinvestigate_MovesEscalatedBackToInvestigating()
    {
        var incident = Given.Incident(IncidentState.Investigating);
        Machine().Escalate(incident, EscalationReason.InvestigationFailed);

        Machine().Reinvestigate(incident, "provider was overloaded", "flo");

        incident.State.Should().Be(IncidentState.Investigating);
    }

    [Fact]
    public void Reinvestigate_ClearsTheEscalationReasonOfTheAttemptItReplaces()
    {
        var incident = Given.Incident(IncidentState.Investigating);
        Machine().Escalate(incident, EscalationReason.BudgetExhausted);

        Machine().Reinvestigate(incident, "retry", "flo");

        // Leaving BudgetExhausted here would describe an attempt that is no longer current.
        incident.EscalationReason.Should().Be(EscalationReason.None);
    }

    [Fact]
    public void Reinvestigate_MovesExpiredBackToInvestigating()
    {
        var incident = Given.Incident(IncidentState.Investigating);
        Machine().Expire(incident, "signal stopped arriving");

        Machine().Reinvestigate(incident, "someone wants an answer anyway", "flo");

        incident.State.Should().Be(IncidentState.Investigating);
    }

    [Fact]
    public void Reinvestigate_RecordsWhoAskedInTheEventLog()
    {
        var incident = Given.Incident(IncidentState.Investigating);
        Machine().Escalate(incident, EscalationReason.InvestigationFailed);

        var evt = Machine().Reinvestigate(incident, "retry", "flo");

        evt.From.Should().Be(IncidentState.Escalated);
        evt.To.Should().Be(IncidentState.Investigating);
        evt.Reason.Should().Contain("flo");
    }

    [Theory]
    [InlineData("hephaisto/model")]
    [InlineData("gemini")]
    [InlineData("LLM")]
    public void Reinvestigate_RefusesTheModelAsRequester(string actor)
    {
        // A retry is the most expensive path in the system. If it ever becomes automatic it
        // must arrive with its own cap under its own name, not by reusing the human door.
        var incident = Given.Incident(IncidentState.Investigating);
        Machine().Escalate(incident, EscalationReason.InvestigationFailed);

        var act = () => Machine().Reinvestigate(incident, "retry", actor);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reinvestigate_RequiresARequester()
    {
        var incident = Given.Incident(IncidentState.Investigating);
        Machine().Escalate(incident, EscalationReason.InvestigationFailed);

        var act = () => Machine().Reinvestigate(incident, "retry", "  ");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Triaging)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.AwaitingApproval)]
    [InlineData(IncidentState.Acting)]
    [InlineData(IncidentState.Verifying)]
    [InlineData(IncidentState.Suppressed)]
    [InlineData(IncidentState.Resolved)]
    public void Reinvestigate_RefusesEveryStateThatIsNotAGiveUp(IncidentState from)
    {
        // Investigating in particular: a second concurrent run would double-spend, and this
        // is the authoritative guard behind the racy in-flight check in IncidentQueries.
        // Resolved belongs to Reopen, which carries different semantics.
        var incident = Given.Incident(from);

        var act = () => Machine().Reinvestigate(incident, "retry", "flo");

        act.Should().Throw<InvalidStateTransitionException>();
    }


    [Fact]
    public void Triage_MovesDetectedToTriaging()
    {
        var incident = Given.Incident(IncidentState.Detected);

        Machine().Triage(incident, "new signal");

        incident.State.Should().Be(IncidentState.Triaging);
    }

    [Fact]
    public void Suppress_MovesTriagingToSuppressed_AndRecordsWhy()
    {
        var incident = Given.Incident(IncidentState.Triaging);

        Machine().Suppress(incident, SuppressionReason.DuplicateOfOpenIncident);

        incident.State.Should().Be(IncidentState.Suppressed);
        incident.SuppressionReason.Should().Be(SuppressionReason.DuplicateOfOpenIncident);
        incident.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void BeginInvestigation_MovesTriagingToInvestigating()
    {
        var incident = Given.Incident(IncidentState.Triaging);

        Machine().BeginInvestigation(incident, "not a duplicate");

        incident.State.Should().Be(IncidentState.Investigating);
    }

    [Fact]
    public void AwaitApproval_MovesInvestigatingToAwaitingApproval()
    {
        var incident = Given.Incident(IncidentState.Investigating);

        Machine().AwaitApproval(incident, "policy requires approval");

        incident.State.Should().Be(IncidentState.AwaitingApproval);
    }

    [Theory]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.AwaitingApproval)]
    public void BeginActing_IsReachableFromInvestigatingAndAwaitingApproval(IncidentState from)
    {
        var incident = Given.Incident(from);

        Machine().BeginActing(incident, "policy allowed");

        incident.State.Should().Be(IncidentState.Acting);
    }

    [Fact]
    public void BeginVerifying_MovesActingToVerifying()
    {
        var incident = Given.Incident(IncidentState.Acting);

        Machine().BeginVerifying(incident, "actions executed");

        incident.State.Should().Be(IncidentState.Verifying);
    }

    [Theory]
    [MemberData(nameof(EveryOpenState))]
    public void Resolve_IsReachableFromEveryOpenState(IncidentState from)
    {
        var incident = Given.Incident(from);

        Machine().Resolve(incident, "pod is Ready again", IncidentStateMachine.VerifierActor);

        incident.State.Should().Be(IncidentState.Resolved);
        incident.ResolvedAt.Should().Be(Given.Now);
        incident.Resolution.Should().Be("pod is Ready again");
    }

    [Theory]
    [MemberData(nameof(EveryOpenState))]
    public void Escalate_IsReachableFromEveryOpenState(IncidentState from)
    {
        var incident = Given.Incident(from);

        Machine().Escalate(incident, EscalationReason.LowConfidence, "no hypothesis above 0.4");

        incident.State.Should().Be(IncidentState.Escalated);
        incident.EscalationReason.Should().Be(EscalationReason.LowConfidence);
    }

    [Theory]
    [MemberData(nameof(EveryOpenState))]
    public void Expire_IsReachableFromEveryOpenState(IncidentState from)
    {
        var incident = Given.Incident(from);

        Machine().Expire(incident, "signal stopped arriving");

        incident.State.Should().Be(IncidentState.Expired);
        incident.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Reopen_MovesResolvedBackToInvestigating_AndClearsTheResolution()
    {
        // Reopening rather than opening a fresh incident is what lets the oscillation detector
        // see that the same fix has now failed repeatedly on one workload.
        var incident = Given.Incident(IncidentState.Verifying);
        var machine = Machine();
        machine.Resolve(incident, "pod is Ready again", "flo");

        machine.Reopen(incident, "signal returned after 6 minutes");

        incident.State.Should().Be(IncidentState.Investigating);
        incident.ResolvedAt.Should().BeNull();
        incident.Resolution.Should().BeNull();
    }

    [Fact]
    public void AFullHappyPath_WalksTheWholeDiagram()
    {
        var incident = Given.Incident();
        var machine = Machine();

        machine.Triage(incident, "accepted");
        machine.BeginInvestigation(incident, "not a duplicate");
        machine.AwaitApproval(incident, "restart needs a human");
        machine.BeginActing(incident, "approved by flo");
        machine.BeginVerifying(incident, "restart issued");
        machine.Resolve(incident, "verified Ready", IncidentStateMachine.VerifierActor);

        incident.State.Should().Be(IncidentState.Resolved);
        incident.Events.Select(e => e.To).Should().Equal(
            IncidentState.Triaging,
            IncidentState.Investigating,
            IncidentState.AwaitingApproval,
            IncidentState.Acting,
            IncidentState.Verifying,
            IncidentState.Resolved);
    }

    // --- illegal edges ----------------------------------------------------------------------

    [Theory]
    [InlineData(IncidentState.Triaging)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Resolved)]
    [InlineData(IncidentState.Suppressed)]
    public void Triage_FromAnythingButDetected_Throws(IncidentState from)
    {
        var incident = Given.Incident(from);

        var act = () => Machine().Triage(incident, "nope");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Acting)]
    public void Suppress_FromAnythingButTriaging_Throws(IncidentState from)
    {
        var act = () => Machine().Suppress(Given.Incident(from), SuppressionReason.Flapping);

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Acting)]
    [InlineData(IncidentState.Resolved)]
    public void BeginInvestigation_FromAnythingButTriaging_Throws(IncidentState from)
    {
        var act = () => Machine().BeginInvestigation(Given.Incident(from), "nope");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Acting)]
    [InlineData(IncidentState.Verifying)]
    public void AwaitApproval_FromAnythingButInvestigating_Throws(IncidentState from)
    {
        var act = () => Machine().AwaitApproval(Given.Incident(from), "nope");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Triaging)]
    [InlineData(IncidentState.Verifying)]
    [InlineData(IncidentState.Resolved)]
    public void BeginActing_FromAnIllegalPredecessor_Throws(IncidentState from)
    {
        var act = () => Machine().BeginActing(Given.Incident(from), "nope");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.AwaitingApproval)]
    [InlineData(IncidentState.Verifying)]
    public void BeginVerifying_FromAnythingButActing_Throws(IncidentState from)
    {
        // Verification without an action to verify is meaningless, and would let an incident
        // reach Resolved with nothing having happened.
        var act = () => Machine().BeginVerifying(Given.Incident(from), "nope");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Resolved)]
    [InlineData(IncidentState.Suppressed)]
    [InlineData(IncidentState.Expired)]
    [InlineData(IncidentState.Escalated)]
    public void ClosedIncidents_CannotBeResolvedEscalatedOrExpired(IncidentState from)
    {
        var machine = Machine();

        FluentActions.Invoking(() => machine.Resolve(Given.Incident(from), "x", "flo"))
            .Should().Throw<InvalidStateTransitionException>();
        FluentActions.Invoking(() => machine.Escalate(Given.Incident(from), EscalationReason.LowConfidence))
            .Should().Throw<InvalidStateTransitionException>();
        FluentActions.Invoking(() => machine.Expire(Given.Incident(from), "x"))
            .Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(IncidentState.Detected)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Escalated)]
    [InlineData(IncidentState.Expired)]
    public void Reopen_FromAnythingButResolved_Throws(IncidentState from)
    {
        var act = () => Machine().Reopen(Given.Incident(from), "nope");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void AnIllegalTransition_LeavesTheIncidentUntouched()
    {
        var incident = Given.Incident(IncidentState.Resolved);

        FluentActions.Invoking(() => Machine().Triage(incident, "nope"))
            .Should().Throw<InvalidStateTransitionException>();

        incident.State.Should().Be(IncidentState.Resolved);
        incident.Events.Should().BeEmpty("a rejected transition must not leave an audit event behind");
    }

    [Fact]
    public void InvalidStateTransitionException_CarriesBothEnds()
    {
        var act = () => Machine().BeginVerifying(Given.Incident(IncidentState.Detected), "nope");

        act.Should().Throw<InvalidStateTransitionException>()
            .Which.Should().Match<InvalidStateTransitionException>(
                e => e.From == IncidentState.Detected && e.To == IncidentState.Verifying);
    }

    // --- audit ------------------------------------------------------------------------------

    [Fact]
    public void EveryTransition_AppendsAnEvent()
    {
        var incident = Given.Incident();
        var machine = Machine();

        machine.Triage(incident, "accepted");
        machine.BeginInvestigation(incident, "not a duplicate");

        incident.Events.Should().HaveCount(2);
    }

    [Fact]
    public void TheAppendedEvent_CarriesFromToReasonAndTime()
    {
        // Incident.State is the column you query; the event log is the one you audit. A single
        // mutable column cannot answer "how long was this awaiting approval".
        var incident = Given.Incident(IncidentState.Investigating);

        var evt = Machine().AwaitApproval(incident, "restart needs a human");

        evt.From.Should().Be(IncidentState.Investigating);
        evt.To.Should().Be(IncidentState.AwaitingApproval);
        evt.Reason.Should().Be("restart needs a human");
        evt.At.Should().Be(Given.Now);
        evt.IncidentId.Should().Be(incident.Id);
        incident.Events.Should().ContainSingle().Which.Should().BeSameAs(evt);
    }

    [Fact]
    public void TheSuppressionEvent_NamesTheReason()
    {
        var incident = Given.Incident(IncidentState.Triaging);

        var evt = Machine().Suppress(incident, SuppressionReason.Flapping, "seen 12 times in 5 minutes");

        evt.Reason.Should().Contain("Flapping").And.Contain("12 times");
    }

    [Fact]
    public void TheClockIsInjected_SoEventTimesAreDeterministic()
    {
        var clock = Given.Clock();
        var machine = new IncidentStateMachine(clock);
        var incident = Given.Incident();

        machine.Triage(incident, "accepted");
        clock.UtcNow = Given.Now.AddMinutes(9);
        machine.BeginInvestigation(incident, "not a duplicate");

        incident.Events[0].At.Should().Be(Given.Now);
        incident.Events[1].At.Should().Be(Given.Now.AddMinutes(9));
    }

    // --- the resolution invariant -------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithoutAGranter_Throws(string? grantedBy)
    {
        // Resolution is a grant, not a conclusion. An incident with no named granter is an
        // incident nobody actually checked.
        var act = () => Machine().Resolve(Given.Incident(IncidentState.Verifying), "looks fine", grantedBy!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("hephaisto/model")]
    [InlineData("HEPHAISTO/MODEL")]
    [InlineData("model")]
    [InlineData("llm")]
    [InlineData("gemini")]
    public void Resolve_GrantedByTheModel_Throws(string grantedBy)
    {
        // The LLM may propose Resolved; only verification grants it. A model asked "is this
        // fixed?" has every incentive to say yes and no way to check, and an incident closed on
        // its own say-so is indistinguishable in the database from one that was actually fixed.
        var act = () => Machine().Resolve(Given.Incident(IncidentState.Verifying), "I fixed it", grantedBy);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*the model proposes, verification grants*");
    }

    [Fact]
    public void Resolve_RejectedForTheModel_LeavesTheIncidentOpen()
    {
        var incident = Given.Incident(IncidentState.Verifying);

        FluentActions.Invoking(() => Machine().Resolve(incident, "I fixed it", IncidentStateMachine.ModelActor))
            .Should().Throw<ArgumentException>();

        incident.State.Should().Be(IncidentState.Verifying);
        incident.ResolvedAt.Should().BeNull();
        incident.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("hephaisto/verifier")]
    [InlineData("flo")]
    public void Resolve_GrantedByVerificationOrAHuman_Succeeds(string grantedBy)
    {
        var incident = Given.Incident(IncidentState.Verifying);

        var evt = Machine().Resolve(incident, "pod Ready for 15 minutes", grantedBy);

        incident.State.Should().Be(IncidentState.Resolved);
        evt.Reason.Should().Contain(grantedBy, "the audit trail has to say who granted the resolution");
    }

    public static TheoryData<IncidentState> EveryOpenState()
    {
        var data = new TheoryData<IncidentState>();
        foreach (var state in OpenStates)
        {
            data.Add(state);
        }

        return data;
    }

    // #71: the first action ever executed on a cluster was written with approvedBy null, while
    // three doc comments said it is always populated. Doc comments are not a guard; this is.
    [Fact]
    public void The_auto_actor_is_the_name_ApprovalSource_Auto_documents()
    {
        // ApprovalSource.Auto's own summary says "ApprovedBy is hephaisto/auto". If the constant
        // and that sentence drift, the audit trail and its documentation disagree.
        IncidentStateMachine.AutoActor.Should().Be("hephaisto/auto");
    }

    [Fact]
    public void The_auto_actor_may_grant_because_policy_is_not_a_model()
    {
        // Policy admitting a low-risk action is rules deciding, not the model deciding, and
        // that is exactly what L3 means. The forbidden list exists to stop a model opinion
        // being laundered into an approval - this must not be caught by it.
        IncidentStateMachine.IsForbiddenGranter(IncidentStateMachine.AutoActor).Should().BeFalse();
    }

    [Fact]
    public void A_model_identity_still_cannot_grant_anything()
    {
        // The other direction, so the test above cannot pass by the list being empty.
        IncidentStateMachine.IsForbiddenGranter(IncidentStateMachine.ModelActor).Should().BeTrue();
        IncidentStateMachine.IsForbiddenGranter("gemini").Should().BeTrue();
    }
}
