using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Builds Gemini-backed <see cref="IChatClient"/> chains.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chain order is load-bearing.</b> <c>ChatClientBuilder</c> applies links outermost-first
/// in the order they are added, so:
/// </para>
/// <code>
/// UseOpenTelemetry()      // outermost: one gen_ai span per caller-visible request
/// UseFunctionInvocation() // the tool loop happens inside that span
/// BudgetGuardChatClient   // innermost: one pass = one provider round trip = one step
/// </code>
/// <para>
/// Put <c>UseOpenTelemetry()</c> after <c>UseFunctionInvocation()</c> and the tool calls fall
/// outside the chat span - the trace still renders, which is why it is easy to get wrong, but
/// the tool calls are no longer children of the turn that caused them and the "what did it
/// do" view in Tempo becomes a flat list. Put the budget guard anywhere but innermost and it
/// counts one step where the invoice counts nine.
/// </para>
/// </remarks>
public sealed class GeminiChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly Client _client;
    private readonly LlmOptions _options;
    private readonly LlmPricing _pricing;
    private readonly ILoggerFactory _loggerFactory;

    public GeminiChatClientFactory(
        IOptions<LlmOptions> options,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _pricing = new LlmPricing(_options.Pricing, loggerFactory.CreateLogger<LlmPricing>());

        // Config first so a ConfigMap or user-secret can override, then the conventional
        // environment variable. Failing at construction is deliberate: an agent that boots
        // without a key looks healthy and then escalates every incident with an obscure
        // error at 3am, which is strictly worse than not booting.
        var apiKey = _options.ApiKey
            ?? configuration["GEMINI_API_KEY"]
            // Fully qualified: Google.GenAI.Types has its own `Environment` type, and the
            // `using Google.GenAI.Types` above makes the bare name ambiguous.
            ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? throw new InvalidOperationException(
                "No Gemini API key. Set Llm:ApiKey or the GEMINI_API_KEY environment variable.");

        var http = BuildHttpOptions(_options);

        if (http.RetryOptions is null)
        {
            // Loud, because a provider blip then discards a whole investigation.
            loggerFactory.CreateLogger<GeminiChatClientFactory>().LogWarning(
                "Llm:Retry:Attempts is {Attempts}. Provider retry is disabled; a single "
                + "transient 429 or 503 will terminate an investigation as Faulted.",
                _options.Retry.Attempts);
        }

        _client = new Client(apiKey: apiKey, httpOptions: http);
    }

    /// <summary>
    /// Builds the SDK transport options, including retry.
    /// </summary>
    /// <remarks>
    /// Always returns an instance, so <c>RetryOptions</c> is always set. It used to return
    /// null unless an endpoint or api-version override was configured, and a null
    /// <c>HttpOptions</c> carries a null <c>RetryOptions</c>, which the SDK documents as "a
    /// single attempt".
    ///
    /// This covers the embedding path only. Measurement showed the SDK accepts this setting
    /// and does not apply it to <c>AsIChatClient</c>, so chat retry is
    /// <see cref="TransientRetryChatClient"/> instead. See <see cref="LlmOptions.Retry"/>.
    /// </remarks>
    internal static HttpOptions BuildHttpOptions(LlmOptions options)
    {
        var http = new HttpOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(options.Endpoint) ? null : options.Endpoint,
            ApiVersion = string.IsNullOrWhiteSpace(options.ApiVersion) ? null : options.ApiVersion,
        };

        if (options.Retry.Attempts > 1)
        {
            http.RetryOptions = new HttpRetryOptions
            {
                Attempts = options.Retry.Attempts,
                InitialDelay = options.Retry.InitialDelay.TotalSeconds,
                MaxDelay = options.Retry.MaxDelay.TotalSeconds,
                ExpBase = options.Retry.ExpBase,
                Jitter = options.Retry.Jitter,
            };
        }

        return http;
    }

    public string ProviderName => "gemini";

    public string InvestigationModelId => _options.Model;

    public string PlanningModelId => _options.PlanningModelId;

    /// <summary>The raw SDK client, for the embedding generator. Owned by this factory.</summary>
    internal Client Client => _client;

    public IChatClient CreateInvestigationClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null) =>
        new ChatClientBuilder(_client.AsIChatClient(_options.Model))
            .UseOpenTelemetry(_loggerFactory, HephaistoTelemetry.ExtensionsAiSourceName)
            .UseFunctionInvocation(_loggerFactory)
            .Use(inner => new BudgetGuardChatClient(
                inner, budget, _pricing, _options.Model, recorder, incidentId))
            // Innermost, beneath the budget guard, so a retried attempt does not spend a step.
            .Use(inner => new TransientRetryChatClient(
                inner, _options.Retry, _loggerFactory.CreateLogger<TransientRetryChatClient>()))
            .Build();

    public IChatClient CreatePlanningClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null) =>
        // No UseFunctionInvocation link. Phase 2 is structurally incapable of calling a tool,
        // not merely instructed not to.
        new ChatClientBuilder(_client.AsIChatClient(_options.PlanningModelId))
            .UseOpenTelemetry(_loggerFactory, HephaistoTelemetry.ExtensionsAiSourceName)
            .Use(inner => new BudgetGuardChatClient(
                inner, budget, _pricing, _options.PlanningModelId, recorder, incidentId))
            .Use(inner => new TransientRetryChatClient(
                inner, _options.Retry, _loggerFactory.CreateLogger<TransientRetryChatClient>()))
            .Build();

    public void Dispose() => _client.Dispose();
}
