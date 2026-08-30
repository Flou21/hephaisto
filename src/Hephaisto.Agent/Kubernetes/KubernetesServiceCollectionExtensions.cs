using k8s;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>
/// Wiring for the Kubernetes layer. The composition root calls this and nothing else.
/// </summary>
public static class KubernetesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the client, the RBAC self-check, the watcher and the read tools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registration order is load-bearing.</b> Hosted services start in the order they are
    /// added and a throw from one aborts host startup, so <see cref="RbacSelfCheck"/> is added
    /// before <see cref="KubernetesWatcherService"/>: an agent holding permissions it must
    /// never hold must not have watched, classified or investigated anything before that is
    /// discovered.
    /// </para>
    /// <para>
    /// The read tools are registered as <see cref="KubernetesReadTools"/> rather than as a
    /// list of <c>AIFunction</c>. The investigation stream composes its own tool set from
    /// several sources - these, the Grafana MCP tools, the virtual <c>conclude</c> tool - and a
    /// bare <c>IReadOnlyList&lt;AIFunction&gt;</c> in the container would collide with the
    /// others. Call <see cref="KubernetesReadTools.CreateFunctions"/> where the set is assembled.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHephaistoKubernetes(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<KubernetesOptions>()
            .Bind(configuration.GetSection(KubernetesOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ClusterName),
                "Kubernetes:ClusterName must be set - it is part of every signal fingerprint, and an "
                + "empty one lets two clusters reporting into one database collide.")
            .Validate(
                o => o.SignalQueueCapacity > 0,
                "Kubernetes:SignalQueueCapacity must be positive. There is deliberately no unbounded "
                + "setting: an unbounded queue turns a node restart into an OOM of the agent.")
            .ValidateOnStart();

        // AddMetrics is idempotent; calling it here means this layer does not depend on
        // ServiceDefaults having been wired first.
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IKubernetes>(_ => CreateClient(configuration));
        services.TryAddSingleton<KubernetesApi>();
        services.TryAddSingleton<OwnerCache>();
        services.TryAddSingleton<KubernetesReadTools>();

        // The real executor, registered where the API handle is - phase 3 of the loop. Scoped,
        // because it writes through the scoped action repository and its saves must land in
        // that DbContext. This REPLACES the RefusingActionExecutor the pipeline TryAdds, and
        // the direction matters: a host with no Kubernetes client keeps the one that refuses.
        services.AddScoped<Pipeline.ActionEventMirror>();
        services.AddScoped<Pipeline.IActionExecutor, Pipeline.ActionExecutor>();

        // Before the watcher. See the remarks above.
        services.AddHostedService<RbacSelfCheck>();
        services.AddHostedService<KubernetesWatcherService>();

        return services;
    }

    /// <summary>
    /// In-cluster when there is a ServiceAccount token to use, kubeconfig otherwise.
    /// </summary>
    /// <remarks>
    /// The branch is on <see cref="KubernetesClientConfiguration.IsInCluster"/> - the presence
    /// of the projected token - and never on a context name. Branching on the kubeconfig
    /// context name is exactly the bug that cost `~/dev` a day: this machine calls the cluster
    /// <c>rancher-desktop</c> and the laptop calls the same cluster
    /// <c>studio-rancher-desktop</c>, so any behaviour keyed on that string is right on one
    /// machine and silently wrong on the other.
    /// </remarks>
    private static IKubernetes CreateClient(IConfiguration configuration)
    {
        var options = configuration.GetSection(KubernetesOptions.SectionName).Get<KubernetesOptions>()
            ?? new KubernetesOptions();

        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : BuildLocalConfig(options);

        return new k8s.Kubernetes(config);
    }

    private static KubernetesClientConfiguration BuildLocalConfig(KubernetesOptions options) =>
        KubernetesClientConfiguration.BuildConfigFromConfigFile(
            kubeconfigPath: options.KubeconfigPath,
            currentContext: options.KubeconfigContext);
}
