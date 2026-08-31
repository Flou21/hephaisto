using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// Pins the provider seam open.
/// </summary>
/// <remarks>
/// <para>
/// The seam was written to make swapping provider a config edit, and for a year it could not
/// have worked: the embedding registration cast <c>IChatClientFactory</c> to the Gemini
/// implementation, so <c>Llm:Provider=openai</c> threw <see cref="InvalidCastException"/> at
/// the first service resolution. Nothing failed until something tried, because nothing ever
/// tried. These tests are that attempt, made permanent.
/// </para>
/// <para>
/// Every case pins <c>GEMINI_API_KEY</c> to the empty string in configuration so the result
/// does not depend on whether the developer running them happens to have one exported.
/// </para>
/// </remarks>
public class ProviderSelectionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                // Beats the environment variable without being a usable key, so "no key
                // configured" is a property of the test rather than of the machine.
                new KeyValuePair<string, string?>("GEMINI_API_KEY", string.Empty),
                .. settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)),
            ])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // The host registers this; the factories take it via ActivatorUtilities rather than
        // through the configuration argument, so the container needs it too.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHephaistoLlm(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Openai_provider_resolves_its_own_factory()
    {
        using var sp = Build(("Llm:Provider", "openai"), ("Llm:ApiKey", "sk-test"));

        var factory = sp.GetRequiredService<IChatClientFactory>();

        Assert.IsType<OpenAiChatClientFactory>(factory);
        Assert.Equal("openai", factory.ProviderName);
    }

    [Fact]
    public void Gemini_stays_the_default()
    {
        using var sp = Build(("Llm:ApiKey", "gm-test"));

        Assert.IsType<GeminiChatClientFactory>(sp.GetRequiredService<IChatClientFactory>());
    }

    [Fact]
    public void An_unknown_provider_is_loud()
    {
        using var sp = Build(("Llm:Provider", "hopeful-typo"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IChatClientFactory>());

        Assert.Contains("hopeful-typo", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The exact regression: this threw InvalidCastException before the split.</summary>
    [Fact]
    public void Embeddings_resolve_while_chat_runs_on_another_provider()
    {
        using var sp = Build(
            ("Llm:Provider", "openai"),
            ("Llm:ApiKey", "sk-test"),
            ("Llm:EmbeddingApiKey", "gm-test"));

        var generator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.NotNull(generator);
        Assert.IsNotType<UnconfiguredEmbeddingGenerator>(generator);
    }

    [Fact]
    public void Embeddings_degrade_rather_than_failing_to_boot_without_a_key()
    {
        // The dev case this whole change exists to serve: chat on a cheap provider, no Gemini
        // credit at all. Search loses its semantic arm; the agent still investigates.
        using var sp = Build(("Llm:Provider", "openai"), ("Llm:ApiKey", "sk-test"));

        var generator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.IsType<UnconfiguredEmbeddingGenerator>(generator);
    }

    [Fact]
    public async Task The_unconfigured_generator_throws_where_IncidentEmbedder_catches()
    {
        // Not an empty vector: that would clear the try/catch and be rejected by the width
        // check as a data-corruption error, once per incident.
        var generator = new UnconfiguredEmbeddingGenerator();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => generator.GenerateAsync(
                ["anything"],
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true, "chat-provider-key")]
    [InlineData(false, "")]
    public void The_chat_key_reaches_embeddings_only_when_the_chat_provider_is_gemini(
        bool allowShared,
        string expected)
    {
        // Llm:ApiKey holds an OpenRouter or DeepSeek key when Provider=openai. Handing that
        // to Gemini's embedding endpoint would authenticate with the wrong credential and
        // fail per call, which IncidentEmbedder would swallow as a routine degradation - a
        // misconfiguration wearing the costume of a known-good fallback.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("GEMINI_API_KEY", string.Empty)])
            .Build();

        var resolved = GeminiChatClientFactory.ResolveApiKey(
            new LlmOptions { ApiKey = "chat-provider-key" },
            configuration,
            allowSharedApiKey: allowShared);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void The_openai_provider_refuses_to_boot_without_a_key_against_a_hosted_endpoint()
    {
        // Silence here would mean every investigation dying on a 401 at 3am.
        using var sp = Build(("Llm:Provider", "openai"));

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IChatClientFactory>());
    }

    [Fact]
    public void A_local_endpoint_may_omit_the_key()
    {
        // Ollama and LM Studio ignore the credential; the SDK still demands one.
        using var sp = Build(
            ("Llm:Provider", "openai"),
            ("Llm:Endpoint", "http://localhost:11434/v1"));

        Assert.IsType<OpenAiChatClientFactory>(sp.GetRequiredService<IChatClientFactory>());
    }
}
