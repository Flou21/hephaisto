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
    /// Selects the <see cref="IChatClientFactory"/> implementation: <c>gemini</c> or
    /// <c>openai</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>openai</c> means the OpenAI wire format rather than the vendor, and it is how every
    /// non-Gemini provider is reached: DeepSeek, OpenRouter, and a local Ollama or LM Studio
    /// server all speak it. Which one is in use is <see cref="Endpoint"/> plus
    /// <see cref="Model"/>, so adding a provider is a ConfigMap edit and not a code change -
    /// which matters both for cost and for the case where a provider is itself the outage.
    /// </para>
    /// <para>
    /// <b>Changing this needs a pod restart, not just a ConfigMap edit.</b> The factory is a
    /// singleton and captures the provider, endpoint and model id at construction.
    /// </para>
    /// </remarks>
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
    /// The chat provider's key. Read from config first, then the provider's conventional
    /// environment variable - <c>GEMINI_API_KEY</c> for <c>gemini</c>, <c>LLM_API_KEY</c> or
    /// <c>OPENAI_API_KEY</c> for <c>openai</c>. Never logged and never put on a span:
    /// <see cref="SafeToolDecorator"/> redacts arguments, but the key never travels through a
    /// tool argument in the first place.
    /// </summary>
    /// <remarks>
    /// <b>This belongs to whichever provider <see cref="Provider"/> selects</b>, so it is not
    /// reused for embeddings unless that provider is Gemini. See
    /// <see cref="EmbeddingApiKey"/>.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The embedding key, when it differs from <see cref="ApiKey"/>. Falls back to
    /// <c>GEMINI_API_KEY</c>.
    /// </summary>
    /// <remarks>
    /// Embeddings stay on Gemini regardless of which provider answers chat, because they are
    /// not on the investigation path and the cheap-provider decision is about the
    /// investigation loop. Running chat on an OpenAI-compatible provider therefore needs two
    /// keys, or none at all if losing the search index's semantic arm is acceptable - a
    /// missing key degrades rather than failing to boot.
    /// </remarks>
    public string? EmbeddingApiKey { get; set; }

    /// <summary>
    /// Overrides the provider endpoint - a local gateway, a proxy, or a record/replay server
    /// for the eval harness. Null means the SDK default.
    /// </summary>
    /// <remarks>
    /// <b>This does not change the wire format, only the address.</b> Under
    /// <c>Provider=gemini</c> it retargets <c>Google.GenAI</c>'s own transport, so pointing it
    /// at an OpenAI-compatible URL sends Gemini-shaped requests there and fails. Reaching
    /// DeepSeek, OpenRouter or a local server means <c>Provider=openai</c> as well. Providers
    /// publish this including the version segment
    /// (<c>https://openrouter.ai/api/v1</c>, <c>http://localhost:11434/v1</c>).
    /// </remarks>
    public string? Endpoint { get; set; }

    public string? ApiVersion { get; set; }

    public double Temperature { get; set; } = 0.2;

    public int? MaxOutputTokens { get; set; } = 8192;

    /// <summary>
    /// Transient-fault retry for provider calls, applied inside the SDK's own HTTP send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The SDK ships this and defaults it off.</b> A null <c>HttpOptions.RetryOptions</c>
    /// means "a single attempt", so until this existed a single 429 or 503 destroyed a whole
    /// investigation. Measured on the dev cluster on 2026-08-28: <b>9 of 12</b> investigations
    /// terminated <see cref="Core.Domain.TerminationReason.Faulted"/>, every one of them on
    /// "This model is currently experiencing high demand" - the provider's own wording for a
    /// retryable overload. Each discarded a complete run's tokens and produced no finding.
    /// </para>
    /// <para>
    /// <b>Why not the ServiceDefaults resilience handler.</b> <c>ConfigureHttpClientDefaults</c>
    /// only reaches clients built by <c>IHttpClientFactory</c>. <c>Google.GenAI.Client</c>
    /// constructs its own <c>HttpClient</c>, so <c>AddStandardResilienceHandler</c> has never
    /// seen a Gemini call - notwithstanding the comment there that says it does.
    /// </para>
    /// <para>
    /// <b>These numbers drive two retries, not one.</b> They are handed to the SDK, which
    /// applies them to the embedding path, and to
    /// <see cref="TransientRetryChatClient"/>, which applies them to chat. Both are needed:
    /// the SDK setting is accepted and then silently ignored on the <c>AsIChatClient</c> path
    /// - configured with five attempts and 1s/2s/4s/8s backoff, failed turns still returned in
    /// 1.2s to 5.7s, which four retries cannot do - so chat needs a retry we control. Chat
    /// never reaches the SDK's retry and embeddings never reach ours, so the two do not stack.
    /// </para>
    /// <para>
    /// <b>The chat retry sits innermost, beneath <see cref="BudgetGuardChatClient"/>.</b> The
    /// budget guard is built innermost so that one pass through it is one provider round trip,
    /// which is what a step means and what gets billed. A retry above it would re-enter
    /// <c>EnsureCanStartStep()</c> per attempt and spend the step budget on calls that
    /// returned zero tokens.
    /// </para>
    /// </remarks>
    public LlmRetryOptions Retry { get; set; } = new();

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

        // Reached through Provider=openai. Verified 2026-08-31.
        //
        // KEYED ON WHAT THE PROVIDER RETURNS, not on what Llm:Model was set to.
        // BudgetGuardChatClient prices `response.ModelId ?? configured id`, so the id that
        // arrives back is the one that has to resolve - and the same weights are named
        // differently by each host. Resolution is exact match then longest prefix, so
        // "openai/gpt-oss-120b" also covers a suffixed variant like ":free", and
        // "deepseek-v4-flash" covers "deepseek-v4-flash-vision-exp", which is priced the same.
        // Get this wrong and the model is charged at zero: the cost budget stops binding
        // while the console reports a comfortable 0.0% utilisation.
        ["gpt-oss-120b"] = new() { InputPerMillionUsd = 0.03m, OutputPerMillionUsd = 0.17m },
        ["openai/gpt-oss-120b"] = new() { InputPerMillionUsd = 0.03m, OutputPerMillionUsd = 0.17m },
        ["gpt-oss:120b"] = new() { InputPerMillionUsd = 0.03m, OutputPerMillionUsd = 0.17m },
        ["gpt-oss-20b"] = new() { InputPerMillionUsd = 0.03m, OutputPerMillionUsd = 0.15m },
        ["openai/gpt-oss-20b"] = new() { InputPerMillionUsd = 0.03m, OutputPerMillionUsd = 0.15m },
        ["gpt-oss:20b"] = new() { InputPerMillionUsd = 0.03m, OutputPerMillionUsd = 0.15m },

        // NOTE the time-of-day pricing, which is this provider's version of the promotion
        // landmine above. These are the OFF-PEAK rates. Peak is DOUBLE, and peak is
        // 01:00-04:00 and 06:00-10:00 UTC on weekdays - so a European afternoon is off-peak
        // and an overnight batch is not. Nothing here knows the clock, so a run inside those
        // windows under-counts by 2x and every cost cap reads twice as tight as it binds.
        ["deepseek-v4-flash"] = new() { InputPerMillionUsd = 0.22m, OutputPerMillionUsd = 0.66m },
        ["deepseek-v4-pro"] = new() { InputPerMillionUsd = 0.66m, OutputPerMillionUsd = 1.98m },

        // No entry for a locally served model, deliberately. Zero is its true price, but a
        // zero entry and a missing entry produce identical arithmetic, and the guard in
        // LlmPricingTests that every listed model costs something is worth more than the
        // tidiness of naming this one. A local model takes the unpriced-model warning
        // instead, which correctly says the cost budget will not bind - the step, token and
        // wall-clock budgets still do.
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

    /// <summary>
    /// A deadlock backstop, not the primary limit. <see cref="MaxSteps"/>,
    /// <see cref="MaxToolCalls"/>, <see cref="MaxInputTokens"/> and <see cref="MaxCostUsd"/>
    /// are the budgets that are meant to bind; this one exists so an investigation whose
    /// provider has stopped answering cannot hold a worker slot forever.
    /// </summary>
    /// <remarks>
    /// Was 4 minutes, which made it the binding constraint instead. Measured against
    /// gemini-3.7-flash on the dev cluster, a round trip takes 20-60s, so 4 minutes bought
    /// 8 of the 12 permitted steps and every investigation ended
    /// <see cref="Core.Domain.TerminationReason.WallClockExhausted"/> - terminated before
    /// the model ever reached its conclusion, which meant no findings, no root cause and no
    /// plan were ever produced. A wall clock that fires first turns every other budget into
    /// decoration and makes the agent look like it cannot diagnose anything.
    ///
    /// Ten minutes is chosen so that 12 steps at the observed worst case still fit. It does
    /// not widen the real exposure: the token and cost ceilings are unchanged, and they are
    /// what actually bound spend.
    /// </remarks>
    public TimeSpan MaxWallClock { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cumulative across steps, not per call. Conversation history is resent every turn, so
    /// the sum grows quadratically in the number of steps - which is the failure mode this
    /// number exists to bound.
    /// </summary>
    /// <remarks>
    /// <b>Cumulative is why this is not a context-window setting</b>, and the distinction
    /// matters when moving to a model with a smaller window: 400,000 here is spread across up
    /// to <see cref="MaxSteps"/> turns, so it does not imply any single call carrying 400,000
    /// tokens, and it does not need lowering to run a 131k-context model. What bounds the
    /// largest single turn is the digester's context cap. Lowering this to match a window
    /// would instead cut the investigation short several steps early, for a limit the
    /// provider was never going to hit.
    /// </remarks>
    public long MaxInputTokens { get; set; } = 400_000;

    public decimal MaxCostUsd { get; set; } = 0.50m;

    /// <summary>
    /// Consecutive model turns that call no tool and do not conclude before the loop gives
    /// up. One is a stumble; two in a row is a model that has stopped making progress.
    /// </summary>
    public int MaxConsecutiveNoToolTurns { get; set; } = 2;
}

/// <summary>
/// Transient-fault retry handed to the provider SDK, which applies
/// <c>min(initialDelay * expBase^(attempt-1) + U(0, jitter), maxDelay)</c> between attempts
/// and retries 408, 429 and 5xx plus transport failures. Caller cancellation is never
/// retried, so the wall-clock budget still terminates a stuck investigation on time.
/// </summary>
public sealed class LlmRetryOptions
{
    /// <summary>Total attempts including the first. 1 disables retry.</summary>
    public int Attempts { get; set; } = 5;

    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A backstop on one sleep, not the usual case. At the default <see cref="Attempts"/> the
    /// schedule is 1s, 2s, 4s, 8s and this never binds; it exists so that raising
    /// <see cref="Attempts"/> cannot inherit the SDK's 60s default and let a single unlucky
    /// step sleep away most of
    /// <see cref="InvestigationBudgetOptions.MaxWallClock"/>.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    public double ExpBase { get; set; } = 2.0;

    public double Jitter { get; set; } = 1.0;
}
