using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Agent.Options;
using Watchtower.Agent.Web;
using Watchtower.Core;
using Watchtower.Core.Abstractions;

namespace Watchtower.Agent.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddWatchtowerPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IngestOptions>(configuration.GetSection(IngestOptions.SectionName));

        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<WatchtowerMetrics>();
        services.AddSingleton<InvestigationQueue>();

        services.AddScoped<IncidentStateMachine>();
        services.AddScoped<IncidentTriage>();

        // One instance serving two roles: the ISignalSink every producer writes to, and the
        // BackgroundService that drains it. Registering it twice would give the webhook a
        // different channel from the one the loop reads, and signals would vanish silently.
        services.AddSingleton<SignalIngestPipeline>();
        services.AddSingleton<ISignalSink>(sp => sp.GetRequiredService<SignalIngestPipeline>());
        services.AddHostedService(sp => sp.GetRequiredService<SignalIngestPipeline>());

        services.AddHostedService<InvestigationWorker>();

        // Replaced by the real runner when the LLM stack is registered. TryAdd so registration
        // order cannot silently downgrade a working investigator to the escalate-only stub.
        services.TryAddScoped<IIncidentInvestigator, EscalateOnlyInvestigator>();

        return services;
    }
}
