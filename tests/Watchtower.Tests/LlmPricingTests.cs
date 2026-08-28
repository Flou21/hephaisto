using Watchtower.Agent.Llm;

namespace Watchtower.Tests;

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
}
