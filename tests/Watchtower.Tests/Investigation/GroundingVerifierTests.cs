using Watchtower.Agent.Investigations;
using Watchtower.Core.Domain;

namespace Watchtower.Tests.Investigations;

/// <summary>
/// The honesty mechanism of the whole system, so it is tested the way the policy engine is:
/// exhaustively, in memory, with no LLM anywhere near it.
/// </summary>
/// <remarks>
/// The cases that matter are not "does Contains work". They are the ones where a model
/// produces something that <i>looks</i> right - a tidied-up quote, a line from a different
/// investigation, a confident finding whose citations all failed - and the check has to
/// throw it away anyway.
/// </remarks>
public class GroundingVerifierTests
{
    private static readonly Guid Investigation = Guid.CreateVersion7();

    private const string Digest =
        """
        log digest: 42 lines, 9001 bytes

        -- notable lines --
        FATAL: could not connect to mongo: connection refused
        panic: runtime error: invalid memory address

        -- last 3 lines --
        starting worker pool
        listening on :8080
        """;

    private static InvestigationStep Step(
        Guid? investigationId = null,
        string? digest = Digest,
        string tool = "get_pod_logs") =>
        new()
        {
            InvestigationId = investigationId ?? Investigation,
            Kind = StepKind.ToolCall,
            ToolName = tool,
            ResultDigest = digest,
            Ordinal = 1,
        };

    private static Finding FindingCiting(params Evidence[] evidence)
    {
        var finding = new Finding
        {
            InvestigationId = Investigation,
            Category = "dependency",
            Hypothesis = "The container cannot reach mongo and exits.",
            Confidence = 0.85,
            IsPrimary = true,
        };

        finding.Evidence.AddRange(evidence);

        return finding;
    }

    private static Evidence Citing(InvestigationStep step, string excerpt) =>
        new() { StepId = step.Id, Excerpt = excerpt };

    // ------------------------------------------------------------------
    // Evidence
    // ------------------------------------------------------------------

    [Fact]
    public void Verbatim_excerpt_passes()
    {
        var step = Step();
        var finding = FindingCiting(Citing(step, "FATAL: could not connect to mongo: connection refused"));

        var result = GroundingVerifier.Verify(Investigation, [step], [finding]);

        result.Findings.Should().ContainSingle();
        result.Findings[0].Evidence.Should().ContainSingle();
        result.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Paraphrased_excerpt_fails()
    {
        var step = Step();

        // Every word of this is true and none of it was ever shown to the model. This is the
        // exact failure the check exists for: a model that hallucinates a plausible line will
        // also sincerely believe it quoted one.
        var finding = FindingCiting(Citing(step, "FATAL: unable to connect to mongo - connection was refused"));

        var result = GroundingVerifier.Verify(Investigation, [step], [finding]);

        result.Findings.Should().BeEmpty();
        result.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.ExcerptNotFound);
    }

    [Fact]
    public void Excerpt_from_a_different_investigations_step_fails()
    {
        var foreign = Step(investigationId: Guid.CreateVersion7());
        var finding = FindingCiting(Citing(foreign, "FATAL: could not connect to mongo: connection refused"));

        var result = GroundingVerifier.Verify(Investigation, [foreign], [finding]);

        result.Findings.Should().BeEmpty();
        result.Rejections.Should().ContainSingle(r => r.Reason == GroundingRejectionReason.ForeignStep);
    }

    [Fact]
    public void Excerpt_citing_a_step_that_does_not_exist_fails()
    {
        var step = Step();
        var finding = FindingCiting(new Evidence { StepId = Guid.CreateVersion7(), Excerpt = "listening on :8080" });

        var result = GroundingVerifier.Verify(Investigation, [step], [finding]);

        result.Rejections.Should().ContainSingle(r => r.Reason == GroundingRejectionReason.UnknownStep);
    }

