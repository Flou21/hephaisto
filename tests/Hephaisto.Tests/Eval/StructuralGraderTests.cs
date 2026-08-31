using Hephaisto.Core.Domain;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// The deterministic half of grading, and the arithmetic of the headline number.
/// </summary>
/// <remarks>
/// <see cref="A_run_scores_against_scenarios_so_losing_findings_cannot_raise_the_score"/> is the
/// test this whole design exists for. Every other test here checks a rule; that one checks that
/// the score cannot be gamed by the agent getting worse.
/// </remarks>
public class StructuralGraderTests
{
    private static readonly AnswerKey C7 = AnswerKey.For("c7")!;

    private static Investigation WithFinding(
        string hypothesis,
        string category = "config",
        string? excerpt = null,
        bool primary = true,
        bool citeRealStep = true)
    {
        var step = new InvestigationStep { Ordinal = 1, Kind = StepKind.ToolCall, ToolName = "get_events" };

        var finding = new Finding
        {
            Category = category,
            Hypothesis = hypothesis,
            Confidence = 0.8,
            IsPrimary = primary,
        };

        if (excerpt is not null)
        {
            finding.Evidence.Add(new Evidence
            {
                StepId = citeRealStep ? step.Id : Guid.CreateVersion7(),
                Excerpt = excerpt,
            });
        }

        var investigation = new Investigation { ModelId = "test", StartedAt = DateTimeOffset.UnixEpoch };
        investigation.Steps.Add(step);
        investigation.Findings.Add(finding);

        return investigation;
    }

    private static Investigation WithNoFinding()
    {
        var investigation = new Investigation { ModelId = "test", StartedAt = DateTimeOffset.UnixEpoch };
        investigation.Steps.Add(new InvestigationStep { Ordinal = 1, Kind = StepKind.ToolCall });
        return investigation;
    }

    // ------------------------------------------------------------------ verdicts

    [Fact]
    public void A_diagnosis_that_names_the_broken_thing_is_correct()
    {
        var grade = StructuralGrader.Grade(
            WithFinding(
                "The Secret c7-database-credentials does not exist, so the kubelet cannot build the env.",
                excerpt: "Error: secret \"c7-database-credentials\" not found"),
            C7);

        grade.Verdict.Should().Be(RootCauseVerdict.Correct);
        grade.Assertions.Should().NotContain(a => a.Status == EvalStatus.Fail);
    }

    [Fact]
    public void Restating_the_symptom_is_incorrect_and_is_not_skipped_away()
    {
        // The negative control. "CreateContainerConfigError" is the symptom; the judge prompt says
        // restating it without saying why is NOT correct, and the deterministic pass agrees.
        var grade = StructuralGrader.Grade(
            WithFinding(
                "The pod is in CreateContainerConfigError and will not start.",
                excerpt: "Back-off restarting failed container"),
            C7);

        grade.Verdict.Should().Be(RootCauseVerdict.Incorrect);
    }

    [Fact]
    public void Naming_the_broken_thing_in_an_evidence_excerpt_counts()
    {
        // A hypothesis that says "a referenced Secret is missing" and quotes the event naming it
        // has identified the cause. The excerpt is part of the diagnosis, not decoration.
        var grade = StructuralGrader.Grade(
            WithFinding(
                "A referenced Secret does not exist, so the container environment cannot be built.",
                excerpt: "Error: secret \"c7-database-credentials\" not found"),
            C7);

        grade.Verdict.Should().Be(RootCauseVerdict.Correct);
    }

    [Fact]
    public void An_investigation_with_no_primary_finding_is_no_finding_not_a_skip()
    {
        var grade = StructuralGrader.Grade(WithNoFinding(), C7);

        grade.Verdict.Should().Be(RootCauseVerdict.NoFinding);
        grade.Hypothesis.Should().BeNull();

        // It must not read as a broken harness: nothing failed, the agent simply did not conclude.
        grade.Assertions.Should().NotContain(a => a.Status == EvalStatus.Fail);
    }

    // ------------------------------------------------------------------ invariants

