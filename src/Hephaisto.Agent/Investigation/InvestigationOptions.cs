namespace Hephaisto.Agent.Investigations;

public sealed class InvestigationOptions
{
    public const string SectionName = "Investigation";

    /// <summary>
    /// How long raw tool output is kept. Asymmetric with incident digests on purpose: blobs
    /// are ~1 MB and expire, digests are ~2 KB and are kept indefinitely, so history stays
    /// searchable long after the logs behind it are gone.
    /// </summary>
    public TimeSpan EvidenceBlobRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Outer iterations of the runner's own loop. Each one is a full
    /// <c>GetResponseAsync</c>, which internally runs as many model turns as the model wants
    /// tool iterations. The real bound is the step budget; this only stops a pathological
    /// nudge/stall cycle from spinning.
    /// </summary>
    public int MaxOuterTurns { get; set; } = 8;

    /// <summary>
    /// Below this, the primary finding's confidence escalates to a human rather than
    /// producing an auto-executable plan.
    /// </summary>
    public double MinConfidenceForPlan { get; set; } = 0.5;

    /// <summary>
    /// The planning call gets its own small budget rather than what is left of the
    /// investigation's.
    /// </summary>
    /// <remarks>
    /// A run that found the cause and then could not afford to say what to do about it has
    /// wasted everything it spent. Phase 2 is one schema-constrained call with no tools, so
    /// its cost is both small and predictable, which is exactly the case for reserving it
    /// rather than letting phase 1 spend it.
    /// </remarks>
    public decimal PlanningCostUsd { get; set; } = 0.10m;

    public TimeSpan PlanningTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public long PlanningMaxInputTokens { get; set; } = 200_000;

    /// <summary>Sent when a turn produced no tool call and no conclusion.</summary>
    public string StallNudge { get; set; } =
        "You did not call a tool and did not conclude. Either run the next query that could "
        + "change your answer, or call `conclude` with what you have - including "
        + "\"insufficient evidence\" if that is the honest answer.";

    /// <summary>
    /// Sent on the reserved final step, when the budget is gone and this is the last thing
    /// the model will ever be asked.
    /// </summary>
    /// <remarks>
    /// States plainly that no further queries are possible, because a model told merely to
    /// "wrap up" reliably proposes one more query. It also names the honest fallback: an
    /// investigation that ends "insufficient evidence, here is what was ruled out" is useful
    /// to whoever picks the incident up, whereas one that ends in silence is not.
    /// </remarks>
    public string FinalConclusionNudge { get; set; } =
        "Your investigation budget is now exhausted. This is your final turn and no further "
        + "tools are available - `conclude` is the only one you can call. State your best "
        + "conclusion from the evidence you have already gathered, citing the steps that "
        + "support it. If the evidence does not identify a cause, say so and record what you "
        + "ruled out; that is a useful answer and silence is not.";

    public string OpeningMessage { get; set; } =
        "Investigate the incident described in your instructions. Begin with the first move "
        + "your runbook specifies.";
}
