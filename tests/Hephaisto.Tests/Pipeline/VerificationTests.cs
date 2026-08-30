using Microsoft.Extensions.Logging.Abstractions;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core;
using Hephaisto.Core.Domain;
using Hephaisto.Tests.TestData;
using NSubstitute;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// The schedule an executed action is judged on, and what "undo" is allowed to mean.
/// </summary>
public sealed class VerificationTests
{
    private static AgentAction Executed(ActionType type, string? preState = null, string? rollbackSpec = null) => new()
    {
        IncidentId = Given.IncidentId,
        Type = type,
        Target = Given.Target(),
        Risk = RiskTier.Low,
        State = ActionState.Executed,
        ExecutedAt = Given.Now,
        PreState = preState,
        RollbackSpec = rollbackSpec,
    };

    // --- the schedule -----------------------------------------------------------------------

    [Fact]
    public void An_executed_action_is_checked_three_times()
    {
        var scheduled = VerificationSchedule.For(Executed(ActionType.RestartPod), Given.Now).ToList();

        scheduled.Should().HaveCount(3);
        scheduled.Select(v => v.Attempt).Should().Equal(1, 2, 3);
        scheduled.Select(v => v.DueAt).Should().Equal(
            Given.Now.AddSeconds(60),
            Given.Now.AddMinutes(5),
            Given.Now.AddMinutes(15));
    }

    [Fact]
    public void Every_scheduled_check_starts_pending_and_unrun()
    {
        foreach (var v in VerificationSchedule.For(Executed(ActionType.RestartPod), Given.Now))
        {
            v.Outcome.Should().Be(VerificationOutcome.Pending);
            v.RanAt.Should().BeNull();
        }
    }

    [Fact]
    public void Only_the_last_attempt_may_conclude_a_failure()
    {
        // The scheduler rolls back on a Failed verdict at FinalAttempt and never before. The
        // three checks ask different questions - "did anything obviously break", "has it
        // converged", "did it come back" - so treating the first as decisive would revert an
        // action because a pod was still pulling its image.
        VerificationSchedule.FinalAttempt.Should().Be(3);
        VerificationSchedule.Delays.Should().HaveCount(VerificationSchedule.FinalAttempt);
    }

    // --- what can be undone -------------------------------------------------------------------

    private static (ActionRollback Sut, IActionExecutor Executor) Build()
    {
        var executor = Substitute.For<IActionExecutor>();

        executor
            .ExecuteAsync(Arg.Any<AgentAction>(), Arg.Any<CancellationToken>())
            .Returns(new ActionExecutionResult { Outcome = ActionExecutionOutcome.Executed });

        return (new ActionRollback(executor, Given.Clock(), NullLogger<ActionRollback>.Instance), executor);
    }

    [Theory]
    [InlineData(ActionType.RestartPod)]
    [InlineData(ActionType.RolloutRestart)]
    [InlineData(ActionType.DeleteStuckJob)]
    [InlineData(ActionType.DeleteFailedJobPods)]
    public async Task An_action_with_no_inverse_is_not_reverted_and_says_so(ActionType type)
    {
        // A restarted pod cannot be un-restarted and a deleted Job cannot be brought back. The
        // honest answer is escalation, which is what Reverted=false makes the caller do.
        var (sut, executor) = Build();

        var result = await sut.TryRevertAsync(Executed(type), TestContext.Current.CancellationToken);

        result.Reverted.Should().BeFalse();
        result.Detail.Should().Contain("no inverse");

        await executor.DidNotReceive().ExecuteAsync(Arg.Any<AgentAction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scale_is_reverted_to_the_replica_count_that_was_observed()
    {
        var (sut, executor) = Build();
        var action = Executed(ActionType.ScaleWorkload, preState: """{"kind":"Deployment","replicas":3}""");

        var result = await sut.TryRevertAsync(action, TestContext.Current.CancellationToken);

        result.Reverted.Should().BeTrue();
        action.State.Should().Be(ActionState.RolledBack);

        await executor.Received(1).ExecuteAsync(
            Arg.Is<AgentAction>(a =>
                a.Type == ActionType.ScaleWorkload &&
                a.IsRollbackOf == action.Id &&
                a.ApprovalSource == ApprovalSource.Auto &&
                a.Arguments!.Contains("\"replicas\":3")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_observed_state_beats_the_models_rollback_spec()
    {
        // PreState is what the cluster looked like; RollbackSpec is what the model said it
        // would look like. They are only ever consulted because something already went wrong,
        // which is the worst moment to prefer a claim over an observation.
        var (sut, executor) = Build();

        var action = Executed(
            ActionType.ScaleWorkload,
            preState: """{"replicas":3}""",
            rollbackSpec: """{"replicas":99}""");

        await sut.TryRevertAsync(action, TestContext.Current.CancellationToken);

        await executor.Received(1).ExecuteAsync(
            Arg.Is<AgentAction>(a => a.Arguments!.Contains("\"replicas\":3")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_malformed_rollback_spec_reverts_nothing_rather_than_guessing()
    {
        // The spec is model-authored, so malformed is ordinary rather than exceptional - and
        // a rollback that guessed a replica count would be a mutation nobody chose.
        var (sut, executor) = Build();

        var result = await sut.TryRevertAsync(
            Executed(ActionType.ScaleWorkload, rollbackSpec: "not json at all"),
            TestContext.Current.CancellationToken);

        result.Reverted.Should().BeFalse();
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<AgentAction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rollback_that_cannot_execute_is_reported_as_not_reverted()
    {
        // The caller escalates on Reverted=false. Reporting a failed revert as done would
        // leave a cluster in a state nobody chose and an incident saying it was handled.
        var executor = Substitute.For<IActionExecutor>();
        executor
            .ExecuteAsync(Arg.Any<AgentAction>(), Arg.Any<CancellationToken>())
            .Returns(new ActionExecutionResult
            {
                Outcome = ActionExecutionOutcome.Refused,
                Detail = "workload cooldown",
            });

        var sut = new ActionRollback(executor, Given.Clock(), NullLogger<ActionRollback>.Instance);
        var action = Executed(ActionType.ScaleWorkload, preState: """{"replicas":3}""");

        var result = await sut.TryRevertAsync(action, TestContext.Current.CancellationToken);

        result.Reverted.Should().BeFalse();
        result.Detail.Should().Contain("could not run");
        action.State.Should().NotBe(ActionState.RolledBack);
    }
}