    [Theory]
    [InlineData("FATAL:   could not connect to mongo:   connection refused")]
    [InlineData("  FATAL: could not connect to mongo: connection refused  ")]
    [InlineData("FATAL: could not connect to mongo:\n    connection refused")]
    [InlineData("FATAL: could not connect to mongo:\tconnection refused")]
    public void Whitespace_only_differences_pass(string excerpt)
    {
        // Reflowing a quote is an artefact of passing text through a JSON field, not a change
        // of meaning. Being strict here would turn honest citations into rejections and hide
        // the drift the metric exists to show.
        var step = Step();

        var result = GroundingVerifier.Verify(Investigation, [step], [FindingCiting(Citing(step, excerpt))]);

        result.Findings.Should().ContainSingle();
        result.Rejections.Should().BeEmpty();
    }

    [Theory]
    [InlineData("fatal: could not connect to mongo: connection refused")]
    [InlineData("FATAL: could not connect to MONGO: connection refused")]
    public void Case_differences_fail(string excerpt)
    {
        // ERROR and error are different log levels; INFO and Info are different loggers.
        // Case is meaning in this domain, so the comparison stays case-sensitive.
        var step = Step();

        var result = GroundingVerifier.Verify(Investigation, [step], [FindingCiting(Citing(step, excerpt))]);

        result.Rejections.Should().ContainSingle(r => r.Reason == GroundingRejectionReason.ExcerptNotFound);
    }

