using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests;

/// <summary>
/// The configured models must have prices.
/// </summary>
/// <remarks>
/// An unpriced model does not make the budget approximate - it switches the cost cap off.
/// Every window multiplies tokens by a price of zero, so no cap is ever reached, while
/// <c>/status</c> keeps showing 0.0% utilisation and looks healthy. The only signal is one
/// warning line at startup, which is exactly the kind of thing nobody sees.
///
/// Since changing the model is a one-line config edit and the price lives somewhere else,
/// these two drift apart by default. This test is what makes them drift together.
/// </remarks>
public class LlmPricingTests
{
    private static readonly LlmOptions Defaults = new();

    [Fact]
    public void The_configured_investigation_model_has_a_price()
    {
        Defaults.Pricing.Should().ContainKey(
            Defaults.Model,
            "an unpriced model silently disables the cost budget rather than approximating it");
    }

    [Fact]
    public void The_configured_planning_model_has_a_price() =>
        Defaults.Pricing.Should().ContainKey(Defaults.PlanningModelId);

    [Fact]
    public void The_configured_embedding_model_has_a_price() =>
        Defaults.Pricing.Should().ContainKey(Defaults.EmbeddingModel);

    /// <summary>A zero price is indistinguishable from a missing one at spend time.</summary>
    [Fact]
    public void No_chat_model_is_priced_at_zero_input()
    {
        foreach (var (model, price) in Defaults.Pricing)
        {
            if (model.Contains("embedding", StringComparison.OrdinalIgnoreCase))
            {
                // Embeddings legitimately have no output price - there are no output tokens.
                continue;
            }

            price.InputPerMillionUsd.Should().BeGreaterThan(0, $"{model} must cost something to read");
            price.OutputPerMillionUsd.Should().BeGreaterThan(0, $"{model} must cost something to write");
        }
    }

    /// <summary>
    /// Pins the choice made on 2026-08-28 and the reason: the flash line was ahead of the pro
    /// line, so Pro would have meant an older model at a higher price. If someone moves this
    /// to a pro model, that should be a deliberate edit to this test too.
    /// </summary>
    [Fact]
    public void Defaults_to_a_flash_model_for_local_use()
    {
        Defaults.Model.Should().Contain("flash");

        var flash = Defaults.Pricing[Defaults.Model];
        var pro = Defaults.Pricing["gemini-2.5-pro"];

        flash.InputPerMillionUsd.Should().BeLessThan(pro.InputPerMillionUsd);
        flash.OutputPerMillionUsd.Should().BeLessThan(pro.OutputPerMillionUsd);
    }

    /// <summary>
    /// The embedding width is a database column, not a preference: incident_digests.embedding
    /// is vector(768) and a wider vector does not go into it. Changing this is a migration.
    /// </summary>
    [Fact]
    public void Embedding_dimensions_match_the_pgvector_column() =>
        Defaults.EmbeddingDimensions.Should().Be(768);

    /// <summary>
    /// The ids an OpenAI-compatible provider actually returns must resolve to a price.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BudgetGuardChatClient"/> prices <c>response.ModelId</c> before the
    /// configured id, so what matters is the string the provider sends back - and the same
    /// weights are named differently by each host: OpenRouter answers
    /// <c>openai/gpt-oss-120b</c>, Ollama answers <c>gpt-oss:120b</c>. A price list keyed on
    /// the tidy name would resolve none of them.
    /// </para>
    /// <para>
    /// This is the one failure in this area that looks like success. Nothing errors: the run
    /// completes, the cost cap never binds because every window multiplies by zero, and the
    /// console reports 0.0% utilisation the whole way.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("openai/gpt-oss-120b")]        // OpenRouter
    [InlineData("openai/gpt-oss-120b:free")]   // OpenRouter, suffixed variant
    [InlineData("gpt-oss:120b")]               // Ollama
    [InlineData("gpt-oss-120b")]               // bare, several hosts
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek-v4-flash-vision-exp")]
    [InlineData("deepseek-v4-pro")]
    public void Provider_returned_model_ids_resolve_to_a_non_zero_price(string modelId)
    {
        var pricing = new LlmPricing(Defaults.Pricing);

        pricing.CostOf(modelId, inputTokens: 1_000_000, outputTokens: 1_000_000)
            .Should()
            .BeGreaterThan(
                0m,
                "an id that does not resolve is charged at zero, which switches the cost "
                + "budget off rather than making it approximate");
    }

}
