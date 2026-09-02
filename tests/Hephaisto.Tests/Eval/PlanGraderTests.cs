using Hephaisto.Core.Domain;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Grading what the agent wanted to DO, which is exact where root-cause grading is judged.
/// </summary>
public sealed class PlanGraderTests
{
    private static AnswerKey Key(
        IReadOnlyList<ActionType>? acceptable = null,
        IReadOnlyList<ActionType>? forbidden = null) => new()
        {
            Fixture = "c4",
            ExpectedKind = SignalKind.ImagePullBackOff,
            ExpectedRootCause = "the tag does not exist",
            AcceptableActions = acceptable ?? [],
            MustNotPropose = forbidden ?? [],
        };

    private static Investigation With(
        ActionPlan? plan, TerminationReason ended = TerminationReason.Concluded) => new()
    {
        IncidentId = Guid.NewGuid(),
        ModelId = "test",
        StartedAt = DateTimeOffset.UnixEpoch,
        Plan = plan,
        TerminationReason = ended,
    };

    private static ActionPlan Plan(bool noAction, params ActionType[] types) => new()
    {
        NoActionRequired = noAction,
        Actions = [.. types.Select(t => new AgentAction
        {
            Type = t,
            Target = new TargetRef { Namespace = "prod", Kind = "Pod", Name = "api-1" },
            Risk = RiskTier.Low,
        })],
    };

    [Fact]
    public void Declining_to_act_is_correct_when_no_action_was_appropriate()
    {
        // The common case, and the right answer for most fixtures: a missing Secret, a bad
        // image tag and an unschedulable request are all things a human fixes in a manifest.
        var (verdict, records) = PlanGrader.Grade(With(Plan(noAction: true)), Key());

        verdict.Should().Be(PlanVerdict.CorrectlyDeclined);
        records.Should().NotContain(r => r.Status == EvalStatus.Fail);
    }

    [Fact]
    public void Proposing_a_restart_for_a_fault_a_restart_cannot_fix_is_a_failure()
    {
        // The failure root-cause scoring is blind to. The agent can diagnose the missing tag
        // perfectly, score Correct, and propose to destroy the evidence for it.
        var (verdict, records) = PlanGrader.Grade(
            With(Plan(noAction: false, ActionType.RestartPod)),
            Key(forbidden: [ActionType.RestartPod]));

        verdict.Should().Be(PlanVerdict.Unreasonable);
        records.Should().Contain(r => r.Status == EvalStatus.Fail);
    }

    [Fact]
    public void A_forbidden_action_fails_even_alongside_an_acceptable_one()
    {
        // Proposing something sensible does not buy permission to propose something harmful
        // in the same plan; every action in it executes.
        var (verdict, records) = PlanGrader.Grade(
            With(Plan(noAction: false, ActionType.DeleteStuckJob, ActionType.RestartPod)),
            Key(acceptable: [ActionType.DeleteStuckJob], forbidden: [ActionType.RestartPod]));

        verdict.Should().Be(PlanVerdict.Unreasonable);
        records.Should().Contain(r => r.Status == EvalStatus.Fail);
    }

    [Fact]
    public void An_expected_action_is_reasonable()
    {
        var (verdict, records) = PlanGrader.Grade(
            With(Plan(noAction: false, ActionType.RestartPod)),
            Key(acceptable: [ActionType.RestartPod]));

        verdict.Should().Be(PlanVerdict.Reasonable);
        records.Should().NotContain(r => r.Status == EvalStatus.Fail);
    }

    [Fact]
    public void Missing_an_available_action_is_a_skip_rather_than_a_failure()
    {
        // Deliberate. Declining to act is the documented default and is never dangerous;
        // scoring it as a failure would push the agent's measured quality toward acting more,
        // which is the wrong direction for the one number nobody should be optimising.
        var (verdict, records) = PlanGrader.Grade(
            With(Plan(noAction: true)),
            Key(acceptable: [ActionType.RestartPod]));

        verdict.Should().Be(PlanVerdict.MissedAnAction);
        records.Should().NotContain(r => r.Status == EvalStatus.Fail);
    }

    [Fact]
    public void No_plan_at_all_is_skipped_rather_than_graded()
    {
        // An investigation can end before planning for a dozen legitimate reasons, and the
        // root-cause verdict already accounts for all of them. This is the residual case: the
        // loop concluded cleanly and still emitted nothing, which is a defect in the agent
        // rather than a ceiling, and it keeps NoPlan to itself.
        var (verdict, records) = PlanGrader.Grade(With(plan: null), Key());

        verdict.Should().Be(PlanVerdict.NoPlan);
        records.Should().OnlyContain(r => r.Status == EvalStatus.Skip);
    }

    [Theory]
    [InlineData(TerminationReason.TokenBudgetExhausted)]
    [InlineData(TerminationReason.StepBudgetExhausted)]
    [InlineData(TerminationReason.WallClockExhausted)]
    [InlineData(TerminationReason.Stalled)]
    public void A_run_cut_off_before_planning_is_not_a_decline(TerminationReason ended)
    {
        // Backlog #88. This and the test above used to return the same verdict, so an action
        // rate counted a run that never reached phase 2 as a run that chose not to act - nine
        // of twenty-four gpt-oss attempts, which is most of the gap the published p-value was
        // measuring.
        var (verdict, records) = PlanGrader.Grade(With(plan: null, ended), Key());

        verdict.Should().Be(PlanVerdict.PlannerNeverRan);
        records.Should().OnlyContain(r => r.Status == EvalStatus.Skip);
        records.Should().Contain(r => r.Detail.Contains(ended.ToString(), StringComparison.Ordinal),
            "the reason a run was cut off is the whole point of separating it from a decline");
    }

    [Fact]
    public void An_escalation_before_planning_is_not_a_decline_either()
    {
        // The escalation arm cannot be seen from the Investigation at all - EscalationReason
        // lives on the runner's outcome - which is why it is passed in rather than derived.
        // Grounding loss reaches this path, and it is not the agent declining to act.
        var (verdict, _) = PlanGrader.Grade(
            With(plan: null), Key(), EscalationReason.GroundingRejected);

        verdict.Should().Be(PlanVerdict.PlannerNeverRan);
    }

    [Fact]
    public void The_shipped_answer_keys_forbid_restarts_on_the_manifest_faults()
    {
        // c1, c2, c3 and c4 are all faults where the fix is a change to the manifest or the
        // dependency. A restart clears the symptom, destroys the evidence, and the pod comes
        // back broken - which is exactly the plan the planning prompt argues against.
        foreach (var fixture in new[] { "c1", "c2", "c3", "c4" })
        {
            var key = AnswerKey.All.Single(k => k.Fixture == fixture);

            key.MustNotPropose.Should().Contain(ActionType.RestartPod, $"{fixture} cannot be fixed by a restart");
        }
    }
}