    [Fact]
    public void Empty_excerpt_fails()
    {
        var step = Step();

        var result = GroundingVerifier.Verify(Investigation, [step], [FindingCiting(Citing(step, "   "))]);

        result.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.EmptyExcerpt);
    }

    [Fact]
    public void Step_with_no_digest_cannot_be_cited()
    {
        var step = Step(digest: null, tool: "query_loki_logs");

        var result = GroundingVerifier.Verify(Investigation, [step], [FindingCiting(Citing(step, "anything"))]);

        result.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.NoDigest);
    }

    [Fact]
    public void Checks_against_the_digest_and_not_the_raw_blob()
    {
        // The model was shown a digest with the middle cut out. An excerpt from the cut
        // region is text it could not have read - true or not, it is not evidence, and
        // accepting it would defeat the whole mechanism.
        var step = Step(digest: "log digest: 2 lines\n[truncated: 998 of 1000 lines omitted]\nlistening on :8080");

        var fromTheCutRegion = FindingCiting(Citing(step, "FATAL: could not connect to mongo: connection refused"));

        var result = GroundingVerifier.Verify(Investigation, [step], [fromTheCutRegion]);

        result.Findings.Should().BeEmpty();
        result.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.ExcerptNotFound);
    }

    // ------------------------------------------------------------------
    // Findings
    // ------------------------------------------------------------------

    [Fact]
    public void A_finding_that_loses_all_its_evidence_is_dropped()
    {
        var step = Step();

        var finding = FindingCiting(
            Citing(step, "not in the digest at all"),
            Citing(step, "also not in the digest"));

        var result = GroundingVerifier.Verify(Investigation, [step], [finding]);

        result.Findings.Should().BeEmpty();
        result.HasGroundedFindings.Should().BeFalse();

        result.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.FindingWithoutEvidence);
        result.Rejections.Count(r => r.Reason == GroundingRejectionReason.ExcerptNotFound).Should().Be(2);
    }

    [Fact]
    public void A_finding_keeps_its_surviving_evidence_and_loses_the_rest()
    {
        var step = Step();

        var finding = FindingCiting(
            Citing(step, "panic: runtime error: invalid memory address"),
            Citing(step, "a line the model made up"));

        var result = GroundingVerifier.Verify(Investigation, [step], [finding]);

        result.Findings.Should().ContainSingle();
        result.Findings[0].Evidence.Should().ContainSingle()
            .Which.Excerpt.Should().Be("panic: runtime error: invalid memory address");
    }

    [Fact]
    public void Dropping_the_primary_finding_promotes_the_most_confident_survivor()
    {
        // The domain rule is "exactly zero or one primary per investigation", and the
        // planning phase is built around there being one. Returning a set with none reads
        // downstream as "no findings at all", which is a different and wrong conclusion.
        var step = Step();

        var invented = FindingCiting(Citing(step, "a line that was never returned"));
        invented.IsPrimary = true;

        var weak = FindingCiting(Citing(step, "listening on :8080"));
        weak.IsPrimary = false;
        weak.Confidence = 0.3;

        var strong = FindingCiting(Citing(step, "starting worker pool"));
        strong.IsPrimary = false;
        strong.Confidence = 0.7;

        var result = GroundingVerifier.Verify(Investigation, [step], [invented, weak, strong]);

        result.Findings.Should().HaveCount(2);
        result.Findings.Should().ContainSingle(f => f.IsPrimary)
            .Which.Confidence.Should().Be(0.7);
    }

    [Fact]
    public void Verification_is_scoped_to_one_investigation_even_when_handed_more_steps()
    {
        var mine = Step();
        var theirs = Step(investigationId: Guid.CreateVersion7());

        var finding = FindingCiting(
            Citing(mine, "listening on :8080"),
            Citing(theirs, "listening on :8080"));

        var result = GroundingVerifier.Verify(Investigation, [mine, theirs], [finding]);

        result.Findings.Should().ContainSingle();
        result.Findings[0].Evidence.Should().ContainSingle()
            .Which.StepId.Should().Be(mine.Id);
        result.Rejections.Should().ContainSingle(r => r.Reason == GroundingRejectionReason.ForeignStep);
    }

    // ------------------------------------------------------------------
    // Plans
    // ------------------------------------------------------------------

    [Fact]
    public void A_plan_citing_a_dropped_finding_is_rejected()
    {
        var surviving = FindingCiting();

        var draft = new ActionPlanDraft
        {
            Summary = "Restart it.",
            Actions =
            [
                new ActionDraft
                {
                    Type = ActionType.RolloutRestart,
                    EvidenceFindingIds = [Guid.CreateVersion7().ToString()],
                },
            ],
        };

        var result = GroundingVerifier.VerifyPlan(draft, [surviving]);

        result.Accepted.Should().BeFalse();
        result.Rejections.Should().ContainSingle(
            r => r.Reason == GroundingRejectionReason.ActionCitesDroppedFinding);
    }

    [Fact]
    public void A_plan_citing_a_surviving_finding_is_accepted()
    {
        var surviving = FindingCiting();

        var draft = new ActionPlanDraft
        {
            Summary = "Restart it.",
            Actions =
            [
                new ActionDraft
                {
                    Type = ActionType.RolloutRestart,
                    EvidenceFindingIds = [surviving.Id.ToString()],
                },
            ],
        };

        GroundingVerifier.VerifyPlan(draft, [surviving]).Accepted.Should().BeTrue();
    }

    [Fact]
    public void An_action_citing_nothing_is_rejected()
    {
        var draft = new ActionPlanDraft
        {
            Actions = [new ActionDraft { Type = ActionType.RestartPod }],
        };

        var result = GroundingVerifier.VerifyPlan(draft, [FindingCiting()]);

        result.Accepted.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Reason == GroundingRejectionReason.ActionWithoutEvidence);
    }

    [Fact]
    public void A_no_action_plan_needs_no_evidence()
    {
        // "Do nothing" is the correct answer for most incidents and needs nothing to justify
        // it. Requiring evidence to do nothing would push the model towards proposing
        // something.
        var draft = new ActionPlanDraft { NoActionRequired = true, Summary = "Transient; already recovering." };

        var result = GroundingVerifier.VerifyPlan(draft, []);

        result.Accepted.Should().BeTrue();
        result.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void An_unparseable_finding_id_is_rejected_rather_than_ignored()
    {
        var draft = new ActionPlanDraft
        {
            Actions =
            [
                new ActionDraft { Type = ActionType.ScaleWorkload, EvidenceFindingIds = ["finding-1"] },
            ],
        };

        GroundingVerifier.VerifyPlan(draft, [FindingCiting()]).Accepted.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Normalisation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("  a   b  ", "a b")]
    [InlineData("a\n\nb", "a b")]
    [InlineData("\t a \r\n b \t", "a b")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("single", "single")]
    public void Normalise_collapses_whitespace_and_trims(string input, string expected) =>
        GroundingVerifier.Normalise(input).Should().Be(expected);
}
