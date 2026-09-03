using Hephaisto.Agent.Persistence;
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

    // ---------------------------------------------------------------------------------
    // The HOURLY pair, which had no test at all until v0.7.0. Backlog #74.
    // ---------------------------------------------------------------------------------
    //
    // The three above relate the per-investigation token ceiling to the per-investigation
    // step ceiling. Nothing related the hourly TOKEN cap to the hourly COST cap, and that
    // is the pair that refused 14 of 27 investigations at 2% of the money spent.
    //
    // A token cap and a cost cap standing side by side IMPLY A PRICE. If that implied price
    // is far above what the model actually charges, the token cap silently becomes the whole
    // budget and the cost cap is decoration - and because CheckAsync tests tokens first, the
    // block that gets reported names tokens while everybody is looking at the money.

    /// <summary>Blended per-token cost, on the 3:1 input:output ratio these runs show.</summary>
    private static decimal BlendedPerMillion(string model)
    {
        var price = new LlmOptions().Pricing[model];

        return ((price.InputPerMillionUsd * 3) + price.OutputPerMillionUsd) / 4;
    }

    private static decimal ImpliedPricePerMillion(LlmBudgetOptions budget) =>
        budget.MaxCostUsdPerHour / ((decimal)budget.MaxTokensPerHour / 1_000_000m);

    [Theory]
    [InlineData("gemini-3.7-flash")]
    [InlineData("gpt-oss-120b")]
    public void Cost_is_the_ceiling_that_binds_first_on_every_supported_model(string model)
    {
        // The property that was broken. Whichever model is configured, spending the hourly
        // TOKEN allowance must cost at least the hourly COST allowance - otherwise tokens run
        // out while the money is untouched, which is precisely what happened.
        var budget = new LlmBudgetOptions();

        var costOfSpendingEveryToken =
            (decimal)budget.MaxTokensPerHour / 1_000_000m * BlendedPerMillion(model);

        costOfSpendingEveryToken.Should().BeGreaterThanOrEqualTo(
            budget.MaxCostUsdPerHour,
            $"on {model}, exhausting MaxTokensPerHour costs ${costOfSpendingEveryToken:F2} against "
            + $"a ${budget.MaxCostUsdPerHour} hourly cost cap - so the token cap is the real "
            + "budget and the cost cap never binds. That is backlog #74, which refused 14 of 27 "
            + "investigations having spent $0.066");
    }

    [Fact]
    public void The_implied_price_is_not_far_above_what_the_cheapest_supported_model_charges()
    {
        // The direction the 2,000,000 default failed in, stated as the number rather than as
        // the story: $3.00 over 2M tokens implies $1.50/1M, which is 45x gpt-oss-120b's real
        // blended rate. Anything within an order of magnitude leaves cost governing.
        var implied = ImpliedPricePerMillion(new LlmBudgetOptions());

        implied.Should().BeLessThan(
            BlendedPerMillion("gpt-oss-120b") * 10,
            "the two hourly caps imply a price far above what the cheapest supported model "
            + "charges, so on that model the token cap binds long before the cost cap and the "
            + "budget stops meaning what values.yaml says it means");
    }

    [Fact]
    public void The_hourly_token_cap_still_bounds_a_runaway()
    {
        // The other direction, and it is not hypothetical: a model with no entry in the price
        // table bills as $0, so MaxCostUsdPerHour can never bind and this is the only backstop
        // left. "Make it enormous" is not available.
        var budget = new LlmBudgetOptions();

        // A full twelve-fixture corpus run is on the order of 4M tokens.
        budget.MaxTokensPerHour.Should().BeLessThan(100_000_000);
        budget.MaxTokensPerHour.Should().BeGreaterThan(4_000_000);
    }

    [Fact]
    public void An_hour_of_investigations_is_not_refused_before_the_daily_cost_cap_matters()
    {
        var budget = new LlmBudgetOptions();

        budget.MaxCostUsdPerDay.Should().BeGreaterThan(
            budget.MaxCostUsdPerHour,
            "a daily cap at or below the hourly one makes the hourly cap unreachable and the "
            + "daily one the only real control, which is not what either name says");
    }
}
