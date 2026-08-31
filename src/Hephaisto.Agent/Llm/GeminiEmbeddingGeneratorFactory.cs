using Google.GenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Owns the embedding generator, independently of whichever provider is answering chat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The embedding generator used to be built by casting
/// <c>IChatClientFactory</c> to <see cref="GeminiChatClientFactory"/> and borrowing its SDK
/// client. That made "which model investigates" and "which model embeds" the same decision:
/// selecting any other chat provider threw <see cref="InvalidCastException"/> at the first
/// service resolution, before a single incident was read.
/// </para>
/// <para>
/// <b>Embeddings stay on Gemini deliberately, and separately.</b> They are not on the
/// investigation path - <see cref="IncidentEmbedder"/> runs after the incident is written and
/// feeds the vector arm of a hybrid search that already falls back to its lexical arm. So the
/// cheap-provider decision, which is about the 12-round-trip investigation loop, has no
/// bearing on them, and coupling the two would have meant changing an unmeasured thing to fix
/// a measured one.
/// </para>
/// <para>
/// <b>A missing key degrades rather than throws.</b> Chat construction fails loudly without a
/// key because an agent that cannot investigate should not report itself healthy. Embeddings
/// are the opposite: search losing its semantic arm is a reduction in quality, not an outage,
/// and it is the documented behaviour of <see cref="IncidentEmbedder.EmbedAsync"/> already.
/// The warning is emitted once, at startup, rather than once per incident.
/// </para>
/// </remarks>
public sealed class GeminiEmbeddingGeneratorFactory : IDisposable
{
    private readonly Client? _client;
    private readonly LlmOptions _options;

    public GeminiEmbeddingGeneratorFactory(
        IOptions<LlmOptions> options,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;

        // Llm:ApiKey is only this provider's key when this provider is also answering chat;
        // see GeminiChatClientFactory.ResolveApiKey.
        var chatProviderIsGemini = string.Equals(
            configuration[$"{LlmOptions.SectionName}:Provider"] ?? "gemini",
            "gemini",
            StringComparison.OrdinalIgnoreCase);

        var apiKey = _options.EmbeddingApiKey
            ?? GeminiChatClientFactory.ResolveApiKey(
                _options, configuration, allowSharedApiKey: chatProviderIsGemini);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            loggerFactory.CreateLogger<GeminiEmbeddingGeneratorFactory>().LogWarning(
                "No Gemini key for embeddings, so incidents will be stored without a vector and "
                + "search will use its lexical arm only. Set Llm:EmbeddingApiKey or "
                + "GEMINI_API_KEY to restore semantic search.");

            return;
        }

        _client = new Client(
            apiKey: apiKey,
            httpOptions: GeminiChatClientFactory.BuildHttpOptions(_options));
    }

    /// <summary>
    /// The generator, or null when no key was configured. Null is a supported state: the
    /// caller substitutes one that fails per call, which <see cref="IncidentEmbedder"/>
    /// already treats as a null embedding.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>>? Create(IServiceProvider services) =>
        _client is null
            ? null
            : new EmbeddingGeneratorBuilder<string, Embedding<float>>(
                    _client.AsIEmbeddingGenerator(
                        _options.EmbeddingModel,
                        _options.EmbeddingDimensions))
                .UseOpenTelemetry(sourceName: HephaistoTelemetry.ExtensionsAiSourceName)
                .Build(services);

    public void Dispose() => _client?.Dispose();
}

/// <summary>
/// Stands in when no embedding key is configured. Throws rather than returning an empty
/// vector, because a zero-width embedding would reach the dimension check in
/// <see cref="IncidentEmbedder"/> and be logged as a data-corruption error once per incident;
/// a throw lands in the same method's catch and is reported as the degradation it is.
/// </summary>
internal sealed class UnconfiguredEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "No embedding generator is configured. Set Llm:EmbeddingApiKey or GEMINI_API_KEY.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
