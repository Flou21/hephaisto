using Hephaisto.Core.Domain;
using Hephaisto.Eval;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Folding one investigation, the judge and the replay accounting into a score.
/// </summary>
/// <remarks>
/// Two rules carry this file. A high miss rate invalidates the <i>instrument</i> and never
/// changes the verdict, and the judge is reported but can neither overturn the deterministic
/// verdict nor fail a run.
/// </remarks>
public class ScenarioScorerTests
{
    private static readonly AnswerKey C7 = AnswerKey.For("c7")!;

    private static Cassette Cassette(string id = "c7") => new()
    {
        Id = id,
        Description = "a Secret that does not exist",
        ExpectedRootCause = C7.ExpectedRootCause,
        Tools = [],
        Calls = [],
    };

    private static Investigation Correct()
    {
        var step = new InvestigationStep { Ordinal = 1, Kind = StepKind.ToolCall, ToolName = "get_events" };

        var finding = new Finding
        {
            Category = "config",
            Hypothesis = "The Secret c7-database-credentials does not exist.",
            Confidence = 0.9,
            IsPrimary = true,
        };

        finding.Evidence.Add(new Evidence
        {
            StepId = step.Id,
            Excerpt = "Error: secret \"c7-database-credentials\" not found",
        });

        var investigation = new Investigation
        {
            ModelId = "test",
            StartedAt = DateTimeOffset.UnixEpoch,
            StepsUsed = 9,
            CostUsd = 0.0123m,
            TerminationReason = TerminationReason.Concluded,
        };

        investigation.Steps.Add(step);
        investigation.Findings.Add(finding);

        return investigation;
    }

    private static ReplaySummary Replay(int total, int missed) => new()
    {
        Total = total,
        Exact = total - missed,
        Fuzzy = 0,
        Missed = missed,
        MissedTools = missed > 0 ? ["query_loki_logs"] : [],
    };

    [Fact]
    public void A_correct_diagnosis_carries_its_cost_and_termination_into_the_score()
    {
        var score = ScenarioScorer.Combine(Cassette(), C7, Correct(), Replay(10, 0));

        score.Fixture.Should().Be("c7");
        score.Verdict.Should().Be(RootCauseVerdict.Correct);
        score.StepsUsed.Should().Be(9);
        score.CostUsd.Should().Be(0.0123m);
        score.TerminationReason.Should().Be("Concluded");
        score.StructurallySound.Should().BeTrue();
    }

    [Fact]
    public void A_high_miss_rate_marks_the_run_unsound_without_touching_the_verdict()
    {
        // The distinction the whole design turns on: the model reached the right answer, but it
        // reached it while mostly talking to the harness. The answer is "re-record", not "the
        // agent is fine" and not "the agent got worse".
        var score = ScenarioScorer.Combine(Cassette(), C7, Correct(), Replay(10, 6));

        score.Verdict.Should().Be(RootCauseVerdict.Correct);
        score.StructurallySound.Should().BeFalse();
        score.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Fail && a.Name.Contains("replay covered"));
    }

    [Fact]
    public void A_few_misses_are_expected_and_do_not_invalidate_a_run()
    {
        // A change that redirects the investigation is supposed to ask questions the recording
        // has no answer to. Flagging that would make every successful experiment look broken.
        ScenarioScorer.Combine(Cassette(), C7, Correct(), Replay(10, 1))
            .StructurallySound.Should().BeTrue();
    }

    [Fact]
    public void A_disagreeing_judge_is_recorded_but_cannot_fail_the_run_or_flip_the_verdict()
    {
        var score = ScenarioScorer.Combine(
            Cassette(), C7, Correct(), Replay(10, 0),
            new JudgeVerdict(Correct: false, "It only restated the symptom."));

        score.Verdict.Should().Be(RootCauseVerdict.Correct);
        score.StructurallySound.Should().BeTrue();
        score.JudgeReason.Should().Be("It only restated the symptom.");

        // Skip, not Fail: a release must not be blocked by a second model having an opinion, and
        // in the shared report format a skip reads as "not established" - which is exactly what
        // two graders contradicting each other establishes.
        score.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Skip && a.Name.Contains("judge agrees"));
    }

    [Fact]
    public void An_agreeing_judge_passes()
    {
        var score = ScenarioScorer.Combine(
            Cassette(), C7, Correct(), Replay(10, 0),
            new JudgeVerdict(Correct: true, "It named the missing Secret."));

        score.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Pass && a.Name.Contains("judge agrees"));
    }

    [Fact]
    public void No_judge_is_a_skip_and_never_a_verdict()
    {
        var score = ScenarioScorer.Combine(Cassette(), C7, Correct(), Replay(10, 0));

        score.JudgeReason.Should().BeNull();
        score.StructurallySound.Should().BeTrue();
        score.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Skip && a.Detail.Contains("no judge ran"));
    }

    [Fact]
    public void A_no_finding_investigation_scores_no_finding_and_is_still_sound()
    {
        // The negative control. Producing nothing is the measurement, not a broken harness -
        // and it must be counted against the total rather than skipped away.
        var empty = new Investigation { ModelId = "test", StartedAt = DateTimeOffset.UnixEpoch };

        var score = ScenarioScorer.Combine(Cassette(), C7, empty, Replay(4, 0));

        score.Verdict.Should().Be(RootCauseVerdict.NoFinding);
        score.StructurallySound.Should().BeTrue();
    }

    [Fact]
    public void A_cassette_id_with_a_suffix_still_resolves_to_its_answer_key()
    {
        AnswerKey.ForCassette("c7-configerror")!.Fixture.Should().Be("c7");
    }

    [Fact]
    public void The_fixture_prefix_match_cannot_confuse_c1_with_c10()
    {
        // A "starts with" test would grade c10 against c1's answer key, which is the kind of bug
        // that produces a plausible number nobody can explain.
        AnswerKey.ForCassette("c10")!.Fixture.Should().Be("c10");
        AnswerKey.ForCassette("c1")!.Fixture.Should().Be("c1");
        AnswerKey.ForCassette("c10-sloburn")!.Fixture.Should().Be("c10");
    }

    [Fact]
    public void An_unknown_fixture_has_no_key_rather_than_a_wrong_one()
    {
        AnswerKey.ForCassette("c6").Should().BeNull();
        AnswerKey.ForCassette("nonsense").Should().BeNull();
    }

    [Fact]
    public void The_judge_is_sent_the_same_text_the_shell_harness_sends()
    {
        // Format copied from judge.sh. Two harnesses sending differently shaped diagnoses to the
        // same prompt produce two numbers that cannot be compared.
        var primary = Correct().Findings[0];

        GeminiRootCauseJudge.Describe(primary).Should().Be(
            "HYPOTHESIS: The Secret c7-database-credentials does not exist.\n"
            + "EVIDENCE: Error: secret \"c7-database-credentials\" not found");
    }

    [Fact]
    public void A_very_long_diagnosis_is_capped_where_the_shell_harness_caps_it()
    {
        var finding = new Finding
        {
            Category = "config",
            Hypothesis = new string('x', 5000),
            Confidence = 0.5,
            IsPrimary = true,
        };

        GeminiRootCauseJudge.Describe(finding).Length.Should().Be(4000);
    }
}
