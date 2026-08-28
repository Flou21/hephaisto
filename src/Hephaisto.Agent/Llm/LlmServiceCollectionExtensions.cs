using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Telemetry;

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

            // Loud on purpose. A typo here would otherwise fall back to a default and produce
            // an agent quietly investigating with the wrong model.
            _ => throw new InvalidOperationException(
                $"Unknown Llm:Provider '{provider}'. The only implementation is 'gemini'."),
        });

        services.AddSingleton<GrafanaMcpToolProvider>();
        services.AddSingleton<PromptComposer>();

        // Gemini's own embedding generator, wrapped so its spans land on the same source as
        // the chat spans. Failure degrades - IncidentEmbedder saves a null embedding and
        // search falls back to its lexical arm - so this never needs a resilience policy that
        // could turn one slow call into a stalled resolution.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var factory = (GeminiChatClientFactory)sp.GetRequiredService<IChatClientFactory>();
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;

            return new EmbeddingGeneratorBuilder<string, Embedding<float>>(
                    factory.Client.AsIEmbeddingGenerator(
                        options.EmbeddingModel,
                        options.EmbeddingDimensions))
                .UseOpenTelemetry(sourceName: HephaistoTelemetry.ExtensionsAiSourceName)
                .Build(sp);
        });

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
