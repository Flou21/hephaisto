using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Telemetry;

using Hephaisto.Agent.Observability;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The investigation stream's single contribution to the composition root, so Program.cs
/// stays one readable page.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddHephaistoLlm(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<GrafanaOptions>(configuration.GetSection(GrafanaOptions.SectionName));
        services.Configure<InvestigationOptions>(configuration.GetSection(InvestigationOptions.SectionName));
        services.Configure<EnvironmentCardOptions>(
            configuration.GetSection(EnvironmentCardOptions.SectionName));

        // Another stream may already have registered the clock; there must be exactly one,
        // because every budget window in this layer is measured against it.
        services.TryAddSingleton<IClock>(SystemClock.Instance);

        var provider = configuration[$"{LlmOptions.SectionName}:Provider"] ?? "gemini";

        services.AddSingleton<IChatClientFactory>(sp => provider.ToLowerInvariant() switch
        {
            "gemini" => ActivatorUtilities.CreateInstance<GeminiChatClientFactory>(sp),

            // Not a vendor but a wire format: DeepSeek, OpenRouter and a local Ollama or
            // LM Studio server are all reached through this one, selected by Llm:Endpoint.
            "openai" => ActivatorUtilities.CreateInstance<OpenAiChatClientFactory>(sp),

            // Loud on purpose. A typo here would otherwise fall back to a default and produce
            // an agent quietly investigating with the wrong model.
            _ => throw new InvalidOperationException(
                $"Unknown Llm:Provider '{provider}'. Implementations are 'gemini' and 'openai'."),
        });

        services.AddSingleton<GrafanaMcpToolProvider>();

        // Same instance behind the interface, not a second one: the provider caches its tool
        // list and owns the MCP connection, so two registrations would mean two connections.
        services.AddSingleton<IGrafanaToolProvider>(sp => sp.GetRequiredService<GrafanaMcpToolProvider>());
        services.AddSingleton<PromptComposer>();

        // Annotations are wired only when Grafana is genuinely reachable AND a token that may
        // write has been supplied. Registering the real one regardless would put an HTTP call
        // on the ingest path of every install, most of which have configured neither, and it
        // would fail per incident instead of being absent once.
        var grafana = configuration.GetSection(GrafanaOptions.SectionName).Get<GrafanaOptions>()
            ?? new GrafanaOptions();

        if (!string.IsNullOrWhiteSpace(grafana.Url) && !string.IsNullOrWhiteSpace(grafana.AnnotationToken))
        {
            services.AddHttpClient<IGrafanaAnnotator, GrafanaAnnotator>();
        }
        else
        {
            services.AddSingleton<IGrafanaAnnotator, NullGrafanaAnnotator>();
        }

        // The embedding generator, wrapped so its spans land on the same source as the chat
        // spans. Failure degrades - IncidentEmbedder saves a null embedding and search falls
        // back to its lexical arm - so this never needs a resilience policy that could turn
        // one slow call into a stalled resolution.
        //
        // Each factory owns its own SDK client rather than borrowing the chat factory's. This
        // used to cast IChatClientFactory to GeminiChatClientFactory, which made any other
        // chat provider an InvalidCastException at the first service resolution - embeddings
        // and investigation are separate decisions and are wired as such.
        //
        // EmbeddingProvider is read separately from Provider and defaults to gemini
        // independently of it. Inheriting would silently move embeddings to whatever answers
        // chat the day someone switched, and a chat endpoint need not serve embeddings at all.
        var embeddingProvider = configuration[$"{LlmOptions.SectionName}:EmbeddingProvider"] ?? "gemini";

        services.AddSingleton<IEmbeddingGeneratorFactory>(sp => embeddingProvider.ToLowerInvariant() switch
        {
            "gemini" => ActivatorUtilities.CreateInstance<GeminiEmbeddingGeneratorFactory>(sp),

            // Ollama, vLLM or any hosted provider serving /v1/embeddings. This is what lets a
            // fully self-hosted install keep semantic search without an external account.
            "openai" => ActivatorUtilities.CreateInstance<OpenAiEmbeddingGeneratorFactory>(sp),

            // Loud on purpose, and matching the chat seam: a typo must not fall back to a
            // default and leave an operator believing they self-hosted embeddings.
            _ => throw new InvalidOperationException(
                $"Unknown Llm:EmbeddingProvider '{embeddingProvider}'. Implementations are "
                + "'gemini' and 'openai'."),
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            sp.GetRequiredService<IEmbeddingGeneratorFactory>().Create(sp)
                ?? new UnconfiguredEmbeddingGenerator());

        services.AddSingleton<IncidentEmbedder>();

        // Scoped: LlmBudgetService needs a DbContext. The runner is resolved per incident, so
        // it lands in the same scope as the repositories that will persist its output - which
        // is what lets the steps, the findings and the llm_usage row commit together.
        services.AddScoped<IGlobalLlmBudget, LlmBudgetServiceAdapter>();
        services.AddScoped<InvestigationRunner>();

        return services;
    }

    /// <summary>
    /// For hosts with no Postgres - the AppHost smoke run and the eval harness. Widens the
    /// outer budget from "the whole process" to "each incident"; the per-investigation
    /// ceilings still bind, so this is a smaller cap, not no cap.
    /// </summary>
    public static IServiceCollection AddHephaistoLlmWithoutPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHephaistoLlm(configuration);
        services.RemoveAll<IGlobalLlmBudget>();
        services.AddSingleton<IGlobalLlmBudget, NullGlobalLlmBudget>();

        return services;
    }
}
