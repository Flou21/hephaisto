using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Thrown when the facts the policy engine needs could not be read. The caller must escalate
/// rather than evaluate policy on what it managed to get.
/// </summary>
/// <remarks>
/// This type exists because of the direction partial facts fail in. <see cref="ClusterFacts"/>
/// has no "unknown" - a workload it could not read is <c>null</c>, and a null workload means
/// gates 7, 8 and 9 are skipped rather than failed. A missing fact therefore *removes* safety
/// checks, silently, and the resulting Allow looks identical to a considered one. So the
/// gatherer refuses to return a half-built record at all.
/// </remarks>
public sealed class ClusterFactsUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Reads, at decision time, everything <see cref="PolicyEngine"/> is allowed to know.
/// </summary>
/// <remarks>
/// <para>
/// The policy engine is a pure function over facts passed in, which is what makes it
/// exhaustively unit-testable without a cluster. This class is the other half of that
/// bargain: all of the I/O, in one place, so the engine needs none.
/// </para>
/// <para>
/// It lives in <c>Pipeline</c> rather than <c>Kubernetes</c> because it reads Postgres too -
/// the action budget is as much a fact about the world as the replica count. Filing it under
/// Kubernetes would describe half of it.
/// </para>
/// <para>
/// <b>Until v0.2.0 nothing built this.</b> <c>InvestigationCoordinator</c> passed a record
/// carrying only the clock, the mode and the quarantine stamp, so gates 3, 7, 8-fractional, 9,
/// 10 and 13's budget downgrade could not fail: the policy engine ran for real against facts
/// that could not contradict it. Every one of those gates has unit tests, all of which passed,
/// because the tests supply the facts the caller did not.
/// </para>
/// </remarks>
public sealed class ClusterFactsGatherer(
    KubernetesApi api,
    IActionRepository actions,
    Microsoft.Extensions.Options.IOptionsMonitor<PolicyOptions> policyOptions,
    IClock clock,
    ILogger<ClusterFactsGatherer> logger)
{
    public async Task<ClusterFacts> GatherAsync(Incident incident, AgentMode mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var target = incident.Target;
        var now = clock.UtcNow;

        try
        {
            var budget = await actions
                .ReadBudgetAsync(incident.Id, target, mode, ct)
                .ConfigureAwait(false);

            var namespaceLabels = await ReadNamespaceLabelsAsync(target.Namespace, ct).ConfigureAwait(false);
            var (workload, workloadLabels) = await ReadWorkloadAsync(target, now, ct).ConfigureAwait(false);
            var targetLabels = await ReadTargetLabelsAsync(target, workloadLabels, ct).ConfigureAwait(false);
            var node = await ReadNodeAsync(target.NodeName, ct).ConfigureAwait(false);
            var workloadQuarantine = await actions.GetWorkloadQuarantineAsync(target, ct).ConfigureAwait(false);
            var unhealthy = await ClusterUnhealthyFractionAsync(ct).ConfigureAwait(false);

            return new ClusterFacts
            {
                Now = now,
                Mode = mode,
                Workload = workload,
                Node = node,
                TargetLabels = targetLabels,
                NamespaceLabels = namespaceLabels,
                RecentActionsOnWorkload = budget.ActionsOnWorkloadLastHour,
                LastActionOnWorkloadAt = budget.LastActionOnWorkloadAt,
                ActionsOnIncident = budget.ActionsOnIncident,
                ActionsClusterWideLastHour = budget.ActionsClusterWideLastHour,
                ActionsClusterWideLastDay = budget.ActionsClusterWideLastDay,
                // The later of the two. The incident's is set by flap suppression; the
                // workload's by the oscillation detector, and that one outlives the incident
                // it was learned from - which is the whole point of recording it per workload.
                QuarantinedUntil = Later(incident.QuarantinedUntil, workloadQuarantine),
                ClusterUnhealthyFraction = unhealthy,

                InMaintenanceWindow = InMaintenanceWindow(now),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ClusterFactsUnavailableException)
        {
            throw new ClusterFactsUnavailableException(
                $"Could not read the facts needed to judge an action on {target.WorkloadKey}.", ex);
        }
    }

    /// <summary>
    /// Whether any configured freeze covers this moment.
    /// </summary>
    /// <remarks>
    /// The gate this feeds has existed since the MVP with nothing on the other end of it.
    /// Empty stays the default - no window is a real answer, and inventing one would be a
    /// control nobody asked for - but the difference now is that a configured window works.
    /// </remarks>
    private bool InMaintenanceWindow(DateTimeOffset now)
    {
        var windows = policyOptions.CurrentValue.MaintenanceWindows;

        foreach (var window in windows)
        {
            if (!window.IsValid)
            {
                // Loud, and not treated as a freeze. A typo that silently froze the agent
                // would look exactly like a healthy quiet cluster.
                logger.LogError(
                    "Maintenance window '{Window}' does not parse (expected HH:mm) and is being ignored.",
                    window.Describe());

                continue;
            }

            if (window.Contains(now))
            {
                logger.LogInformation("In maintenance window {Window}; nothing will be acted on.", window.Describe());

                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// The namespace's own opt-in label. A namespace that cannot be read is not an opt-in.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadNamespaceLabelsAsync(
        string @namespace, CancellationToken ct)
    {
        var ns = await api.Core.ReadNamespaceAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);

        return Copy(ns.Metadata?.Labels);
    }

    /// <summary>
    /// Replica, generation and revision facts for the owning controller, plus its labels.
    /// </summary>
    /// <remarks>
    /// Keyed on the OWNER, never the object the signal arrived about: a crash-looping
    /// Deployment produces a new pod name every couple of minutes, and blast radius, rollout
    /// state and revision age are all properties of the controller rather than of whichever
    /// pod happened to fail last.
    /// </remarks>
    private async Task<(WorkloadFacts? Facts, IDictionary<string, string>? Labels)> ReadWorkloadAsync(
        TargetRef target, DateTimeOffset now, CancellationToken ct)
    {
        var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;
        var ns = target.Namespace;

        switch (kind)
        {
            case "Deployment":
            {
                var d = await api.Apps.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
                var (current, previous) = await RevisionAgesAsync(d, now, ct).ConfigureAwait(false);

                return (new WorkloadFacts
                {
                    Key = target.WorkloadKey,
                    Kind = kind,
                    DesiredReplicas = d.Spec?.Replicas ?? 0,
                    ReadyReplicas = d.Status?.ReadyReplicas ?? 0,
                    UpdatedReplicas = d.Status?.UpdatedReplicas ?? 0,
                    Generation = d.Metadata?.Generation ?? 0,
                    ObservedGeneration = d.Status?.ObservedGeneration ?? 0,
                    YoungestPodAge = await YoungestPodAgeAsync(ns, d.Spec?.Selector, now, ct).ConfigureAwait(false),
                    CurrentRevisionAge = current,
                    PreviousRevisionHealthyFor = previous,
                }, d.Metadata?.Labels);
            }

            case "StatefulSet":
            {
                var s = await api.Apps.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);

                return (new WorkloadFacts
                {
                    Key = target.WorkloadKey,
                    Kind = kind,
                    DesiredReplicas = s.Spec?.Replicas ?? 0,
                    ReadyReplicas = s.Status?.ReadyReplicas ?? 0,
                    UpdatedReplicas = s.Status?.UpdatedReplicas ?? 0,
                    Generation = s.Metadata?.Generation ?? 0,
                    ObservedGeneration = s.Status?.ObservedGeneration ?? 0,
                    YoungestPodAge = await YoungestPodAgeAsync(ns, s.Spec?.Selector, now, ct).ConfigureAwait(false),
                }, s.Metadata?.Labels);
            }

            case "DaemonSet":
            {
                var d = await api.Apps.ReadNamespacedDaemonSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);

                // A DaemonSet's "replicas" is however many nodes it is scheduled on, so the
                // blast-radius maths is the same shape with different field names.
                return (new WorkloadFacts
                {
                    Key = target.WorkloadKey,
                    Kind = kind,
                    DesiredReplicas = d.Status?.DesiredNumberScheduled ?? 0,
                    ReadyReplicas = d.Status?.NumberReady ?? 0,
                    UpdatedReplicas = d.Status?.UpdatedNumberScheduled ?? 0,
                    Generation = d.Metadata?.Generation ?? 0,
                    ObservedGeneration = d.Status?.ObservedGeneration ?? 0,
                    YoungestPodAge = await YoungestPodAgeAsync(ns, d.Spec?.Selector, now, ct).ConfigureAwait(false),
                }, d.Metadata?.Labels);
            }

            default:
                // A bare Pod, a Job, or something with no controller. There is no replica set
                // to reason about, so the blast-radius and rollout gates have nothing to say -
                // which is honest here, unlike a workload read that failed.
                logger.LogDebug(
                    "No workload facts for kind {Kind}; blast-radius and rollout gates will not apply.", kind);
                return (null, null);
        }
    }

    /// <summary>
    /// A rollout close enough to this incident to be worth stating in the prompt, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrow, and deliberately separate from <see cref="GatherAsync"/>. The full
    /// fact-gathering runs immediately before the policy engine judges a plan, which is after
    /// the investigation has finished - far too late to save the investigation a step. This is
    /// two API reads against one Deployment, run before the investigation prompt is composed.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> Its caller is composing a prompt, not judging an action, so the
    /// asymmetry that governs <c>GatherAsync</c> is inverted here: an unread fact there means
    /// default-deny, because acting on an unknown cluster is unsafe. Here it means one fewer
    /// hint in a prompt, and failing an investigation because a convenience read failed would
    /// be a bad trade. Anything unexpected returns null and is logged.
    /// </para>
    /// </remarks>
    public async Task<RolloutCorrelation?> RecentRolloutAsync(Incident incident, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var target = incident.Target;
        var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        // Deployments only. StatefulSet and DaemonSet history is in ControllerRevisions, which
        // carry no images and no useful "what changed" - and rollback_deployment, the action
        // this fact exists to inform, is Deployment-only anyway.
        if (kind != "Deployment")
        {
            return null;
        }

        try
        {
            var deployment = await api.Apps
                .ReadNamespacedDeploymentAsync(name, target.Namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            var uid = deployment.Metadata?.Uid;

            if (uid is null)
            {
                return null;
            }

            var replicaSets = await api.Apps
                .ListNamespacedReplicaSetAsync(target.Namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            var owned = replicaSets.Items
                .Where(rs => rs.Metadata?.OwnerReferences?.Any(o => o.Uid == uid) == true)
                .OrderByDescending(ClusterFactsRules.RevisionOf)
                .ToList();

            if (owned.Count == 0 || owned[0].Metadata?.CreationTimestamp is not { } rolledOutAt)
            {
                return null;
            }

            var rolledOut = new DateTimeOffset(DateTime.SpecifyKind(rolledOutAt, DateTimeKind.Utc));
            var openedAfter = incident.OpenedAt - rolledOut;

            // Negative means the rollout happened AFTER the incident opened - which is a real
            // case, because the agent may be looking at a workload somebody is mid-deploy on.
            // That is not a cause, and offering it as one would invite a rollback of the fix.
            if (openedAfter < TimeSpan.Zero || openedAfter > RolloutCorrelation.RelevanceWindow)
            {
                return null;
            }

            TimeSpan? previousLasted = owned.Count >= 2
                && owned[1].Metadata?.CreationTimestamp is { } previousCreated
                    ? rolledOutAt - previousCreated
                    : null;

            return new RolloutCorrelation
            {
                Revision = ClusterFactsRules.RevisionOf(owned[0]),
                IncidentOpenedAfter = openedAfter,
                PreviousRevisionLastedFor = previousLasted,
                Images = Images(owned[0]),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex, "Could not read rollout history for {Workload}; the incident card goes without it.",
                target.WorkloadKey);

            return null;
        }
    }

    private static string? Images(k8s.Models.V1ReplicaSet replicaSet)
    {
        var containers = replicaSet.Spec?.Template?.Spec?.Containers;

        if (containers is null || containers.Count == 0)
        {
            return null;
        }

        return string.Join(", ", containers.Select(c => c.Image).Where(i => !string.IsNullOrWhiteSpace(i)));
    }

    /// <summary>
    /// Age of the current revision, and how long the one before it lasted.
    /// </summary>
    /// <remarks>
    /// A Deployment's history IS its ReplicaSets - the revision number is an annotation on
    /// each one and there is no history object to read. <c>PreviousRevisionHealthyFor</c> is a
    /// proxy: how long the previous revision was the live one, measured between the two
    /// creation timestamps. It is not a health measurement, and the gate that reads it treats
    /// "survived a while" as the signal. Anything better needs a rollout-status history nobody
    /// keeps.
    /// </remarks>
    private async Task<(TimeSpan? Current, TimeSpan? Previous)> RevisionAgesAsync(
        V1Deployment deployment, DateTimeOffset now, CancellationToken ct)
    {
        var uid = deployment.Metadata?.Uid;

        if (uid is null)
        {
            return (null, null);
        }

        var replicaSets = await api.Apps
            .ListNamespacedReplicaSetAsync(deployment.Metadata!.NamespaceProperty, cancellationToken: ct)
            .ConfigureAwait(false);

        var owned = replicaSets.Items
            .Where(rs => rs.Metadata?.OwnerReferences?.Any(o => o.Uid == uid) == true)
            .OrderByDescending(ClusterFactsRules.RevisionOf)
            .ToList();

        if (owned.Count == 0)
        {
            return (null, null);
        }

        var currentCreated = owned[0].Metadata?.CreationTimestamp;
        TimeSpan? current = currentCreated is { } c ? now - c : null;

        if (owned.Count < 2)
        {
            return (current, null);
        }

        var previousCreated = owned[1].Metadata?.CreationTimestamp;
        TimeSpan? previous = currentCreated is { } cc && previousCreated is { } pc ? cc - pc : null;

        return (current, previous);
    }

    /// <summary>
    /// Age of the youngest pod under a selector. Gate 7 refuses to act on pods that have not
    /// had a fair chance to become healthy, and the youngest is the one that decides that.
    /// </summary>
    private async Task<TimeSpan?> YoungestPodAgeAsync(
        string @namespace, V1LabelSelector? selector, DateTimeOffset now, CancellationToken ct)
    {
        var labelSelector = ClusterFactsRules.LabelSelector(selector);

        if (labelSelector is null)
        {
            return null;
        }

        var pods = await api.Core
            .ListNamespacedPodAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
            .ConfigureAwait(false);

        var youngest = pods.Items
            .Select(p => p.Metadata?.CreationTimestamp)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Max();

        return youngest == default ? null : now - youngest;
    }

    /// <summary>
    /// Labels on the target itself, for the protected-label check.
    /// </summary>
    /// <remarks>
    /// The workload's labels are the base, because that is where a team puts an opt-out. A
    /// readable pod's labels are merged over the top. Merging is safe in exactly one direction
    /// and this is it: <see cref="PolicyOptions.ProtectedLabels"/> only ever produces denials,
    /// so a larger set can refuse more and permit nothing extra. A pod that has already gone -
    /// routine, since these are usually crash-looping - is not an error.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> ReadTargetLabelsAsync(
        TargetRef target, IDictionary<string, string>? workloadLabels, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in workloadLabels ?? new Dictionary<string, string>())
        {
            labels[key] = value;
        }

        if (!string.Equals(target.Kind, "Pod", StringComparison.OrdinalIgnoreCase))
        {
            return labels;
        }

        try
        {
            var pod = await api.Core
                .ReadNamespacedPodAsync(target.Name, target.Namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            foreach (var (key, value) in pod.Metadata?.Labels ?? new Dictionary<string, string>())
            {
                labels[key] = value;
            }
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogDebug(
                "Pod {Namespace}/{Name} is gone; using the workload's labels for the protected-label check.",
                target.Namespace, target.Name);
        }

        return labels;
    }

    private async Task<NodeFacts?> ReadNodeAsync(string? nodeName, CancellationToken ct)
    {
        if (nodeName is not { Length: > 0 })
        {
            return null;
        }

        V1Node node;

        try
        {
            node = await api.Core.ReadNodeAsync(nodeName, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // A node that does not exist is a fact we do not have, not a fact we could not
            // read, and the difference decides whether an action can be judged at all. Every
            // other failure here still propagates and still default-denies.
            //
            // Narrow on purpose - 404 only. This is the second line of defence behind #92,
            // where a mis-mapped `instance` label put `10.244.0.6:8080` in this argument and
            // denied every action on every alert without a `node` label. The mapping is the
            // bug and is fixed; this is here because the failure was silent, total, and
            // presented as an agent that had simply stopped acting.
            logger.LogWarning(
                "Incident target names node '{Node}', which does not exist. Judging without node facts.",
                nodeName);

            return null;
        }

        var pods = await api.Core
            .ListPodForAllNamespacesAsync(fieldSelector: $"spec.nodeName={nodeName}", cancellationToken: ct)
            .ConfigureAwait(false);

        return new NodeFacts
        {
            Name = nodeName,
            Unschedulable = node.Spec?.Unschedulable ?? false,
            PodCount = pods.Items.Count,
            MemoryPressure = ClusterFactsRules.HasCondition(node, "MemoryPressure"),
            DiskPressure = ClusterFactsRules.HasCondition(node, "DiskPressure"),
        };
    }

    /// <summary>
    /// The fraction of pods cluster-wide that are unhealthy. Above the configured ceiling,
    /// restarting one pod is the wrong response to what is clearly a cluster-level event.
    /// </summary>
    /// <remarks>
    /// One list of every pod in the cluster, per action proposal. That is affordable because
    /// proposals are rare - a handful an hour at the budget caps, against an investigation
    /// that already costs several seconds and several cents. It would not be affordable on a
    /// hot path, and this is deliberately not on one.
    /// </remarks>
    private async Task<double> ClusterUnhealthyFractionAsync(CancellationToken ct)
    {
        var pods = await api.Core.ListPodForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false);

        return ClusterFactsRules.UnhealthyFraction(pods.Items);
    }

    private static IReadOnlyDictionary<string, string> Copy(IDictionary<string, string>? source) =>
        source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);
}
