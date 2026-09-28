namespace Hephaisto.Core.Classification;

/// <summary>
/// Whether an alert's rule wants it investigated (#134).
/// </summary>
/// <remarks>
/// Decided 2026-09-28: everything is investigated unless the rule says otherwise, with
/// <c>hephaisto_investigate: "false"</c> on the rule. There is deliberately no mode that inverts
/// the default - a rule nobody labelled is investigated, which fails expensive rather than silent.
/// Such an incident still opens and a person is still told; only the model is not asked.
/// </remarks>
public static class InvestigationPolicy
{
    public const string Label = "hephaisto_investigate";

    public static bool ShouldInvestigate(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        return !(labels.TryGetValue(Label, out var value)
            && value.Trim().ToLowerInvariant() is "false" or "no" or "0" or "off");
    }
}
