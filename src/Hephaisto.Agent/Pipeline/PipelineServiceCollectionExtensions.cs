using Microsoft.Extensions.DependencyInjection.Extensions;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Web;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Policy;

namespace Hephaisto.Agent.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddHephaistoPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IngestOptions>(configuration.GetSection(IngestOptions.SectionName));

        // The policy engine's configuration. This binding was missing until v0.2.0, and its
        // absence was invisible: IOptionsMonitor<PolicyOptions> resolves happily to a
        // default-constructed instance, whose AllowedNamespaces is empty, so gate 2 denied
        // every action for the right-looking reason. The chart has been setting
        // Policy__AllowedNamespaces__N since the write Role existed and nothing read it.
        services.Configure<PolicyOptions>(configuration.GetSection(PolicyOptions.SectionName));

        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<HephaistoMetrics>();
        services.AddSingleton<InvestigationQueue>();

        // Singleton: it describes what this process is doing right now, so every
        // reader has to see the same set. Scoped would give each request its own
        // empty one, which reads as "nothing is running" no matter what is.
        services.AddSingleton<InvestigationTracker>();

        // Runs once at startup. Without it, anything queued or in flight when the
        // process last stopped stays Investigating in Postgres forever.
        services.AddHostedService<StrandedIncidentRequeue>();

        // Scoped: it reads the action budget through the scoped repository, and those counts
        // must come from the same DbContext as the decision they inform.
        services.AddScoped<ClusterFactsGatherer>();

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

        // Same shape, opposite risk. The investigator stub exists so a misordered registration
        // still diagnoses; this one exists so a misordered registration still cannot ACT. The
        // real executor is registered by AddHephaistoKubernetes, which owns the API handle -
        // so a host with no Kubernetes client gets an executor that refuses rather than one
        // that half works.
        services.TryAddScoped<IActionExecutor, RefusingActionExecutor>();

        return services;
    }
}
