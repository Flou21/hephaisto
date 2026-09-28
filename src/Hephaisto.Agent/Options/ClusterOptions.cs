using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Kubernetes;

using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Options;

/// <summary>
/// The name of the cluster this agent runs in. Bound from <c>Cluster:*</c>; the chart sets it
/// from the required value <c>cluster.name</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One key, because there were three</b> (backlog #139). <c>Ingest:ClusterName</c>,
/// <c>Kubernetes:ClusterName</c> and <c>Investigation:Environment:ClusterName</c> each named the
/// cluster, the chart set none of them, and the defaults were the development machine's name. So
/// every install that was not that machine fingerprinted its signals with somebody else's
/// cluster and told the model that its metrics carry <c>cluster=studio-rancher-desktop</c> - a
/// label that then matched nothing in every query the model wrote.
/// </para>
/// <para>
/// The three still exist, because each layer reads its own options, but they are now filled from
/// this one by <see cref="ClusterServiceCollectionExtensions.AddHephaistoCluster"/>, and their code
/// defaults are empty. An empty name refuses to start: it is part of every signal fingerprint and
/// the label every query filters on, and no default can be right for a machine it has never seen.
/// </para>
/// </remarks>
public sealed class ClusterOptions
{
    public const string SectionName = "Cluster";

    /// <summary>
    /// The value of the <c>cluster</c> label on this cluster's metrics and logs, and the cluster
    /// in every fingerprint. Treat it as immutable once the cluster has reported: changing it
    /// re-keys every future signal, so nothing new dedupes against what came before.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

public static class ClusterServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="ClusterOptions"/>, refuses to start without a name, and copies the name
    /// into the three options that used to be set separately. Idempotent: the pipeline, the
    /// Kubernetes layer and the LLM layer each call it, so none of them depends on another having
    /// been registered first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The copy reads <paramref name="configuration"/> directly rather than
    /// <c>IOptions&lt;ClusterOptions&gt;</c>. Resolving the options would run their validation,
    /// and a test or the eval harness that builds a container without a cluster name would then
    /// fail on reading <i>ingest</i> options. The refusal belongs to host start, which is where
    /// <c>ValidateOnStart</c> puts it.
    /// </para>
    /// <para>
    /// <b>Cluster:Name wins.</b> A legacy key that is still set and agrees is harmless; one that
    /// disagrees is overridden and logged as a warning, because the likely cause is an
    /// <c>extraEnv</c> entry left over from before this key existed, and the operator should
    /// delete it rather than wonder which one is in effect.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHephaistoCluster(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(d => d.ServiceType == typeof(ClusterRegistered)))
        {
            return services;
        }

        services.AddSingleton<ClusterRegistered>();

        services.AddOptions<ClusterOptions>()
            .Bind(configuration.GetSection(ClusterOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.Name),
                "Cluster:Name must be set (chart value cluster.name). It is part of every signal "
                + "fingerprint and the `cluster` label every query the model writes filters on, and "
                + "no default can be right for a cluster it has never seen.")
            .ValidateOnStart();

        services.AddOptions<IngestOptions>()
            .PostConfigure<IServiceProvider>((o, sp) =>
                o.ClusterName = Resolve(configuration, "Ingest:ClusterName", o.ClusterName, sp.GetService<ILoggerFactory>()));

        services.AddOptions<KubernetesOptions>()
            .PostConfigure<IServiceProvider>((o, sp) =>
                o.ClusterName = Resolve(configuration, "Kubernetes:ClusterName", o.ClusterName, sp.GetService<ILoggerFactory>()));

        services.AddOptions<EnvironmentCardOptions>()
            .PostConfigure<IServiceProvider>((o, sp) =>
                o.ClusterName = Resolve(
                    configuration, "Investigation:Environment:ClusterName", o.ClusterName, sp.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>The name a layer should use: Cluster:Name when set, otherwise what it bound.</summary>
    internal static string Resolve(
        IConfiguration configuration,
        string legacyKey,
        string bound,
        ILoggerFactory? logs)
    {
        var name = configuration[$"{ClusterOptions.SectionName}:Name"]?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            return bound;
        }

        if (!string.IsNullOrWhiteSpace(bound) && !string.Equals(bound, name, StringComparison.Ordinal))
        {
            logs?.CreateLogger(typeof(ClusterOptions)).LogWarning(
                "{LegacyKey} is {Legacy} but Cluster:Name is {Name}; Cluster:Name wins. Remove "
                + "{LegacyKey} - it predates the one key that names the cluster.",
                legacyKey,
                bound,
                name,
                legacyKey);
        }

        return name;
    }

    /// <summary>Marks the registration as done, so a second call is a no-op.</summary>
    private sealed class ClusterRegistered;
}
