using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Parsing the judge's answer.
/// </summary>
/// <remarks>
/// The theme is that <b>silence is not a verdict</b>. Every failure path returns null rather than
/// a <c>correct: false</c>, because a judge that timed out, returned prose instead of JSON, or was
/// never given an API key has not said the agent was wrong. Collapsing those into "incorrect"
/// would let an unreachable judge quietly drive the reported score to zero.
/// </remarks>
public class RootCauseJudgeTests
{
    [Fact]
    public void A_well_formed_verdict_parses()
    {
        var verdict = GeminiRootCauseJudge.Parse(
            """{"correct": true, "reason": "It named the missing Secret."}""");

        verdict.Should().NotBeNull();
        verdict!.Correct.Should().BeTrue();
        verdict.Reason.Should().Be("It named the missing Secret.");
    }

    [Fact]
    public void A_negative_verdict_parses_and_is_distinguishable_from_silence()
    {
        var verdict = GeminiRootCauseJudge.Parse(
            """{"correct": false, "reason": "It restated the symptom."}""");

        verdict.Should().NotBeNull();
        verdict!.Correct.Should().BeFalse();
    }

    [Fact]
    public void Prose_instead_of_json_is_silence_not_a_negative_verdict()
    {
        // The model ignoring responseMimeType and answering in English must not be recorded as
        // "the agent was wrong".
        GeminiRootCauseJudge.Parse("The agent was broadly right, I think.").Should().BeNull();
    }

    [Fact]
    public void A_truncated_response_is_silence()
    {
        GeminiRootCauseJudge.Parse("""{"correct": tr""").Should().BeNull();
    }

    [Fact]
    public void A_missing_reason_still_parses_because_the_verdict_is_the_payload()
    {
        var verdict = GeminiRootCauseJudge.Parse("""{"correct": true}""");

        verdict.Should().NotBeNull();
        verdict!.Correct.Should().BeTrue();
        verdict.Reason.Should().BeEmpty();
    }
}
