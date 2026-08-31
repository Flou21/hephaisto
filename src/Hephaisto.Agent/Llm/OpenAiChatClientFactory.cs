using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Builds <see cref="IChatClient"/> chains against anything speaking the OpenAI wire format.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, several providers. DeepSeek, OpenRouter, Ollama and LM Studio all
/// expose OpenAI-shaped chat completions, so which one is in use is
/// <see cref="LlmOptions.Endpoint"/> plus <see cref="LlmOptions.Model"/> - a ConfigMap edit,
/// not a second factory. This is the reason the seam is worth having at all.
/// </para>
/// <para>
/// <b>Why this is not a Gemini config override.</b> <see cref="LlmOptions.Endpoint"/> reaches
/// <c>Google.GenAI</c>'s own transport in <see cref="GeminiChatClientFactory"/>, so pointing
/// it at an OpenAI-compatible URL redirects a client that still speaks the Gemini protocol.
/// A different wire format needs a different SDK underneath, which is this type.
/// </para>
/// <para>
/// <b>Chain order is load-bearing and identical to
/// <see cref="GeminiChatClientFactory"/>.</b> <c>ChatClientBuilder</c> applies links
/// outermost-first in the order they are added:
/// </para>
/// <code>
/// UseOpenTelemetry()      // outermost: one gen_ai span per caller-visible request
/// UseFunctionInvocation() // the tool loop happens inside that span
/// BudgetGuardChatClient   // one pass = one provider round trip = one step
/// TransientRetryChatClient// innermost: a retried attempt does not spend a step
/// </code>
/// <para>
/// The two factories must stay in step. If the order changes in one it changes in both, or
/// the budget and the traces mean different things depending on which provider is configured
/// - which is the kind of difference that only shows up in an incident.
/// </para>
/// </remarks>
public sealed class OpenAiChatClientFactory : IChatClientFactory
{
    private readonly OpenAIClient _client;
    private readonly LlmOptions _options;
    private readonly LlmPricing _pricing;
    private readonly ILoggerFactory _loggerFactory;

    public OpenAiChatClientFactory(
        IOptions<LlmOptions> options,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _pricing = new LlmPricing(_options.Pricing, loggerFactory.CreateLogger<LlmPricing>());

        var logger = loggerFactory.CreateLogger<OpenAiChatClientFactory>();
        var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? null : _options.Endpoint;

        // Config first so a ConfigMap or user-secret can override, then the conventional
        // environment variables. LLM_API_KEY is the provider-neutral name; OPENAI_API_KEY is
        // accepted because most tooling already sets it.
        var apiKey = _options.ApiKey
            ?? configuration["LLM_API_KEY"]
            ?? System.Environment.GetEnvironmentVariable("LLM_API_KEY")
            ?? System.Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // A local server (Ollama, LM Studio) ignores the credential but the SDK still
            // requires one, so an unauthenticated endpoint gets a placeholder rather than a
            // crash. Against a hosted provider the same silence would mean every
            // investigation fails on a 401 at 3am, so that case still throws at construction.
            if (endpoint is null)
            {
                throw new InvalidOperationException(
                    "No API key for the openai provider. Set Llm:ApiKey or the LLM_API_KEY "
                    + "environment variable. A key may only be omitted when Llm:Endpoint "
                    + "points at a local server that does not authenticate.");
            }

            logger.LogInformation(
                "No API key configured; sending a placeholder credential to {Endpoint}. This is "
                + "expected for a local server and wrong for a hosted provider.",
                endpoint);

            apiKey = "not-required-by-local-server";
        }

        var clientOptions = new OpenAIClientOptions();

        if (endpoint is not null)
        {
            // Providers publish this including the version segment (OpenRouter's
            // https://openrouter.ai/api/v1, Ollama's http://host:11434/v1). It is passed
            // through verbatim rather than normalised, because guessing a suffix is how a
            // gateway behind a path prefix breaks.
            clientOptions.Endpoint = new Uri(endpoint);
        }

        // Deliberately not the SDK's retry. Chat retry is TransientRetryChatClient, innermost
        // and beneath the budget guard, for the reasons in LlmOptions.Retry - a retry above
        // the guard spends a step per attempt on calls that returned zero tokens.
        _client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

        if (!_options.Pricing.Keys.Any(k => _options.Model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            && !_options.Pricing.ContainsKey(_options.Model))
        {
            // LlmPricing warns too, but only once the first turn is priced. Saying it at
            // construction means the operator sees it before the run, not in the middle of
            // one, and this is the failure that looks like success: an unpriced model bills
            // as zero, so MaxCostUsd never binds while the UI reports 0.0% utilisation.
            logger.LogWarning(
                "No price entry for model {Model}. Its spend will count as $0 and the cost "
                + "budget will not bind. Add Llm:Pricing:{Model}.",
                _options.Model,
                _options.Model);
        }
    }

    public string ProviderName => "openai";

    public string InvestigationModelId => _options.Model;

    public string PlanningModelId => _options.PlanningModelId;

    public IChatClient CreateInvestigationClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null) =>
        new ChatClientBuilder(_client.GetChatClient(_options.Model).AsIChatClient())
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
        new ChatClientBuilder(_client.GetChatClient(_options.PlanningModelId).AsIChatClient())
            .UseOpenTelemetry(_loggerFactory, HephaistoTelemetry.ExtensionsAiSourceName)
            .Use(inner => new BudgetGuardChatClient(
                inner, budget, _pricing, _options.PlanningModelId, recorder, incidentId))
            .Use(inner => new TransientRetryChatClient(
                inner, _options.Retry, _loggerFactory.CreateLogger<TransientRetryChatClient>()))
            .Build();
}
