using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// The arithmetic of an experiment arm.
/// </summary>
/// <remarks>
/// An arm is several passes over the corpus, because there is a language model in the loop and
/// one pass is one sample. These tests pin how the passes add up - which is the number two arms
/// get compared on, and therefore the number every conclusion in the roadmap will rest on.
/// </remarks>
public class RunReportTests
{
    private static ScenarioScore Score(
        string fixture,
        RootCauseVerdict verdict,
        int steps = 10,
        bool sound = true) => new()
        {
            Fixture = fixture,
            Verdict = verdict,
            StepsUsed = steps,
            CostUsd = 0.01m,
            Assertions = sound
                ? [EvalRecord.Pass("structure", "ok")]
                : [EvalRecord.Fail("structure", "citation resolves", "dangling")],
        };

    private static RunReport TwoPasses() => new()
    {
        Label = "baseline",
        StartedAt = DateTimeOffset.UnixEpoch,
        Passes =
        [
            new RunScore
            {
                Label = "baseline pass 1",
                Scenarios =
                [
                    Score("c4", RootCauseVerdict.Correct, steps: 8),
                    Score("c7", RootCauseVerdict.NoFinding, steps: 13),
                ],
            },
            new RunScore
            {
                Label = "baseline pass 2",
                Scenarios =
                [
                    Score("c4", RootCauseVerdict.Correct, steps: 6),
                    Score("c7", RootCauseVerdict.Incorrect, steps: 13),
                ],
            },
        ],
    };

    [Fact]
    public void The_denominator_is_every_attempt_across_every_pass()
    {
        var report = TwoPasses();

        // Two fixtures times two passes. Scoring against the fixtures instead would report
        // 2/2 here and hide that half the attempts produced nothing usable.
        report.Total.Should().Be(4);
        report.Correct.Should().Be(2);
        report.Incorrect.Should().Be(1);
        report.NoFinding.Should().Be(1);
        report.ToString().Should().Contain("2/4");
    }

    [Fact]
    public void A_fixture_that_answers_inconsistently_is_visible_per_fixture()
    {
        // The reason per-fixture tallies exist: a change that helps one scenario and breaks
        // another leaves the headline unmoved, and only this breakdown shows it happened.
        var byFixture = TwoPasses().ByFixture;

        byFixture.Should().HaveCount(2);

        var c7 = byFixture.Single(f => f.Fixture == "c7");

        c7.Attempts.Should().Be(2);
        c7.Correct.Should().Be(0);
        c7.Incorrect.Should().Be(1);
        c7.NoFinding.Should().Be(1);
    }

    [Fact]
    public void Mean_steps_are_reported_because_accuracy_bought_with_steps_is_not_free()
    {
        // Raising the step budget will improve accuracy by spending more. Without this axis the
        // report cannot tell that from a change that made the agent better.
        TwoPasses().ByFixture.Single(f => f.Fixture == "c4").MeanSteps.Should().Be(7);
    }

    [Fact]
    public void Soundness_is_counted_separately_from_correctness()
    {
        var report = new RunReport
        {
            Label = "x",
            StartedAt = DateTimeOffset.UnixEpoch,
            Passes =
            [
                new RunScore
                {
                    Label = "x pass 1",
                    Scenarios =
                    [
                        Score("c4", RootCauseVerdict.Correct),
                        Score("c7", RootCauseVerdict.Correct, sound: false),
                    ],
                },
            ],
        };

        // Both correct, but one attempt's assertions did not hold - so the score is 2/2 and the
        // soundness count says one of those two verdicts should not be trusted.
        report.Correct.Should().Be(2);
        report.Sound.Should().Be(1);
    }

    [Fact]
    public void An_arm_records_the_settings_it_ran_with()
    {
        // A number with no settings beside it is a number nobody can reproduce next week, which
        // is the exact failure this harness was built to stop repeating.
        var report = TwoPasses() with { Overrides = ["Llm:Investigation:MaxSteps=20"] };

        report.Overrides.Should().ContainSingle().Which.Should().Contain("MaxSteps=20");
    }
}
