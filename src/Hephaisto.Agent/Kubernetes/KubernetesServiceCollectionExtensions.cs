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

        // Bound directly rather than through IOptions because this decides what gets
        // registered at all, and IOptions is not resolvable while the collection is being
        // built.
        var enabled = configuration.GetSection(KubernetesOptions.SectionName)
            .Get<KubernetesOptions>()?.Enabled ?? true;

        if (!enabled)
        {
            // The client is still registered, and still the only thing that changes: it
            // throws a sentence naming the setting instead of a kubeconfig FileNotFound from
            // four frames down. Everything that reaches for the cluster therefore fails the
            // same way, explaining itself, rather than failing differently per call site.
            services.TryAddSingleton<IKubernetes>(_ => throw new InvalidOperationException(
                "Kubernetes:Enabled is false, so this process has no cluster client. That is "
                + "the demo and UI-only configuration; an agent expected to detect anything "
                + "must not run with it."));

            services.TryAddSingleton<KubernetesApi>();
            services.TryAddSingleton<OwnerCache>();
            services.TryAddSingleton<KubernetesReadTools>();

            // Deliberately NOT registering the real executor, RbacSelfCheck or the watcher.
            // Leaving the executor alone is what keeps the pipeline's RefusingActionExecutor,
            // and the two hosted services are precisely what made a cluster mandatory: one
            // runs forty-odd access reviews at boot, the other opens watches.
            services.AddHostedService<KubernetesDisabledAnnouncer>();

            return services;
        }

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

/// <summary>
/// Says, once per start and at warning level, that this process has no cluster.
/// </summary>
/// <remarks>
/// An agent with <c>Kubernetes:Enabled=false</c> detects nothing and is otherwise
/// indistinguishable from a healthy one: it serves the console, answers <c>/readyz</c> and
/// reports no errors, because nothing is wrong with it - it simply is not watching anything.
/// That is the failure mode this repository treats as the worst kind, so the disabled path is
/// audible rather than silent. It is a hosted service so the line lands in the startup
/// sequence beside the checks it replaces, not buried in DI.
/// </remarks>
internal sealed class KubernetesDisabledAnnouncer(
    ILogger<KubernetesDisabledAnnouncer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Kubernetes:Enabled is false. This process has NO cluster connection: it will not "
            + "watch, detect, investigate or act, and the executor refuses everything. The "
            + "console and the HTTP surface work. This is the demo configuration - if this is "
            + "a deployed agent, it is misconfigured.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
