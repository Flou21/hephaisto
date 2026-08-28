using System.Text;

using k8s;
using k8s.Models;

using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>One cell of the RBAC matrix: a verb against a resource, optionally in one namespace.</summary>
/// <param name="Verb">A Kubernetes verb, or a virtual one such as <c>escalate</c> or <c>impersonate</c>.</param>
/// <param name="Resource">Plural resource name, or the subject kind for an impersonation check.</param>
/// <param name="Group">API group. The empty string is the core group and is not a missing value.</param>
/// <param name="Namespace">Null means cluster-wide, which is what "in every namespace" means to a SelfSubjectAccessReview.</param>
/// <param name="Subresource">e.g. <c>log</c>, <c>exec</c>.</param>
/// <param name="Why">Written into the log line, so the matrix explains itself to a reviewer.</param>
public sealed record RbacProbe(
    string Verb,
    string Resource,
    string Group = "",
    string? Namespace = null,
    string? Subresource = null,
    string Why = "")
{
    public string Display
    {
        get
        {
            var resource = Group.Length == 0 ? Resource : $"{Resource}.{Group}";
            if (Subresource is { Length: > 0 })
            {
                resource += $"/{Subresource}";
            }

            return $"{Verb} {resource} in {(Namespace is { Length: > 0 } ns ? ns : "*")}";
        }
    }
}

/// <summary>The answer for one probe. <paramref name="Allowed"/> is null when the API could not be asked.</summary>
public sealed record RbacProbeResult(RbacProbe Probe, bool? Allowed, string? Reason);

/// <summary>Thrown at startup when the agent holds a verb it must never hold.</summary>
public sealed class RbacSelfCheckException(string message) : Exception(message);

