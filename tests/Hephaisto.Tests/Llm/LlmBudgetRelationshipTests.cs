using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// The five ceilings are not independent, and treating them as independent has now cost two
/// milestones.
/// </summary>
/// <remarks>
/// <para>
/// A conversation is resent in full on every turn, so cumulative input tokens grow with the
/// <b>square</b> of the step count. Raising <c>MaxSteps</c> therefore does not buy more
/// investigation unless <c>MaxInputTokens</c> moves with it - it just relocates the ceiling
/// that binds. That is what happened: backlog #59 raised steps from 12 to 20 for
/// openai-compatible providers, #82 found the token ceiling binding instead, and #88 measured
/// what it cost - 5 of 12 c12 replays terminated early with no finding at all, each one
/// recorded as the planner proposing nothing.
/// </para>
/// <para>
/// So the relationship is asserted rather than commented. This is a test about a pair of
/// numbers and it exists because both numbers are individually plausible.
/// </para>
/// </remarks>
public class LlmBudgetRelationshipTests
{
    /// <summary>
    /// Tokens of transcript each additional turn carries, measured on c12 replay against
    /// gpt-oss-120b: 414,484 cumulative input at 17 steps is 414,484 / (17*18/2) = 2,709.
    /// </summary>
    private const long TokensPerTurnOfGrowth = 2_700;

    private static long CumulativeInputFor(int steps) => (long)steps * (steps + 1) / 2 * TokensPerTurnOfGrowth;

    [Fact]
    public void The_token_ceiling_cannot_bind_before_the_step_ceiling_at_the_shipped_defaults()
    {
        var budget = new InvestigationBudgetOptions();

        budget.MaxInputTokens.Should().BeGreaterThan(
            CumulativeInputFor(budget.MaxSteps),
            "a run that spends every permitted step must not breach the token ceiling doing it - "
            + "otherwise the token ceiling is the real step ceiling, and it reports a different "
            + "TerminationReason than the one that actually bound");
    }

    /// <summary>
    /// 20 is not a default, it is what <c>scripts/e2e/lib/deploy.sh</c> sets for every
    /// openai-compatible provider, so it is the value the corpus is actually measured at.
    /// </summary>
    [Fact]
    public void The_token_ceiling_also_covers_the_step_ceiling_the_e2e_raises_it_to()
    {
        new InvestigationBudgetOptions().MaxInputTokens
            .Should().BeGreaterThan(CumulativeInputFor(20));
    }

    /// <summary>
    /// The other direction, so the fix cannot be "make it enormous". A ceiling that no run
    /// could ever reach is not a ceiling, and this one is a safety control.
    /// </summary>
    [Fact]
    public void The_token_ceiling_still_binds_somewhere_reachable()
    {
        new InvestigationBudgetOptions().MaxInputTokens
            .Should().BeLessThan(CumulativeInputFor(40));
    }
}
