using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Embeddings against anything serving the OpenAI <c>/v1/embeddings</c> wire format - a local
/// Ollama or vLLM, or a hosted provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> Until it existed, one arm of the console's hybrid search required
/// a Google API account, on an agent whose whole point is that it runs in your cluster. That
/// is a dependency problem rather than a cost or a correctness one - the spend is negligible
/// and search already falls back to its lexical arm - and a deployment tax out of proportion
/// to what it buys. This makes the endpoint a configuration value, so a fully self-hosted
/// install can keep semantic search without an external account.
/// </para>
/// <para>
/// <b>It does not change the default.</b> <c>Llm:EmbeddingProvider</c> defaults to
/// <c>gemini</c>, so an existing install embeds exactly as it did. Choosing a different
/// provider is an operator decision made once, in git, and nothing about this type recommends
/// which model to point it at: this repository has no measurement of search quality, so a
/// bundled default would be an unevidenced choice standing next to measured ones.
/// </para>
/// <para>
/// <b>Dimensions are requested, not assumed.</b> <see cref="LlmOptions.EmbeddingDimensions"/>
/// is sent with the request because it must match the <c>vector(768)</c> column in
/// <c>incident_digests</c>. A server that ignores or rejects the parameter surfaces either as
/// an error or as a dimension mismatch, and both degrade to the lexical arm rather than
/// writing a vector the column cannot hold. Prefer a model whose native width already matches.
/// </para>
/// </remarks>
public sealed class OpenAiEmbeddingGeneratorFactory : IEmbeddingGeneratorFactory
{
    private readonly OpenAIClient? _client;
    private readonly LlmOptions _options;

    public OpenAiEmbeddingGeneratorFactory(
        IOptions<LlmOptions> options,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;

        var logger = loggerFactory.CreateLogger<OpenAiEmbeddingGeneratorFactory>();

        // The embedding endpoint is its own value and only falls back to the chat one. They
        // are frequently different hosts: a local Ollama serving embeddings next to a hosted
        // chat provider is the case this exists for.
        var endpoint = FirstNonBlank(_options.EmbeddingEndpoint, _options.Endpoint);

        // Llm:ApiKey belongs to whichever provider answers chat, so it is only borrowed when
        // that provider is also the OpenAI wire format. The same rule the Gemini factory
        // applies, in the other direction.
        var chatProviderIsOpenAi = string.Equals(
            configuration[$"{LlmOptions.SectionName}:Provider"] ?? "gemini",
            "openai",
            StringComparison.OrdinalIgnoreCase);

        var apiKey = FirstNonBlank(
            _options.EmbeddingApiKey,
            chatProviderIsOpenAi ? _options.ApiKey : null,
            chatProviderIsOpenAi ? configuration["LLM_API_KEY"] : null,
            chatProviderIsOpenAi ? System.Environment.GetEnvironmentVariable("LLM_API_KEY") : null);

        if (apiKey is null)
        {
            if (endpoint is null)
            {
                // Degrade rather than throw: this is the embedding path, and the whole
                // contract of this seam is that losing it costs search quality, not uptime.
                logger.LogWarning(
                    "Llm:EmbeddingProvider is 'openai' but neither Llm:EmbeddingApiKey nor "
                    + "Llm:EmbeddingEndpoint is set, so incidents will be stored without a "
                    + "vector and search will use its lexical arm only.");

                return;
            }

            // A local server ignores the credential but the SDK still requires one. Unlike
            // the chat factory, a hosted endpoint with no key is not a startup error here -
            // it will fail per call and degrade, which is this path's documented behaviour.
            logger.LogInformation(
                "No embedding API key configured; sending a placeholder credential to "
                + "{Endpoint}. This is expected for a local server.",
                endpoint);

            apiKey = "not-required-by-local-server";
        }

        var clientOptions = new OpenAIClientOptions();

        if (endpoint is not null)
        {
            // Passed through verbatim including the version segment, exactly as
            // OpenAiChatClientFactory does - guessing a suffix is how a gateway behind a path
            // prefix breaks.
            clientOptions.Endpoint = new Uri(endpoint);
        }

        _client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

        logger.LogInformation(
            "Embeddings will use the openai wire format: model {Model} at {Endpoint}, "
            + "{Dimensions} dimensions.",
            _options.EmbeddingModel,
            endpoint ?? "the SDK default endpoint",
            _options.EmbeddingDimensions);
    }

    public string ProviderName => "openai";

    public string ModelId => _options.EmbeddingModel;

    public IEmbeddingGenerator<string, Embedding<float>>? Create(IServiceProvider services) =>
        _client is null
            ? null
            : new EmbeddingGeneratorBuilder<string, Embedding<float>>(
                    _client.GetEmbeddingClient(_options.EmbeddingModel)
                        .AsIEmbeddingGenerator(_options.EmbeddingDimensions))
                .UseOpenTelemetry(sourceName: HephaistoTelemetry.ExtensionsAiSourceName)
                .Build(services);

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