/// <summary>
/// Asserts, before the agent serves anything, that its ServiceAccount does <b>not</b> hold the
/// verbs it must never hold - and warns about the read verbs it needs but is missing.
/// </summary>
/// <remarks>
/// <para>
/// This exists because RBAC is the outermost safety layer, the one that still holds if every
/// other check in this process is compromised, and it is also the layer that is edited by
/// hand in YAML. A ClusterRoleBinding pointed at the wrong ServiceAccount, or a
/// <c>resources: ["*"]</c> that was meant to be a list, grants secret access silently: nothing
/// fails, nothing logs, and the mistake surfaces the first time somebody audits it or the
/// first time it is exploited. Asking the API server directly turns that into a crash loop
/// within seconds of the rollout, which is a failure mode operators already know how to read.
/// </para>
/// <para>
/// The negative assertions are the important half. A missing read verb degrades an
/// investigation; a granted <c>get secrets</c> is read access to every credential in the
/// cluster, and there is no in-process control that meaningfully compensates for it.
/// SelfSubjectAccessReview is itself unprivileged - every authenticated identity may ask about
/// its own access - so this check needs no permission of its own.
/// </para>
/// <para>
/// Missing read verbs only warn. A half-applied ClusterRole should be obvious in the log, but
/// refusing to boot over it would mean a narrower-than-intended role takes the agent down
/// during the incident it was deployed to watch.
/// </para>
/// </remarks>
public sealed class RbacSelfCheck(
    KubernetesApi api,
    IOptions<KubernetesOptions> options,
    ILogger<RbacSelfCheck> logger) : IHostedService
{
    private readonly KubernetesOptions options = options.Value;

    /// <summary>
    /// Verbs whose presence is a defect, whatever granted them. Every entry is here because
    /// holding it would break a property the rest of the design depends on.
    /// </summary>
    public static IReadOnlyList<RbacProbe> Forbidden { get; } =
    [
        new("get", "secrets", Why: "read access to secrets is read access to every credential in the cluster"),
        new("list", "secrets", Why: "listing secrets leaks names and, with get, their contents"),
        new("watch", "secrets", Why: "a watch is a list that keeps giving"),

        // kube-system is where the cluster's own control plane runs. Deleting a pod there is
        // how an agent turns one incident into an outage of the thing that would have told it.
        new("delete", "pods", Namespace: "kube-system", Why: "kube-system is permanently protected"),
        new("deletecollection", "pods", Namespace: "kube-system", Why: "the bulk form of the same thing"),

        // Privilege escalation primitives. Any one of these makes every other RBAC limit
        // advisory, because the agent could grant itself whatever it lacked.
        new("create", "clusterrolebindings", "rbac.authorization.k8s.io", Why: "would let the agent grant itself anything"),
        new("create", "rolebindings", "rbac.authorization.k8s.io", Why: "the namespaced form of the same escalation"),
        new("escalate", "roles", "rbac.authorization.k8s.io", Why: "escalate bypasses the rule that you cannot grant what you lack"),
        new("escalate", "clusterroles", "rbac.authorization.k8s.io", Why: "cluster-wide form of escalate"),
        new("bind", "roles", "rbac.authorization.k8s.io", Why: "bind attaches an existing role to a new subject"),
        new("bind", "clusterroles", "rbac.authorization.k8s.io", Why: "cluster-wide form of bind"),
        new("impersonate", "users", Why: "impersonation makes the audit trail name somebody else"),
        new("impersonate", "groups", Why: "same, via group membership"),
        new("impersonate", "serviceaccounts", Why: "same, via another workload's identity"),

        // Unrecoverable data loss. DeletePvc is in ActionType only so that a plan naming it is
        // recorded and rejected with a reason - the API access behind it must not exist at all.
        new("delete", "persistentvolumeclaims", Why: "unrecoverable data loss; no confidence level justifies it"),
        new("delete", "persistentvolumes", Why: "same, one layer down"),

        // exec is a shell in a container: an arbitrary-code path that no policy engine sees.
        new("create", "pods", Subresource: "exec", Why: "exec is arbitrary code execution outside the action vocabulary"),
        new("create", "pods", Subresource: "attach", Why: "attach is the same reach by another name"),

        new("delete", "nodes", Why: "removing a node is not in the action vocabulary"),
        new("create", "serviceaccounts", Subresource: "token", Why: "minting tokens is identity forgery"),
    ];

    /// <summary>
    /// The read surface this layer actually calls. Kept in step with
    /// <see cref="KubernetesReadTools"/> so that a warning here means a real tool will fail
    /// later, not that this list drifted.
    /// </summary>
    public static IReadOnlyList<RbacProbe> Required { get; } =
    [
        new("list", "pods", Why: "list_pods, and the pod watch"),
        new("watch", "pods", Why: "the pod watch"),
        new("get", "pods", Why: "get_pod, describe_pod, who_owns"),
        new("get", "pods", Subresource: "log", Why: "get_pod_logs"),
        new("list", "events", Why: "get_events, and the event watch - the only place a FailedScheduling reason exists"),
        new("watch", "events", Why: "the event watch"),
        new("list", "nodes", Why: "list_nodes, and the node watch"),
        new("get", "nodes", Why: "get_node"),
        new("watch", "nodes", Why: "the node watch"),
        new("list", "deployments", "apps", Why: "list_deployments, get_rollout_history"),
        new("get", "deployments", "apps", Why: "get_workload, who_owns"),
        new("list", "statefulsets", "apps", Why: "list_statefulsets"),
        new("list", "daemonsets", "apps", Why: "list_daemonsets"),
        new("list", "replicasets", "apps", Why: "get_rollout_history, and the owner walk"),
        new("get", "replicasets", "apps", Why: "the owner walk from Pod to Deployment"),
        new("list", "controllerrevisions", "apps", Why: "get_rollout_history for StatefulSets and DaemonSets"),
        new("list", "jobs", "batch", Why: "the job watch"),
        new("get", "jobs", "batch", Why: "get_workload on a Job"),
        new("watch", "jobs", "batch", Why: "the job watch"),
        new("list", "endpoints", Why: "get_service_endpoints"),
        new("get", "services", Why: "get_service_endpoints needs the selector to explain an empty list"),
        new("list", "persistentvolumeclaims", Why: "list_pvcs"),
        new("list", "horizontalpodautoscalers", "autoscaling", Why: "list_hpa"),
        new("list", "pods", "metrics.k8s.io", Why: "get_resource_usage"),
        new("list", "nodes", "metrics.k8s.io", Why: "get_resource_usage"),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var forbidden = await RunAsync(Forbidden, cancellationToken).ConfigureAwait(false);
        var required = await RunAsync(Required, cancellationToken).ConfigureAwait(false);

        // One log line, not one per probe. A security review wants the whole matrix in a
        // single artefact it can copy; forty interleaved lines are not that.
        logger.LogInformation("RBAC self-check for cluster {Cluster}:\n{Matrix}", options.ClusterName, Render(forbidden, required));

        var missing = required.Where(r => r.Allowed != true).ToArray();
        if (missing.Length > 0)
        {
            logger.LogWarning(
                "RBAC self-check: {Count} read permission(s) the agent needs are NOT granted, so the "
                + "matching tools will fail during an investigation: {Missing}. This usually means a "
                + "half-applied ClusterRole.",
                missing.Length,
                string.Join("; ", missing.Select(m => m.Probe.Display)));
        }

        var violations = forbidden.Where(r => r.Allowed != false).ToArray();
        if (violations.Length == 0)
        {
            return;
        }

        var granted = violations.Where(v => v.Allowed == true).ToArray();
        var unknown = violations.Where(v => v.Allowed is null).ToArray();

        var message = new StringBuilder("RBAC self-check failed. ");
        if (granted.Length > 0)
        {
            message.Append(
                $"The agent holds {granted.Length} permission(s) it must never hold: " +
                string.Join("; ", granted.Select(v => $"{v.Probe.Display} ({v.Probe.Why})")) + ". ");
        }

        if (unknown.Length > 0)
        {
            // An unanswerable probe is not a pass. If the API server cannot be asked whether
            // the agent can read secrets, the honest state is "unknown", and booting on
            // unknown is how a check like this quietly stops being a check.
            message.Append(
                $"{unknown.Length} probe(s) could not be evaluated: " +
                string.Join("; ", unknown.Select(v => $"{v.Probe.Display}: {v.Reason}")) + ". ");
        }

        message.Append("Fix infra/app/rbac.yaml and redeploy.");

        if (options.RbacMode == RbacEnforcement.WarnOnly)
        {
            logger.LogError("{Message} Continuing only because Kubernetes:RbacMode is WarnOnly.", message.ToString());
            return;
        }

        throw new RbacSelfCheckException(message.ToString());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<IReadOnlyList<RbacProbeResult>> RunAsync(
        IReadOnlyList<RbacProbe> probes,
        CancellationToken ct)
    {
        var results = new RbacProbeResult[probes.Count];

        // Sequential on purpose. Forty SelfSubjectAccessReviews are forty cheap POSTs, and
        // firing them in parallel is the fastest way to have the API server's priority-and-
        // fairness queue reject the check that decides whether the agent may boot.
        for (var i = 0; i < probes.Count; i++)
        {
            results[i] = await ProbeAsync(probes[i], ct).ConfigureAwait(false);
        }

        return results;
    }

    private async Task<RbacProbeResult> ProbeAsync(RbacProbe probe, CancellationToken ct)
    {
        var review = new V1SelfSubjectAccessReview
        {
            Spec = new V1SelfSubjectAccessReviewSpec
            {
                ResourceAttributes = new V1ResourceAttributes
                {
                    Verb = probe.Verb,
                    Resource = probe.Resource,
                    Group = probe.Group,
                    Subresource = probe.Subresource,

                    // An empty namespace on a ResourceAttributes means "all namespaces", which
                    // is precisely the question being asked of the forbidden probes.
                    NamespaceProperty = probe.Namespace ?? string.Empty,
                },
            },
        };

        try
        {
            var response = await api.Authorization
                .CreateSelfSubjectAccessReviewAsync(review, cancellationToken: ct)
                .ConfigureAwait(false);

            return new RbacProbeResult(probe, response.Status?.Allowed ?? false, response.Status?.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RbacProbeResult(probe, null, ex.Message);
        }
    }

    private static string Render(
        IReadOnlyList<RbacProbeResult> forbidden,
        IReadOnlyList<RbacProbeResult> required)
    {
        var rows = new List<IReadOnlyList<string?>>(forbidden.Count + required.Count);

        foreach (var result in forbidden)
        {
            rows.Add(["MUST-NOT", result.Probe.Display, Verdict(result, expectedAllowed: false), result.Probe.Why]);
        }

        foreach (var result in required)
        {
            rows.Add(["NEEDS", result.Probe.Display, Verdict(result, expectedAllowed: true), result.Probe.Why]);
        }

        return TextTable.Render(["class", "permission", "verdict", "why"], rows, "no probes configured");
    }

    private static string Verdict(RbacProbeResult result, bool expectedAllowed) => result.Allowed switch
    {
        null => "UNKNOWN",
        true => expectedAllowed ? "ok (allowed)" : "VIOLATION (allowed)",
        false => expectedAllowed ? "MISSING (denied)" : "ok (denied)",
    };
}
