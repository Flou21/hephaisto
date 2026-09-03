using Hephaisto.Core.Classification;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests;

/// <summary>
/// The ordering that decides whether a later signal re-labels an incident. Backlog #70.
/// </summary>
public class SignalKindSpecificityTests
{
    [Theory]
    [InlineData(SignalKind.PodNotReady, SignalKind.ImagePullBackOff)]
    [InlineData(SignalKind.PodNotReady, SignalKind.Unschedulable)]
    [InlineData(SignalKind.PodNotReady, SignalKind.ConfigError)]
    [InlineData(SignalKind.ReadinessFlapping, SignalKind.CrashLoopBackOff)]
    [InlineData(SignalKind.TargetDown, SignalKind.OomKilled)]
    [InlineData(SignalKind.ReplicaMismatch, SignalKind.ImagePullBackOff)]
    [InlineData(SignalKind.Unknown, SignalKind.PodNotReady)]
    public void A_signal_that_names_the_mechanism_replaces_one_that_names_a_state(
        SignalKind generic, SignalKind specific) =>
        SignalKindSpecificity.ShouldReplace(generic, specific).Should().BeTrue();

    [Theory]
    [InlineData(SignalKind.ImagePullBackOff, SignalKind.PodNotReady)]
    [InlineData(SignalKind.Unschedulable, SignalKind.PodNotReady)]
    [InlineData(SignalKind.OomKilled, SignalKind.TargetDown)]
    [InlineData(SignalKind.CrashLoopBackOff, SignalKind.ReadinessFlapping)]
    public void A_state_never_overwrites_a_mechanism(SignalKind specific, SignalKind generic) =>
        SignalKindSpecificity.ShouldReplace(specific, generic).Should().BeFalse();

    [Fact]
    public void Two_equally_specific_kinds_do_not_replace_each_other_in_either_direction()
    {
        // If they did, the race this replaces would have moved rather than been fixed: the
        // incident's label would depend on arrival order again, just later.
        SignalKindSpecificity.ShouldReplace(SignalKind.OomKilled, SignalKind.CrashLoopBackOff)
            .Should().BeFalse();
        SignalKindSpecificity.ShouldReplace(SignalKind.CrashLoopBackOff, SignalKind.OomKilled)
            .Should().BeFalse();
    }

    [Fact]
    public void No_kind_replaces_itself() =>
        Enum.GetValues<SignalKind>().Should().AllSatisfy(k =>
            SignalKindSpecificity.ShouldReplace(k, k).Should().BeFalse());

    [Theory]
    [InlineData(SignalKind.Watchdog, SignalKind.OomKilled)]
    [InlineData(SignalKind.ObservabilityDegraded, SignalKind.ImagePullBackOff)]
    [InlineData(SignalKind.BudgetExhausted, SignalKind.CrashLoopBackOff)]
    [InlineData(SignalKind.OomKilled, SignalKind.Watchdog)]
    [InlineData(SignalKind.PodNotReady, SignalKind.ObservabilityDegraded)]
    public void Hephaistos_own_health_and_a_workloads_are_never_relabelled_into_each_other(
        SignalKind a, SignalKind b) =>
        // An incident about the alert path being broken is not the same incident as an
        // unhealthy workload, and merging them loses the one nobody else will report.
        SignalKindSpecificity.ShouldReplace(a, b).Should().BeFalse();

    [Fact]
    public void Every_kind_has_a_rank_in_range() =>
        Enum.GetValues<SignalKind>().Should().AllSatisfy(k =>
            SignalKindSpecificity.Of(k).Should().BeInRange(0, 3));

    [Fact]
    public void The_generic_kinds_are_exactly_the_ones_that_describe_a_state()
    {
        // Pinned deliberately. Adding a member to this set is a decision about which runbook
        // an incident gets, so it should be a visible edit rather than a side effect.
        var generic = Enum.GetValues<SignalKind>()
            .Where(k => SignalKindSpecificity.Of(k) == 1)
            .ToArray();

        generic.Should().BeEquivalentTo(new[]
        {
            SignalKind.PodNotReady,
            SignalKind.ReadinessFlapping,
            SignalKind.TargetDown,
            SignalKind.ReplicaMismatch,
            SignalKind.RestartStorm,
        });
    }

    [Fact]
    public void An_unranked_new_member_is_treated_as_specific()
    {
        // Guessing high leaves an incident with a label that is too precise; guessing low
        // silently overwrites a real finding with a generic one. The first is recoverable.
        SignalKindSpecificity.Of((SignalKind)9999).Should().Be(2);
    }
}