    [Fact]
    public void A_primary_finding_with_no_evidence_fails_an_invariant()
    {
        var grade = StructuralGrader.Grade(
            WithFinding("The Secret c7-database-credentials is missing.", excerpt: null),
            C7);

        grade.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Fail && a.Name.Contains("cites evidence"));
    }

    [Fact]
    public void A_citation_naming_a_step_from_another_investigation_fails_an_invariant()
    {
        var grade = StructuralGrader.Grade(
            WithFinding(
                "The Secret c7-database-credentials is missing.",
                excerpt: "Error: secret \"c7-database-credentials\" not found",
                citeRealStep: false),
            C7);

        grade.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Fail && a.Name.Contains("citation resolves"));
    }

    [Fact]
    public void A_category_outside_the_published_eight_fails_an_invariant()
    {
        var grade = StructuralGrader.Grade(
            WithFinding(
                "The Secret c7-database-credentials is missing.",
                category: "misconfiguration",
                excerpt: "Error: secret \"c7-database-credentials\" not found"),
            C7);

        grade.Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Fail && a.Name.Contains("category"));
    }

    [Fact]
    public void Two_primary_findings_fail_an_invariant()
    {
        var investigation = WithFinding(
            "The Secret c7-database-credentials is missing.",
            excerpt: "Error: secret \"c7-database-credentials\" not found");

        investigation.Findings.Add(new Finding
        {
            Category = "config",
            Hypothesis = "Something else entirely.",
            Confidence = 0.9,
            IsPrimary = true,
        });

        StructuralGrader.Grade(investigation, C7).Assertions.Should().Contain(a =>
            a.Status == EvalStatus.Fail && a.Name.Contains("at most one primary"));
    }

    // ------------------------------------------------------------------ the arithmetic

    [Fact]
    public void A_run_scores_against_scenarios_so_losing_findings_cannot_raise_the_score()
    {
        var one = new ScenarioScore
        {
            Fixture = "c7", Verdict = RootCauseVerdict.Correct, Assertions = [],
        };

        var lost = new ScenarioScore
        {
            Fixture = "c3", Verdict = RootCauseVerdict.NoFinding, Assertions = [],
        };

        var run = new RunScore { Label = "test", Scenarios = [one, lost] };

        // The bug this guards against: scoring `correct / graded` would call this 1/1 - a perfect
        // score for an agent that failed to diagnose half the corpus. Both existing instruments
        // skip no-finding, so both would report exactly that.
        run.Total.Should().Be(2);
        run.Correct.Should().Be(1);
        run.NoFinding.Should().Be(1);
        run.ToString().Should().Contain("1/2");
    }

    [Fact]
    public void The_answer_key_covers_the_ten_gradeable_fixtures_and_omits_c6_and_c9()
    {
        AnswerKey.All.Should().HaveCount(10);
        AnswerKey.All.Select(k => k.Fixture).Should().BeEquivalentTo(
            ["c1", "c2", "c3", "c4", "c5", "c7", "c8", "c10", "c11", "c12"]);

        // c6 cannot fire on local-path and c9 is node-wide; neither is gradeable, and pretending
        // otherwise is how a corpus of 8 gets reported as 10.
        AnswerKey.For("c6").Should().BeNull();
        AnswerKey.For("c9").Should().BeNull();
    }

    [Fact]
    public void The_fixtures_that_expect_the_agent_to_act_are_the_transient_ones()
    {
        // Every key in this corpus had AcceptableActions empty until c11, which means
        // PlanGrader.MissedAnAction - "proposed nothing where an action was available" - had
        // never been reachable by any scenario. An eval where declining is always correct
        // measures one direction of a two-directional behaviour, and cannot see the failure
        // docs/backlog.md #41 records.
        //
        // This is not a cap. If a later fixture is one an action genuinely answers, raise it
        // and say so - the assertion exists so that going back to zero is loud.
        //
        // Two of them, and they are the same fault at different difficulties: c11 hides the
        // hinge behind an emptyDir marker gating a PVC counter, c12 puts one comparison in
        // plain sight. #41 is the measurement that made the second one necessary.
        AnswerKey.All.Where(k => k.AcceptableActions.Count > 0)
            .Select(k => k.Fixture)
            .Should().BeEquivalentTo(["c11", "c12"]);

        // And the other direction stays asserted: the four fixtures a restart would answer
        // plausibly and wrongly still forbid it.
        AnswerKey.All.Where(k => k.MustNotPropose.Contains(ActionType.RestartPod))
            .Select(k => k.Fixture)
            .Should().BeEquivalentTo(["c1", "c2", "c3", "c4"]);
    }

    [Fact]
    public void Every_answer_key_names_a_real_signal_kind()
    {
        // scripts/e2e/lib/chaos.sh maps c10 to "SloBurn", which is not a member of SignalKind, so
        // that assertion can never match. Typing this key as the enum makes the same mistake
        // impossible here.
        AnswerKey.All.Should().OnlyContain(k => Enum.IsDefined(k.ExpectedKind));
    }
}
