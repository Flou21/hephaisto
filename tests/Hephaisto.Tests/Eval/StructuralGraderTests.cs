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
    public void The_answer_key_covers_the_twelve_gradeable_fixtures_and_omits_c6_and_c9()
    {
        AnswerKey.All.Should().HaveCount(12);
        AnswerKey.All.Select(k => k.Fixture).Should().BeEquivalentTo(
            ["c1", "c2", "c3", "c4", "c5", "c7", "c8", "c10", "c11", "c12", "c13", "c14"]);

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
        // Three of them, and they are the same fault at three difficulties. c11 hides the hinge
        // behind an emptyDir marker gating a PVC counter. c12 puts one comparison in plain
        // sight - but its state is still on a PVC, so acting means overriding the (correct)
        // rule that PVC contents survive a replacement, and backlog #89 measured gpt-oss-120b
        // failing that override 7 times in 9 when asked point blank. c13 puts the state on an
        // emptyDir, where the rule 30-planning.md already states is sufficient and there is
        // nothing to override.
        //
        // That progression is the point: a decline on c11 or c12 is ambiguous between "will
        // not act" and "did not make the inference", and only c13 separates them. Adding it
        // is not widening AcceptableActions on a fixture that was failing - c11 and c12 are
        // untouched and keep reporting what they report. See #90.
        //
        // v0.7.0 adds two, and they widen this in a direction the three above could not.
        //
        // c14 is the FIRST FIXTURE WHERE A RESTART IS THE WRONG ANSWER. All three above accept
        // [RestartPod, RolloutRestart], so every acting number this project has ever quoted was
        // of one action type against faults a restart genuinely fixes - which made "willing to
        // act" and "chose the right action" the same measurement. c14 accepts only
        // RollbackDeployment, and RolloutRestart is excluded deliberately rather than
        // forgotten: restarting its pods replaces them with more pods running the same bad
        // revision, so accepting it would let a model score by reaching for the tool it has.
        //
        // c5 is a much smaller thing: it already existed, is the obvious DeleteStuckJob /
        // DeleteFailedJobPods fixture, and simply had no AcceptableActions - so it could never
        // score an action however good the plan was.
        AnswerKey.All.Where(k => k.AcceptableActions.Count > 0)
            .Select(k => k.Fixture)
            .Should().BeEquivalentTo(["c5", "c11", "c12", "c13", "c14"]);

        // The corpus no longer measures a single action type. Four of the six executable
        // ActionTypes had no fixture at all before this; these three cover three of them.
        AnswerKey.All.SelectMany(k => k.AcceptableActions).Distinct()
            .Should().Contain([
                ActionType.RestartPod,
                ActionType.RolloutRestart,
                ActionType.RollbackDeployment,
                ActionType.DeleteStuckJob,
                ActionType.DeleteFailedJobPods,
            ]);

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
