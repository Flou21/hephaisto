using Microsoft.Extensions.AI;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The embedding provider seam, deliberately separate from <see cref="IChatClientFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second seam rather than a second method on the first one.</b> Which model
/// investigates and which model embeds are independent decisions, and the repository has
/// already paid once for conflating them: the embedding generator used to be built by casting
/// <c>IChatClientFactory</c> to the Gemini implementation, so selecting any other chat provider
/// threw <see cref="InvalidCastException"/> at the first service resolution. Four releases of
/// documented portability, never once exercised. Two seams cannot fail that way.
/// </para>
/// <para>
/// <b>The two are configured separately and default separately.</b> <c>Llm:EmbeddingProvider</c>
/// is not inherited from <c>Llm:Provider</c>, because speaking the OpenAI chat wire format does
/// not imply serving <c>/v1/embeddings</c>, and where it is served the useful model is rarely
/// the chat model. Silently redirecting embeddings at whatever answers chat would turn a
/// working install into one that fails per incident. Inheriting would be convenient exactly
/// until it was wrong.
/// </para>
/// <para>
/// <b>Every implementation degrades rather than throws.</b> <see cref="Create"/> returns
/// <see langword="null"/> when nothing is configured, and the caller substitutes a generator
/// that fails per call. Chat construction is the opposite and throws, because an agent that
/// cannot investigate must not report itself healthy; losing the vector arm of a hybrid search
/// is a reduction in quality, not an outage, and
/// <see cref="IncidentEmbedder"/> already treats a null embedding as the degradation it is.
/// </para>
/// </remarks>
public interface IEmbeddingGeneratorFactory
{
    /// <summary>The configured provider name, for logs and the status page.</summary>
    string ProviderName { get; }

    /// <summary>The embedding model id, for logs and the status page.</summary>
    string ModelId { get; }

    /// <summary>
    /// The generator, or <see langword="null"/> when the provider has nothing to build with.
    /// Null is a supported state, not an error - see the remarks on this interface.
    /// </summary>
    IEmbeddingGenerator<string, Embedding<float>>? Create(IServiceProvider services);
}
