namespace Hephaisto.Agent.Llm;

/// <summary>
/// Everything under <c>Llm:</c> except <c>Llm:Budget</c>, which the persistence stream owns
/// (<see cref="Persistence.LlmBudgetOptions"/>) because those are the *global* rolling
/// windows counted in Postgres. The two are deliberately separate: this file caps one
/// investigation, that one caps the process.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Selects the <see cref="IChatClientFactory"/> implementation. Only <c>gemini</c> ships
    /// today; the indirection exists so swapping provider is a ConfigMap edit rather than a
    /// code change, which matters when a provider is the outage.
    /// </summary>
    public string Provider { get; set; } = "gemini";

    /// <summary>The investigating model. Tool-calling quality dominates here.</summary>
    /// <remarks>
    /// <para>
    /// Flash rather than Pro, and not only to save money. As of 2026-08 the Gemini flash line
    /// is <b>ahead</b> of the pro line: 3.7 Flash is generally available while the newest pro
    /// is gemini-3.1-pro-preview. Choosing 2.5 Pro to be "the serious one" would mean running
    /// a model two generations older AND paying more for it - 3.7 Flash is $0.75/$3.75 per
    /// million against 2.5 Pro's $1.25/$10.00.
    /// </para>
    /// <para>
    /// For production, override <c>Llm:Model</c> rather than editing this. The two candidates
    /// are gemini-3.1-pro-preview ($2.00/$12.00, and preview means it can change underneath
    /// you) and whatever pro model has gone GA by then. Do not promote a model without adding
    /// its price below first - see the note on <see cref="Pricing"/>.
    /// </para>
    /// </remarks>
    public string Model { get; set; } = "gemini-3.7-flash";

    /// <summary>
    /// The planning model. Separate key because phase 2 is a single schema-constrained call
    /// with no tools - a smaller, cheaper model is often the right answer, and being able to
    /// say so without touching phase 1 is the point.
    /// </summary>
    public string? PlanningModel { get; set; }

    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>
    /// Must match the pgvector column width in <c>incident_digests.embedding</c>. Changing it
    /// is a migration, not a config edit - a 1536-dim vector will not go into a vector(768).
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>
    /// Read from config first, then <c>GEMINI_API_KEY</c>. Never logged and never put on a
    /// span: <see cref="SafeToolDecorator"/> redacts arguments, but the key never travels
    /// through a tool argument in the first place.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Overrides the provider endpoint - a local gateway, a proxy, or a record/replay server
    /// for the eval harness. Null means the SDK default.
    /// </summary>
    public string? Endpoint { get; set; }

    public string? ApiVersion { get; set; }

    public double Temperature { get; set; } = 0.2;

    public int? MaxOutputTokens { get; set; } = 8192;

    /// <summary>Per-investigation ceilings, enforced by <see cref="BudgetGuardChatClient"/>.</summary>
    public InvestigationBudgetOptions Investigation { get; set; } = new();

    public SafeToolOptions Tools { get; set; } = new();

    /// <summary>Model id to price.</summary>
    /// <remarks>
    /// <para>
    /// <b>A model with no entry here is charged at zero.</b> It logs one warning and then
    /// spends freely, because every cost window multiplies tokens by a price of 0 and never
    /// reaches its cap. So switching <see cref="Model"/> to something unlisted does not make
    /// the budget approximate - it switches the cost budget off, which is one of the safety
    /// controls, while the UI still shows a reassuring 0.0% utilisation.
    /// </para>
    /// <para>
    /// Add the price in the same change as the model. Always.
    /// </para>
    /// </remarks>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // Public list prices per million tokens, verified against ai.google.dev/gemini-api/docs/pricing
        // on 2026-08-28. Wrong prices produce wrong budgets, so this is config: correcting it
        // must not need a redeploy.
        //
        // NOTE the promotional pricing. 3.7 and 3.6 Flash are half price until 2026-12-31 and
        // DOUBLE on 2027-01-01 ($1.50/$7.50). Nothing here knows that date, so from January
        // these figures silently under-count spend by 2x and every cost cap becomes twice as
        // loose as it reads. Revisit before then.
        ["gemini-3.7-flash"] = new() { InputPerMillionUsd = 0.75m, OutputPerMillionUsd = 3.75m },
        ["gemini-3.6-flash"] = new() { InputPerMillionUsd = 0.75m, OutputPerMillionUsd = 3.75m },
        ["gemini-3.5-flash"] = new() { InputPerMillionUsd = 1.50m, OutputPerMillionUsd = 9.00m },
        ["gemini-3-flash-preview"] = new() { InputPerMillionUsd = 0.50m, OutputPerMillionUsd = 3.00m },

        // Pro. Both are tiered by prompt length - the figures here are the <=200k tier, so a
        // genuinely long investigation is under-counted. Acceptable while the digester caps
        // context well below 200k; revisit if that cap ever rises.
        ["gemini-3.1-pro-preview"] = new() { InputPerMillionUsd = 2.00m, OutputPerMillionUsd = 12.00m },
        ["gemini-2.5-pro"] = new() { InputPerMillionUsd = 1.25m, OutputPerMillionUsd = 10.00m },

        ["gemini-2.5-flash"] = new() { InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 2.50m },
        ["gemini-2.5-flash-lite"] = new() { InputPerMillionUsd = 0.10m, OutputPerMillionUsd = 0.40m },

        ["gemini-embedding-001"] = new() { InputPerMillionUsd = 0.15m, OutputPerMillionUsd = 0m },
        ["gemini-embedding-2"] = new() { InputPerMillionUsd = 0.20m, OutputPerMillionUsd = 0m },
    };

    public string PlanningModelId => string.IsNullOrWhiteSpace(PlanningModel) ? Model : PlanningModel;
}

public sealed class ModelPrice
{
    public decimal InputPerMillionUsd { get; set; }

    public decimal OutputPerMillionUsd { get; set; }
}

/// <summary>
/// The five ceilings on one investigation. Each maps to exactly one
/// <see cref="Core.Domain.TerminationReason"/>, so "why did it stop" is answerable without
/// reading logs.
/// </summary>
/// <remarks>
/// These are enforced in code, in <see cref="BudgetGuardChatClient"/>, and are deliberately
/// <b>not</b> stated in the system prompt as a request to the model. A model asked to stay
/// within twelve steps will sincerely believe it did. A counter cannot be talked out of it.
/// </remarks>
public sealed class InvestigationBudgetOptions
{
    /// <summary>Model round trips. One step is one call to the provider, tool calls excluded.</summary>
    public int MaxSteps { get; set; } = 12;

    public int MaxToolCalls { get; set; } = 20;

    public TimeSpan MaxWallClock { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Cumulative across steps, not per call. Conversation history is resent every turn, so
    /// the sum grows quadratically in the number of steps - which is the failure mode this
    /// number exists to bound.
    /// </summary>
    public long MaxInputTokens { get; set; } = 400_000;

    public decimal MaxCostUsd { get; set; } = 0.50m;

    /// <summary>
    /// Consecutive model turns that call no tool and do not conclude before the loop gives
    /// up. One is a stumble; two in a row is a model that has stopped making progress.
    /// </summary>
    public int MaxConsecutiveNoToolTurns { get; set; } = 2;
}
