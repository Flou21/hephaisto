using Watchtower.Core.Domain;
using Watchtower.Core.Safety;
using Watchtower.Tests.TestData;

namespace Watchtower.Tests;

public sealed class OscillationDetectorTests
{
    private static OscillationVerdict Evaluate(params ActionOutcome[] history) =>
        OscillationDetector.Evaluate(history, Given.Now, Given.Options());

    [Fact]
    public void ThreeReopeningRestartsInTwoHours_Quarantines()
    {
        // The failure mode that makes an autonomous remediator dangerous rather than useless:
        // restarting an OOMing pod always "works", so it gets done again, and again, and the
        // memory limit never gets raised.
        var verdict = Evaluate(
            Given.Restart(1.8),
            Given.Restart(1.2),
            Given.Restart(0.5));

        verdict.Quarantine.Should().BeTrue();
        verdict.Until.Should().Be(Given.Now.AddHours(24));
        verdict.Reason.Should().Contain("symptom");
    }

    [Fact]
    public void ThreeRestartsThatHeld_DoNotQuarantine()
    {
        // Three restarts that each fixed something are three successes. Only the reopening
        // makes it an oscillation.
        var verdict = Evaluate(
            Given.Restart(1.5, reopened: false),
            Given.Restart(1.0, reopened: false),
            Given.Restart(0.5, reopened: false));

        verdict.Quarantine.Should().BeFalse();
        verdict.Until.Should().BeNull();
    }

    [Fact]
    public void TwoReopeningRestarts_AreACoincidenceNotAPattern()
    {
        var verdict = Evaluate(Given.Restart(1.5), Given.Restart(0.5));

        verdict.Quarantine.Should().BeFalse();
    }

    [Fact]
    public void ThreeReopeningActionsOfDifferentTypes_DoNotAggregate()
    {
        // Three different attempts is an agent working the problem; three identical ones is an
        // agent stuck in a loop.
        var verdict = OscillationDetector.Evaluate(
            [
                new ActionOutcome(Given.Now.AddHours(-1.5), ActionType.RestartPod, true),
                new ActionOutcome(Given.Now.AddHours(-1.0), ActionType.RolloutRestart, true),
                new ActionOutcome(Given.Now.AddHours(-0.5), ActionType.ScaleWorkload, true),
            ],
            Given.Now,
            Given.Options());

        verdict.Quarantine.Should().BeFalse();
    }

    [Fact]
    public void ThreeReopeningRestartsSpreadWiderThanTwoHours_DoNotQuarantine()
    {
        var verdict = Evaluate(Given.Restart(8), Given.Restart(5), Given.Restart(3));

        verdict.Quarantine.Should().BeFalse();
    }

    [Fact]
    public void FourReopeningRestarts_StillQuarantine()
    {
        var verdict = Evaluate(
            Given.Restart(1.9),
            Given.Restart(1.4),
            Given.Restart(0.9),
            Given.Restart(0.3));

        verdict.Quarantine.Should().BeTrue();
    }

    [Fact]
    public void MixedOutcomesInTheWindow_NeedThreeReopeningsNotThreeActions()
    {
        var verdict = Evaluate(
            Given.Restart(1.9),
            Given.Restart(1.4, reopened: false),
            Given.Restart(0.9, reopened: false),
            Given.Restart(0.3));

        verdict.Quarantine.Should().BeFalse();
    }

    [Fact]
    public void EmptyHistory_IsQuiet()
    {
        var verdict = OscillationDetector.Evaluate([], Given.Now, Given.Options());

        verdict.Quarantine.Should().BeFalse();
        verdict.Backoff.Should().BeNull();
        verdict.Reason.Should().Contain("no prior actions");
    }

    [Fact]
    public void ShrinkingMeanTimeBetweenFailures_SuggestsABackoff()
    {
        // The interesting version of this decays over days: 8h, then 6h, then 5h. By the time
        // it fits inside a two-hour window a human should already have been told.
        var verdict = Evaluate(
            Given.Restart(20),
            Given.Restart(12),
            Given.Restart(6),
            Given.Restart(1));

        verdict.Quarantine.Should().BeFalse();
        verdict.Backoff.Should().Be(TimeSpan.FromMinutes(120));
        verdict.Reason.Should().Contain("shrinking");
    }

    [Fact]
    public void SteadyMeanTimeBetweenFailures_SuggestsNothing()
    {
        var verdict = Evaluate(Given.Restart(9), Given.Restart(6), Given.Restart(3));

        verdict.Quarantine.Should().BeFalse();
        verdict.Backoff.Should().BeNull();
        verdict.Reason.Should().Contain("no oscillation");
    }

    [Fact]
    public void ShrinkingMtbfThatHeldLastTime_SuggestsNothing()
    {
        // Nothing has come back, so there is nothing to back off from.
        var verdict = Evaluate(
            Given.Restart(20, reopened: false),
            Given.Restart(12, reopened: false),
            Given.Restart(6, reopened: false),
            Given.Restart(1, reopened: false));

        verdict.Backoff.Should().BeNull();
    }

    [Fact]
    public void TheBackoffIsCapped()
    {
        // Past twelve hours a backoff is indistinguishable from a quarantine, and pretending
        // otherwise produces a number nobody can reason about.
        var verdict = Evaluate(
            Given.Restart(35),
            Given.Restart(27),
            Given.Restart(20),
            Given.Restart(14),
            Given.Restart(9),
            Given.Restart(5),
            Given.Restart(2),
            Given.Restart(0));

        verdict.Backoff.Should().Be(OscillationDetector.MaxBackoff);
    }

    [Fact]
    public void QuarantineOutranksBackoff()
    {
        // Both signals fire here; the more restrictive one has to win.
        var verdict = Evaluate(
            Given.Restart(1.9),
            Given.Restart(1.0),
            Given.Restart(0.3));

        verdict.Quarantine.Should().BeTrue();
        verdict.Backoff.Should().BeNull();
    }

    [Fact]
    public void FutureDatedActions_AreIgnored()
    {
        // Clock skew between the agent and the API server must not manufacture a quarantine.
        var verdict = Evaluate(
            Given.Restart(-1),
            Given.Restart(-2),
            Given.Restart(-3));

        verdict.Quarantine.Should().BeFalse();
    }
}
