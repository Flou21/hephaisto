using Hephaisto.Core;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests.CodeFix;

public sealed class CodeFixStateMachineTests
{
    private readonly CodeFixStateMachine machine = new(Given.Clock());

    private static CodeFixAttempt At(CodeFixState state) => new() { State = state };

    [Fact]
    public void HappyPath_EndsInPrOpened()
    {
        var a = At(CodeFixState.Eligible);

        machine.BeginPlanning(a, "codefix-x-plan");
        machine.PlanReady(a);
        machine.Approve(a, "flo@true-relevance.com", ApprovalSource.Oidc);
        machine.BeginImplementing(a, "codefix-x-implement");
        machine.PrOpened(a, "https://github.com/o/r/pull/1", 1);

        a.State.Should().Be(CodeFixState.PrOpened);
        a.ApprovedBy.Should().Be("flo@true-relevance.com");
        a.PlanJobName.Should().Be("codefix-x-plan");
        a.FinishedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(CodeFixState.Eligible)]
    [InlineData(CodeFixState.Planning)]
    [InlineData(CodeFixState.Implementing)]
    [InlineData(CodeFixState.PrOpened)]
    [InlineData(CodeFixState.Denied)]
    public void Approve_OnlyFromPlanReady(CodeFixState from)
    {
        var act = () => machine.Approve(At(from), "a human", ApprovalSource.Ui);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(IncidentStateMachine.ModelActor)]
    [InlineData(IncidentStateMachine.SystemActor)]
    [InlineData(IncidentStateMachine.AutoActor)]
    [InlineData(IncidentStateMachine.VerifierActor)]
    [InlineData("")]
    public void MachineActors_CannotOpenTheDoor(string actor)
    {
        var a = At(CodeFixState.PlanReady);

        ((Action)(() => machine.Approve(a, actor, ApprovalSource.Api))).Should().Throw<ArgumentException>();
        ((Action)(() => machine.Deny(a, actor, null, ApprovalSource.Ui))).Should().Throw<ArgumentException>();
        a.State.Should().Be(CodeFixState.PlanReady);
    }

    [Fact]
    public void NeedsCait_IsNeverImplemented()
    {
        var a = At(CodeFixState.PlanReady);
        a.NeedsCait = true;

        ((Action)(() => machine.Approve(a, "a human", ApprovalSource.Ui))).Should().Throw<InvalidOperationException>();
        a.State.Should().Be(CodeFixState.PlanReady);
    }

    [Fact]
    public void Deny_RecordsWhoAndWhy()
    {
        var a = At(CodeFixState.PlanReady);

        machine.Deny(a, "a human", "wrong file", ApprovalSource.GitHub);

        a.State.Should().Be(CodeFixState.Denied);
        a.FailureReason.Should().Be("wrong file");
        a.ApprovedBy.Should().Be("a human");
        a.ApprovalSource.Should().Be(ApprovalSource.GitHub, "the attempt's page says through what a plan was denied");
    }

    [Theory]
    [InlineData(CodeFixState.Eligible)]
    [InlineData(CodeFixState.Planning)]
    [InlineData(CodeFixState.PlanReady)]
    [InlineData(CodeFixState.Implementing)]
    public void Cancel_FromEveryOpenState(CodeFixState from)
    {
        var a = At(from);

        machine.Cancel(a, "kill switch");

        a.State.Should().Be(CodeFixState.Cancelled);
    }

    [Theory]
    [InlineData(CodeFixState.PrOpened)]
    [InlineData(CodeFixState.Failed)]
    [InlineData(CodeFixState.Cancelled)]
    public void TerminalStates_StayTerminal(CodeFixState from)
    {
        ((Action)(() => machine.Cancel(At(from), "x"))).Should().Throw<InvalidOperationException>();
        ((Action)(() => machine.Fail(At(from), "x"))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Expire_OnlyAPlanWaitingOnAHuman()
    {
        var a = At(CodeFixState.PlanReady);
        machine.Expire(a);
        a.State.Should().Be(CodeFixState.Expired);

        ((Action)(() => machine.Expire(At(CodeFixState.Implementing)))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OpenStates_AreExactlyTheOnesHoldingASlot()
    {
        CodeFixStates.Open.Should().BeEquivalentTo(
            [CodeFixState.Eligible, CodeFixState.Planning, CodeFixState.PlanReady, CodeFixState.Implementing]);
    }
}
