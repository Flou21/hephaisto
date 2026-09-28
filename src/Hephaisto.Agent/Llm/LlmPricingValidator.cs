using Hephaisto.Agent.Persistence;

using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The options type the startup price check hangs off. It carries nothing.
/// </summary>
/// <remarks>
/// The check is not a validator of <see cref="LlmOptions"/> itself because a validator runs
/// wherever those options are first read, and the eval harness and a dozen tests read them in a
/// container that never starts a host, with models nobody priced on purpose. A marker validated
/// with <c>ValidateOnStart</c> makes the check exactly what it should be: a refusal to START.
/// </remarks>
public sealed class LlmPricingCheck;

/// <summary>
/// Refuses to start when a cost cap is configured and the model it would be enforced on has no
/// price (backlog #140).
/// </summary>
/// <remarks>
/// <para>
/// A model with no price bills as zero, so every cost cap - hourly, daily, per incident and per
/// investigation - silently stops binding while the console reports 0.0% utilisation. That used
/// to be a warning in the log. It was the only thing standing between the number of alerts and
/// the bill, and it read as success.
/// </para>
/// <para>
/// An explicit price of 0 passes: a model that really is free - a local one - is a legitimate
/// configuration, and saying so in a values file is exactly the acknowledgement this asks for.
/// A cap of 0 does not switch the check off, because a cap of 0 does not mean "no cap": it
/// blocks every call.
/// </para>
/// </remarks>
public sealed class LlmPricingValidator(
    IOptions<LlmOptions> llm,
    IOptions<LlmBudgetOptions> budget) : IValidateOptions<LlmPricingCheck>
{
    public ValidateOptionsResult Validate(string? name, LlmPricingCheck options)
    {
        var o = llm.Value;
        var b = budget.Value;

        var capped = b.MaxCostUsdPerHour > 0
            || b.MaxCostUsdPerDay > 0
            || b.MaxCostUsdPerIncident > 0
            || o.Investigation.MaxCostUsd > 0;

        if (!capped)
        {
            return ValidateOptionsResult.Success;
        }

        var unpriced = new[] { o.Model, o.PlanningModelId }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => !LlmPricing.Resolves(o.Pricing, m))
            .ToArray();

        return unpriced.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"No price for {string.Join(" or ", unpriced.Select(m => $"'{m}'"))}, so the cost "
                + "caps can never bind: an unpriced model bills as $0. Add it to the chart value "
                + "llm.pricing (env Llm__Prices__N__Model, Llm__Prices__N__InputPerMillionUsd, "
                + "Llm__Prices__N__OutputPerMillionUsd). A price of 0 is accepted for a model that "
                + "really is free.");
    }
}
