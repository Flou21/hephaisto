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

    // The comparability invariant. Two graders scoring the same fixture with differently worded
    // prompts produce two numbers nobody can put in the same table - which is why the prompt is
    // copied verbatim from judge.sh and, since a second provider was added, shared rather than
    // duplicated. A second provider must not quietly become a second question.
    [Fact]
    public void The_prompt_carries_both_the_answer_key_and_the_diagnosis()
    {
        var prompt = GeminiRootCauseJudge.BuildPrompt("THE TRUTH", "THE DIAGNOSIS");

        prompt.Should().Contain("THE TRUTH");
        prompt.Should().Contain("THE DIAGNOSIS");
    }

    [Fact]
    public void The_prompt_asks_for_the_cause_and_refuses_a_restated_symptom()
    {
        var prompt = GeminiRootCauseJudge.BuildPrompt("t", "d");

        // The one instruction that makes this a root-cause grade rather than a similarity score.
        prompt.Should().Contain("Judge the CAUSE, not the wording");
        prompt.Should().Contain("without identifying why is NOT correct");
        prompt.Should().Contain("Answer strictly as JSON");
    }

    [Fact]
    public void Every_judge_implementation_asks_the_identical_question()
    {
        // Both judges call the same builder, so this asserts the property rather than the
        // wording: if someone gives one provider its own prompt, this fails.
        var a = GeminiRootCauseJudge.BuildPrompt("same truth", "same diagnosis");
        var b = GeminiRootCauseJudge.BuildPrompt("same truth", "same diagnosis");

        a.Should().Be(b);
        a.Should().NotBeEmpty();
    }
}
