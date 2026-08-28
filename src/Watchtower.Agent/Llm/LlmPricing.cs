using Microsoft.Extensions.Logging;

namespace Watchtower.Agent.Llm;

/// <summary>
/// Turns token counts into dollars. Separated from the budget so the arithmetic can be
/// tested without a clock, a client or a database.
/// </summary>
/// <remarks>
/// A model with no price entry is charged <b>zero</b> and logged once as a warning, rather
/// than throwing. Refusing to investigate because a price list is stale would turn a
/// bookkeeping gap into an outage in the thing that diagnoses outages. The cost budget then
/// silently stops binding for that model, which is exactly what the warning is for - and the
/// step, token and wall-clock budgets still bound the run.
/// </remarks>
public sealed class LlmPricing(IReadOnlyDictionary<string, ModelPrice> prices, ILogger? logger = null)
{
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    public decimal CostOf(string? modelId, long inputTokens, long outputTokens)
    {
        if (string.IsNullOrWhiteSpace(modelId) || !TryResolve(modelId, out var price))
        {
            WarnOnce(modelId);
            return 0m;
        }

        return (inputTokens * price.InputPerMillionUsd / 1_000_000m)
            + (outputTokens * price.OutputPerMillionUsd / 1_000_000m);
    }

    /// <summary>
    /// Exact match first, then longest-prefix. Providers append dated suffixes
    /// (<c>gemini-2.5-pro-preview-06-05</c>) to a model whose price is the base model's, and
    /// a price list that has to be updated on every such rename is a price list that is wrong.
    /// </summary>
    private bool TryResolve(string modelId, out ModelPrice price)
    {
        if (prices.TryGetValue(modelId, out var exact))
        {
            price = exact;
            return true;
        }

        ModelPrice? best = null;
        var bestLength = 0;

        foreach (var (key, value) in prices)
        {
            if (key.Length > bestLength && modelId.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                best = value;
                bestLength = key.Length;
            }
        }

        price = best ?? new ModelPrice();
        return best is not null;
    }

    private void WarnOnce(string? modelId)
    {
        var key = modelId ?? "(null)";

        lock (_warned)
        {
            if (!_warned.Add(key))
            {
                return;
            }
        }

        logger?.LogWarning(
            "No price configured for model {ModelId}; its spend counts as $0 and the cost budget "
            + "will not bind for it. Add Llm:Pricing:{ModelId}.",
            key,
            key);
    }
}
