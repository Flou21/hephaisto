using Microsoft.Extensions.DependencyInjection.Extensions;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Agent.Investigations.Jobs;

public static class InvestigationJobServiceCollectionExtensions
{
    /// <summary>
    /// Investigation in a Job. Registered unconditionally so the executor can be read and reported
    /// on every install; every default resolves to in-process.
    /// </summary>
    public static IServiceCollection AddHephaistoInvestigationJob(this IServiceCollection services, IConfiguration configuration)
    {
        var mcpPort = configuration.GetValue<int?>("Mcp:Port") ?? 8083;

        services.AddOptions<InvestigationJobOptions>()
            .Bind(configuration.GetSection(InvestigationJobOptions.SectionName))
            .Validate(
                o => InvestigationExecutorResolver.Parse("env", o.Executor).Status != Core.Safety.ModeArmStatus.Malformed,
                "Investigation:Job:Executor must be inprocess or job. A typo would silently read as inprocess, "
                    + "which at startup is worth refusing rather than discovering in production.")
            .Validate(
                o => !o.Enabled || o.Port is > 0 and < 65536,
                "Investigation:Job:Port must be a TCP port.")
            .Validate(
                o => !o.Enabled || (o.Port != 8080 && o.Port != mcpPort),
                "Investigation:Job:Port must be its own port: nothing else answers on it, and it answers nothing else.")
            .Validate(
                o => !o.Enabled || Uri.TryCreate(o.EndpointUrl, UriKind.Absolute, out _),
                "Investigation:Job:EndpointUrl must be an absolute URL when Investigation:Job:Enabled is set.")
            .Validate(
                o => o.Deadline > TimeSpan.FromMinutes(4),
                "Investigation:Job:Deadline must exceed four minutes; the runner reserves three to report.")
            .Validate(
                o => o.MaxConcurrentJobs >= 0 && o.MaxJobsPerHour >= 0 && o.MaxTurns > 0 && o.MaxCostUsd >= 0,
                "Investigation:Job caps must not be negative, and MaxTurns must be positive.")
            .Validate(
                o => o.Sdk is "real" or "fake",
                "Investigation:Job:Sdk must be real or fake.")
            .ValidateOnStart();

        services.TryAddSingleton<IInvestigationExecutorSwitch, InvestigationExecutorSwitch>();

        // The live Job investigations and their tokens. In memory on purpose - see the type.
        services.TryAddSingleton<InvestigationJobSessions>();

        // The runner asks it whether a Job takes a run. Always registered: with every default it
        // decides in-process, which is v0.11. A singleton, because it counts the Jobs it started.
        services.TryAddSingleton<IInvestigationJobLoop, KubernetesInvestigationJobLoop>();

        // Orphaned investigator Jobs - an agent restart leaves every running one without a session.
        // Only with a cluster client; it checks the enable flag on every pass.
        if (!string.Equals(configuration["Kubernetes:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
            services.AddHostedService<InvestigatorJobSweeper>();

        return services;
    }
}
