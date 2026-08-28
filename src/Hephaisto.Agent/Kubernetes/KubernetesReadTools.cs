using System.Globalization;
using System.Text;
using System.Text.Json;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using Hephaisto.Core.Digest;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>
/// The read-only Kubernetes tool surface handed to the investigating model.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no mutation in this file, and its absence is a security property rather than a
/// scoping decision.</b> No delete, patch, evict, scale or create call appears here, not even
/// as an unused helper, so no prompt - however crafted, however convincing the log line that
/// carried it - can reach a mutating Kubernetes API through a tool the model holds. Acting on
/// the cluster is a separate stream behind the policy engine, over a closed
/// <c>ActionType</c> vocabulary that pure C# turns into API calls.
/// </para>
/// <para>
/// Two things matter as much as the tool list. First, <b>output is compact text, never JSON</b>:
/// the JSON of one pod spec is thousands of tokens of fields that have never explained a
/// failure, and an investigation that reads ten objects spends its whole context window on
/// punctuation. Second, <b>no raw log ever reaches the model</b> - everything goes through
/// <see cref="LogDigester"/>, which is what makes a crash-looping pod's three interesting
/// lines visible instead of buried under ten thousand identical ones.
/// </para>
/// <para>
/// Every description is written for the model rather than for a developer: what the tool
/// returns, and when to reach for it. That text is the only documentation the model gets, and
/// a tool it does not understand is a tool it calls at the wrong moment and then reasons from.
/// </para>
/// </remarks>
public sealed class KubernetesReadTools(
    KubernetesApi api,
    OwnerCache owners,
    IOptions<KubernetesOptions> options,
    TimeProvider time,
    ILogger<KubernetesReadTools> logger)
{
    private readonly KubernetesOptions options = options.Value;

    /// <summary>
    /// Builds the <see cref="AIFunction"/> list. Descriptions are addressed to the model.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            ListPodsAsync,
            "list_pods",
            "Lists pods in a namespace as a table: name, ready containers, phase, status reason, "
            + "restart count, age and node. Start here to see which pods are unhealthy and how "
            + "many. Restart count plus age tells you whether a problem is new or long-standing."),

        AIFunctionFactory.Create(
            GetPodAsync,
            "get_pod",
            "One pod's status: phase, node, per-container state and reason, last termination "
            + "(exit code and reason), restart count, readiness, image and resource requests/limits. "
            + "Use after list_pods to find out WHY a specific pod is unhealthy. The container "
            + "state reason is the discriminator between CrashLoopBackOff, ImagePullBackOff and "
            + "CreateContainerConfigError - read it rather than guessing from the message."),

        AIFunctionFactory.Create(
            DescribePodAsync,
            "describe_pod",
            "The full pod object with server bookkeeping stripped (managedFields, resourceVersion "
            + "and most annotations removed), plus the events for that pod. Use when get_pod is "
            + "not enough - to read probe settings (timeoutSeconds, periodSeconds, "
            + "failureThreshold), volume mounts, secret and configMap references, or affinity "
            + "rules. Larger than get_pod, so prefer get_pod first."),

        AIFunctionFactory.Create(
            GetEventsAsync,
            "get_events",
            "Kubernetes events for a namespace, deduplicated by reason and message with an "
            + "occurrence count and a time range. Events carry the REASON something happened, "
            + "which metrics never do - the scheduler's FailedScheduling message, naming each "
            + "node and why it was rejected, exists nowhere else. Pass an object name to narrow "
            + "to one pod or workload."),

        AIFunctionFactory.Create(
            GetPodLogsAsync,
            "get_pod_logs",
            "Container logs, digested: repeated lines collapsed to a count with an exemplar, "
            + "every panic/fatal/exception/OOM/refused/timeout line kept verbatim with context, "
            + "and the last lines kept as-is. "
            + "IMPORTANT: after any restart, pass previous=true. The current container was "
            + "started AFTER the failure, so its logs cannot contain the crash; the previous "
            + "container's logs are the ones that explain it. Note that an OOMKilled container "
            + "usually has no logs at all - the kernel killed it without warning, and that "
            + "absence is itself evidence rather than a broken tool."),

        AIFunctionFactory.Create(
            ListDeploymentsAsync,
            "list_deployments",
            "Deployments in a namespace: ready/desired replicas, up-to-date, available, age, and "
            + "whether the controller has observed the current spec. Use to see the shape of a "
            + "namespace and to spot workloads that are not fully rolled out."),

        AIFunctionFactory.Create(
            ListStatefulSetsAsync,
            "list_statefulsets",
            "StatefulSets in a namespace: ready/desired replicas, current and updated revision, "
            + "and age. StatefulSet pods roll one at a time, so one stuck pod blocks the rest - "
            + "check this before concluding a rollout is merely slow."),

        AIFunctionFactory.Create(
            ListDaemonSetsAsync,
            "list_daemonsets",
            "DaemonSets in a namespace: desired, current, ready, up-to-date and available counts "
            + "plus age. Desired follows the node count, so a DaemonSet that is not ready "
            + "everywhere often points at one bad node rather than at the workload."),

        AIFunctionFactory.Create(
            GetWorkloadAsync,
            "get_workload",
            "One workload (Deployment, StatefulSet, DaemonSet or Job) in detail: replica counts, "
            + "conditions, container images, resource requests and limits, and - importantly - "
            + "generation versus observedGeneration. When they differ, the controller has not yet "
            + "acted on the current spec, which means a rollout is in flight or the controller is "
            + "wedged; treating that as a steady state is a common misreading."),

        AIFunctionFactory.Create(
            GetRolloutHistoryAsync,
            "get_rollout_history",
            "Revision history for a Deployment, StatefulSet or DaemonSet: revision number, age, "
            + "replica counts and images, newest first. Use whenever a problem has a sharp onset. "
            + "A revision created minutes before the symptoms started is the most likely cause, "
            + "and it is also what makes a rollback the right recommendation instead of a guess."),

        AIFunctionFactory.Create(
            ListNodesAsync,
            "list_nodes",
            "All nodes: ready status, any pressure conditions, taints, allocatable CPU and memory, "
            + "pod count and age. Use to establish whether a problem is workload-shaped or "
            + "node-shaped before investigating individual pods."),

        AIFunctionFactory.Create(
            GetNodeAsync,
            "get_node",
            "One node in detail: every condition with its reason, taints, capacity versus "
            + "allocatable, the summed CPU and memory REQUESTS of the pods placed on it against "
            + "that allocatable, and the pods themselves. This is how you tell 'the node is out "
            + "of memory' from 'the scheduler cannot fit this request', which have different fixes."),

        AIFunctionFactory.Create(
            GetResourceUsageAsync,
            "get_resource_usage",
            "Live CPU and memory usage from metrics.k8s.io. With a namespace, per-pod usage in "
            + "that namespace; with no namespace, per-node usage. This is ACTUAL consumption, "
            + "not requests or limits - compare it against the limits from get_pod to see how "
            + "close a container is to being OOMKilled. Returns a plain message if the metrics "
            + "API is not installed, which is not an error worth retrying."),

        AIFunctionFactory.Create(
            ListHpaAsync,
            "list_hpa",
            "HorizontalPodAutoscalers in a namespace: target workload, min/max/current replicas, "
            + "metric targets versus current values, and conditions. An HPA at its maximum, or "
            + "one unable to read its metric, explains both under-capacity and replica counts "
            + "that keep changing under you."),

        AIFunctionFactory.Create(
            ListPvcsAsync,
            "list_pvcs",
            "PersistentVolumeClaims in a namespace: phase, capacity, access modes, StorageClass "
            + "and age. A claim stuck in Pending explains a pod stuck in Pending. Note that "
            + "capacity here is the requested size - how full the volume is comes from PromQL "
            + "(kubelet_volume_stats_*), not from this tool."),

        AIFunctionFactory.Create(
            GetServiceEndpointsAsync,
            "get_service_endpoints",
            "The backends behind a Service: ready and not-ready addresses, the ports, and the "
            + "selector. Call this whenever the report is 'the service is down' - an EMPTY "
            + "endpoint list is one of the most common root causes and means no pod is both "
            + "matching the selector and passing its readiness probe. The output says so "
            + "explicitly, and shows which pods match the selector and why each is not ready."),

        AIFunctionFactory.Create(
            WhoOwnsAsync,
            "who_owns",
            "Walks ownerReferences from an object up to its top-level controller and prints the "
            + "chain, e.g. Pod -> ReplicaSet -> Deployment. Use this FIRST on any pod. Pod names "
            + "are ephemeral - a crash-looping Deployment produces a new one every couple of "
            + "minutes - so any conclusion tied to a pod name is stale almost immediately. Reason "
            + "about the controller."),
    ];

    // ------------------------------------------------------------------
    // Pods
    // ------------------------------------------------------------------

    private async Task<string> ListPodsAsync(
        string @namespace,
        string? labelSelector = null,
        CancellationToken ct = default)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(ListPodsAsync), async () =>
        {
            var pods = await api.Core
                .ListNamespacedPodAsync(@namespace, labelSelector: NullIfBlank(labelSelector), cancellationToken: ct)
                .ConfigureAwait(false);

            var now = time.GetUtcNow();

            return TextTable.Render(
                ["name", "ready", "phase", "reason", "restarts", "age", "node"],
                pods.Items.Select(pod => new string?[]
                {
                    pod.Metadata?.Name,
                    ReadyRatio(pod),
                    pod.Status?.Phase,
                    PodReason(pod),
                    (pod.Status?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0).ToString(CultureInfo.InvariantCulture),
                    TextTable.Age(pod.Metadata?.CreationTimestamp, now),
                    pod.Spec?.NodeName,
                }),
                $"no pods in namespace {@namespace}"
                    + (labelSelector is { Length: > 0 } ? $" matching {labelSelector}" : string.Empty),
                options.MaxRows);
        }).ConfigureAwait(false);
    }

    private async Task<string> GetPodAsync(string @namespace, string name, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetPodAsync), async () =>
        {
            var pod = await api.Core.ReadNamespacedPodAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
            var now = time.GetUtcNow();

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"pod {@namespace}/{name}\n");
            sb.Append(CultureInfo.InvariantCulture, $"phase: {pod.Status?.Phase}  node: {pod.Spec?.NodeName ?? "<unscheduled>"}  age: {TextTable.Age(pod.Metadata?.CreationTimestamp, now)}\n");

            if (pod.Status?.Reason is { Length: > 0 } reason)
            {
                sb.Append(CultureInfo.InvariantCulture, $"reason: {reason} - {pod.Status.Message}\n");
            }

            var conditions = pod.Status?.Conditions ?? [];
            sb.Append("\nconditions:\n");
            sb.Append(TextTable.Render(
                ["type", "status", "reason", "message", "since"],
                conditions.Select(c => new string?[]
                {
                    c.Type, c.Status, c.Reason, c.Message, TextTable.Age(c.LastTransitionTime, now),
                }),
                "  (none reported)"));

            sb.Append("\ncontainers:\n");
            sb.Append(TextTable.Render(
                ["container", "ready", "state", "reason", "restarts", "last exit", "image", "requests", "limits"],
                AllContainerStatuses(pod).Select(c => new string?[]
                {
                    c.Name,
                    c.Ready ? "yes" : "no",
                    StateName(c.State),
                    StateReason(c.State),
                    c.RestartCount.ToString(CultureInfo.InvariantCulture),
                    LastTermination(c),
                    c.Image,
                    Resources(c.Resources?.Requests),
                    Resources(c.Resources?.Limits),
                }),
                "  (no container statuses yet)"));

            return sb.ToString();
        }).ConfigureAwait(false);
    }

    private async Task<string> DescribePodAsync(string @namespace, string name, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(DescribePodAsync), async () =>
        {
            var pod = await api.Core.ReadNamespacedPodAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);

            // DigestDescribe drops managedFields and last-applied-configuration, either of
            // which is routinely larger than everything a human would actually look at.
            var described = LogDigester.DigestDescribe(KubernetesYaml.Serialize(pod));

            var events = await EventsForAsync(@namespace, name, ct).ConfigureAwait(false);

            return described + "\nevents for this pod:\n" + events;
        }).ConfigureAwait(false);
    }

    private async Task<string> GetPodLogsAsync(
        string @namespace,
        string name,
        string? container = null,
        bool previous = false,
        CancellationToken ct = default)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetPodLogsAsync), async () =>
        {
            await using var stream = await api.Core.ReadNamespacedPodLogAsync(
                name,
                @namespace,
                container: NullIfBlank(container),
                limitBytes: options.LogLimitBytes,
                previous: previous,
                tailLines: options.LogTailLines,
                timestamps: true,
                cancellationToken: ct).ConfigureAwait(false);

            using var reader = new StreamReader(stream);
            var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(raw))
            {
                // Said in words, because an empty string reads as a broken tool and invites a
                // retry. For an OOMKilled container this is the expected result and is evidence.
                return $"no {(previous ? "previous " : string.Empty)}logs for {@namespace}/{name}"
                    + $"{(container is { Length: > 0 } ? "/" + container : string.Empty)}. "
                    + "A container that was OOMKilled, or that never started (image pull or config "
                    + "error), writes nothing - absence here is consistent with those causes.";
            }

            var digest = LogDigester.Digest(raw, LogDigestOptions.Default);

            return $"{(previous ? "previous" : "current")} container logs for {@namespace}/{name}"
                + $"{(container is { Length: > 0 } ? "/" + container : string.Empty)}\n"
                + digest.Text;
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    private async Task<string> GetEventsAsync(
        string @namespace,
        string? objectName = null,
        CancellationToken ct = default)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetEventsAsync), () => EventsForAsync(@namespace, NullIfBlank(objectName), ct))
            .ConfigureAwait(false);
    }

    private async Task<string> EventsForAsync(string @namespace, string? objectName, CancellationToken ct)
    {
        var events = await api.Core.ListNamespacedEventAsync(
            @namespace,
            fieldSelector: objectName is { Length: > 0 } ? $"involvedObject.name={objectName}" : null,
            cancellationToken: ct).ConfigureAwait(false);

        var now = time.GetUtcNow();
        var groups = EventDigest.Dedupe(events.Items);

        return TextTable.Render(
            ["type", "reason", "count", "objects", "first", "last", "message"],
            groups.Select(g => new string?[]
            {
                g.Type,
                g.Reason,
                g.Count.ToString(CultureInfo.InvariantCulture),
                g.DistinctObjects == 1 ? g.SampleObject : $"{g.DistinctObjects} objects",
                TextTable.Age(g.FirstSeen, now),
                TextTable.Age(g.LastSeen, now),
                g.Message,
            }),
            $"no events in namespace {@namespace}"
                + (objectName is { Length: > 0 } ? $" for {objectName}" : string.Empty)
                + ". Note that the API server discards events after about an hour, so this also "
                + "means nothing has happened recently - not that nothing ever did.",
            options.MaxRows);
    }

    // ------------------------------------------------------------------
    // Workloads
    // ------------------------------------------------------------------

    private async Task<string> ListDeploymentsAsync(string @namespace, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(ListDeploymentsAsync), async () =>
        {
            var list = await api.Apps.ListNamespacedDeploymentAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);
            var now = time.GetUtcNow();

            return TextTable.Render(
                ["name", "ready", "up-to-date", "available", "age", "spec observed"],
                list.Items.Select(d => new string?[]
                {
                    d.Metadata?.Name,
                    $"{d.Status?.ReadyReplicas ?? 0}/{d.Spec?.Replicas ?? 0}",
                    (d.Status?.UpdatedReplicas ?? 0).ToString(CultureInfo.InvariantCulture),
                    (d.Status?.AvailableReplicas ?? 0).ToString(CultureInfo.InvariantCulture),
                    TextTable.Age(d.Metadata?.CreationTimestamp, now),
                    Observed(d.Metadata?.Generation, d.Status?.ObservedGeneration),
                }),
                $"no deployments in namespace {@namespace}",
                options.MaxRows);
        }).ConfigureAwait(false);
    }

    private async Task<string> ListStatefulSetsAsync(string @namespace, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(ListStatefulSetsAsync), async () =>
        {
            var list = await api.Apps.ListNamespacedStatefulSetAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);
            var now = time.GetUtcNow();

            return TextTable.Render(
                ["name", "ready", "current rev", "updated rev", "age", "spec observed"],
                list.Items.Select(s => new string?[]
                {
                    s.Metadata?.Name,
                    $"{s.Status?.ReadyReplicas ?? 0}/{s.Spec?.Replicas ?? 0}",
                    s.Status?.CurrentRevision,
                    s.Status?.UpdateRevision,
                    TextTable.Age(s.Metadata?.CreationTimestamp, now),
                    Observed(s.Metadata?.Generation, s.Status?.ObservedGeneration),
                }),
                $"no statefulsets in namespace {@namespace}",
                options.MaxRows);
        }).ConfigureAwait(false);
    }

    private async Task<string> ListDaemonSetsAsync(string @namespace, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(ListDaemonSetsAsync), async () =>
        {
            var list = await api.Apps.ListNamespacedDaemonSetAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);
            var now = time.GetUtcNow();

            return TextTable.Render(
                ["name", "desired", "current", "ready", "up-to-date", "available", "age", "spec observed"],
                list.Items.Select(d => new string?[]
                {
                    d.Metadata?.Name,
                    d.Status?.DesiredNumberScheduled.ToString(CultureInfo.InvariantCulture),
                    d.Status?.CurrentNumberScheduled.ToString(CultureInfo.InvariantCulture),
                    d.Status?.NumberReady.ToString(CultureInfo.InvariantCulture),
                    (d.Status?.UpdatedNumberScheduled ?? 0).ToString(CultureInfo.InvariantCulture),
                    (d.Status?.NumberAvailable ?? 0).ToString(CultureInfo.InvariantCulture),
                    TextTable.Age(d.Metadata?.CreationTimestamp, now),
                    Observed(d.Metadata?.Generation, d.Status?.ObservedGeneration),
                }),
                $"no daemonsets in namespace {@namespace}",
                options.MaxRows);
        }).ConfigureAwait(false);
    }

    private async Task<string> GetWorkloadAsync(string @namespace, string kind, string name, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetWorkloadAsync), async () =>
        {
            var sb = new StringBuilder();
            var now = time.GetUtcNow();

            switch (kind)
            {
                case "Deployment":
                {
                    var d = await api.Apps.ReadNamespacedDeploymentAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                    Header(sb, "Deployment", @namespace, name, d.Metadata, now);
                    sb.Append(CultureInfo.InvariantCulture,
                        $"replicas: desired={d.Spec?.Replicas ?? 0} ready={d.Status?.ReadyReplicas ?? 0} "
                        + $"updated={d.Status?.UpdatedReplicas ?? 0} available={d.Status?.AvailableReplicas ?? 0} "
                        + $"unavailable={d.Status?.UnavailableReplicas ?? 0}\n");
                    sb.Append(CultureInfo.InvariantCulture, $"strategy: {d.Spec?.Strategy?.Type}\n");
                    Generation(sb, d.Metadata?.Generation, d.Status?.ObservedGeneration);
                    Conditions(sb, d.Status?.Conditions?.Select(c => (c.Type, c.Status, c.Reason, c.Message, c.LastTransitionTime)), now);
                    Containers(sb, d.Spec?.Template?.Spec);
                    break;
                }

                case "StatefulSet":
                {
                    var s = await api.Apps.ReadNamespacedStatefulSetAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                    Header(sb, "StatefulSet", @namespace, name, s.Metadata, now);
                    sb.Append(CultureInfo.InvariantCulture,
                        $"replicas: desired={s.Spec?.Replicas ?? 0} ready={s.Status?.ReadyReplicas ?? 0} "
                        + $"current={s.Status?.CurrentReplicas ?? 0} updated={s.Status?.UpdatedReplicas ?? 0}\n");
                    sb.Append(CultureInfo.InvariantCulture,
                        $"revisions: current={s.Status?.CurrentRevision} update={s.Status?.UpdateRevision}\n");
                    Generation(sb, s.Metadata?.Generation, s.Status?.ObservedGeneration);
                    Conditions(sb, s.Status?.Conditions?.Select(c => (c.Type, c.Status, c.Reason, c.Message, c.LastTransitionTime)), now);
                    Containers(sb, s.Spec?.Template?.Spec);
                    break;
                }

                case "DaemonSet":
                {
                    var d = await api.Apps.ReadNamespacedDaemonSetAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                    Header(sb, "DaemonSet", @namespace, name, d.Metadata, now);
                    sb.Append(CultureInfo.InvariantCulture,
                        $"nodes: desired={d.Status?.DesiredNumberScheduled} current={d.Status?.CurrentNumberScheduled} "
                        + $"ready={d.Status?.NumberReady} updated={d.Status?.UpdatedNumberScheduled ?? 0} "
                        + $"unavailable={d.Status?.NumberUnavailable ?? 0}\n");
                    Generation(sb, d.Metadata?.Generation, d.Status?.ObservedGeneration);
                    Conditions(sb, d.Status?.Conditions?.Select(c => (c.Type, c.Status, c.Reason, c.Message, (DateTime?)null)), now);
                    Containers(sb, d.Spec?.Template?.Spec);
                    break;
                }

                case "Job":
                {
                    var j = await api.Batch.ReadNamespacedJobAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                    Header(sb, "Job", @namespace, name, j.Metadata, now);
                    sb.Append(CultureInfo.InvariantCulture,
                        $"completions={j.Spec?.Completions?.ToString(CultureInfo.InvariantCulture) ?? "1"} "
                        + $"parallelism={j.Spec?.Parallelism?.ToString(CultureInfo.InvariantCulture) ?? "1"} "
                        + $"backoffLimit={j.Spec?.BackoffLimit?.ToString(CultureInfo.InvariantCulture) ?? "6"}\n");
                    sb.Append(CultureInfo.InvariantCulture,
                        $"status: active={j.Status?.Active ?? 0} succeeded={j.Status?.Succeeded ?? 0} failed={j.Status?.Failed ?? 0}\n");
                    Conditions(sb, j.Status?.Conditions?.Select(c => (c.Type, c.Status, c.Reason, c.Message, c.LastTransitionTime)), now);
                    Containers(sb, j.Spec?.Template?.Spec);
                    break;
                }

                default:
                    return $"ERROR: get_workload does not know the kind '{kind}'. "
                        + "Supported kinds: Deployment, StatefulSet, DaemonSet, Job.";
            }

            return sb.ToString();
        }).ConfigureAwait(false);
    }

    private async Task<string> GetRolloutHistoryAsync(string @namespace, string kind, string name, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetRolloutHistoryAsync), async () =>
        {
            var now = time.GetUtcNow();

            if (kind == "Deployment")
            {
                var deployment = await api.Apps.ReadNamespacedDeploymentAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                var uid = deployment.Metadata?.Uid;

                // A Deployment's history IS its ReplicaSets: the revision number lives in an
                // annotation on each one, and there is no separate history object to read.
                var replicaSets = await api.Apps.ListNamespacedReplicaSetAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);

                var owned = replicaSets.Items
                    .Where(rs => rs.Metadata?.OwnerReferences?.Any(o => o.Uid == uid) == true)
                    .OrderByDescending(rs => Revision(rs.Metadata))
                    .ToList();

                return TextTable.Render(
                    ["revision", "replicaset", "age", "replicas", "ready", "images", "change-cause"],
                    owned.Select(rs => new string?[]
                    {
                        Revision(rs.Metadata).ToString(CultureInfo.InvariantCulture),
                        rs.Metadata?.Name,
                        TextTable.Age(rs.Metadata?.CreationTimestamp, now),
                        (rs.Spec?.Replicas ?? 0).ToString(CultureInfo.InvariantCulture),
                        (rs.Status?.ReadyReplicas ?? 0).ToString(CultureInfo.InvariantCulture),
                        Images(rs.Spec?.Template?.Spec),
                        Annotation(rs.Metadata, "kubernetes.io/change-cause"),
                    }),
                    $"no ReplicaSets own Deployment {@namespace}/{name}, so it has no recorded history");
            }

            if (kind is "StatefulSet" or "DaemonSet")
            {
                var revisions = await api.Apps.ListNamespacedControllerRevisionAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);

                var owned = revisions.Items
                    .Where(r => r.Metadata?.OwnerReferences?.Any(o => o.Kind == kind && o.Name == name) == true)
                    .OrderByDescending(r => r.Revision)
                    .ToList();

                return TextTable.Render(
                    ["revision", "controllerrevision", "age"],
                    owned.Select(r => new string?[]
                    {
                        r.Revision.ToString(CultureInfo.InvariantCulture),
                        r.Metadata?.Name,
                        TextTable.Age(r.Metadata?.CreationTimestamp, now),
                    }),
                    $"no ControllerRevisions for {kind} {@namespace}/{name}");
            }

            return $"ERROR: get_rollout_history supports Deployment, StatefulSet and DaemonSet, not '{kind}'.";
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Nodes
    // ------------------------------------------------------------------

    private async Task<string> ListNodesAsync(CancellationToken ct) =>
        await GuardAsync(nameof(ListNodesAsync), async () =>
        {
            var nodes = await api.Core.ListNodeAsync(cancellationToken: ct).ConfigureAwait(false);
            var pods = await api.Core.ListPodForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false);
            var now = time.GetUtcNow();

            var podsPerNode = pods.Items
                .Where(p => p.Spec?.NodeName is { Length: > 0 })
                .GroupBy(p => p.Spec!.NodeName!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            return TextTable.Render(
                ["name", "ready", "pressure", "taints", "alloc cpu", "alloc mem", "pods", "age"],
                nodes.Items.Select(n => new string?[]
                {
                    n.Metadata?.Name,
                    Condition(n, "Ready"),
                    Pressure(n),
                    n.Spec?.Taints is { Count: > 0 } taints
                        ? string.Join(",", taints.Select(t => $"{t.Key}={t.Value}:{t.Effect}"))
                        : "-",
                    Quantity(n.Status?.Allocatable, "cpu"),
                    Quantity(n.Status?.Allocatable, "memory"),
                    podsPerNode.GetValueOrDefault(n.Metadata?.Name ?? string.Empty).ToString(CultureInfo.InvariantCulture),
                    TextTable.Age(n.Metadata?.CreationTimestamp, now),
                }),
                "no nodes returned, which means the cluster API is answering but reporting no nodes");
        }).ConfigureAwait(false);

    private async Task<string> GetNodeAsync(string name, CancellationToken ct) =>
        await GuardAsync(nameof(GetNodeAsync), async () =>
        {
            var node = await api.Core.ReadNodeAsync(name, cancellationToken: ct).ConfigureAwait(false);

            // fieldSelector rather than a client-side filter: on a busy cluster this is the
            // difference between transferring one node's pods and transferring all of them.
            var pods = await api.Core.ListPodForAllNamespacesAsync(
                fieldSelector: $"spec.nodeName={name}",
                cancellationToken: ct).ConfigureAwait(false);

            var now = time.GetUtcNow();
            var sb = new StringBuilder();

            sb.Append(CultureInfo.InvariantCulture, $"node {name}  age {TextTable.Age(node.Metadata?.CreationTimestamp, now)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"unschedulable: {(node.Spec?.Unschedulable == true ? "YES (cordoned)" : "no")}\n");

            sb.Append("\nconditions:\n");
            sb.Append(TextTable.Render(
                ["type", "status", "reason", "message", "since"],
                (node.Status?.Conditions ?? []).Select(c => new string?[]
                {
                    c.Type, c.Status, c.Reason, c.Message, TextTable.Age(c.LastTransitionTime, now),
                }),
                "  (none)"));

            sb.Append("\ntaints:\n");
            sb.Append(TextTable.Render(
                ["key", "value", "effect"],
                (node.Spec?.Taints ?? []).Select(t => new string?[] { t.Key, t.Value, t.Effect }),
                "  (none)"));

            // Requests, not usage. The scheduler places pods by request, so "insufficient
            // memory" from FailedScheduling is arithmetic on this table and not on live usage -
            // a node can be 20% used and still unable to fit anything.
            var cpuRequests = pods.Items.Sum(p => Sum(p, "cpu"));
            var memRequests = pods.Items.Sum(p => Sum(p, "memory"));

            sb.Append("\ncapacity vs allocatable vs requests:\n");
            sb.Append(TextTable.Render(
                ["resource", "capacity", "allocatable", "requested by pods", "requested %"],
                new IReadOnlyList<string?>[]
                {
                    new string?[]
                    {
                        "cpu",
                        Quantity(node.Status?.Capacity, "cpu"),
                        Quantity(node.Status?.Allocatable, "cpu"),
                        cpuRequests.ToString("F2", CultureInfo.InvariantCulture),
                        Percent(cpuRequests, Value(node.Status?.Allocatable, "cpu")),
                    },
                    new string?[]
                    {
                        "memory",
                        Quantity(node.Status?.Capacity, "memory"),
                        Quantity(node.Status?.Allocatable, "memory"),
                        Bytes(memRequests),
                        Percent(memRequests, Value(node.Status?.Allocatable, "memory")),
                    },
                },
                "  (no resource information)"));

            sb.Append(CultureInfo.InvariantCulture, $"\npods on this node ({pods.Items.Count}):\n");
            sb.Append(TextTable.Render(
                ["namespace", "pod", "phase", "restarts", "cpu req", "mem req", "mem limit"],
                pods.Items.Select(p => new string?[]
                {
                    p.Metadata?.NamespaceProperty,
                    p.Metadata?.Name,
                    p.Status?.Phase,
                    (p.Status?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0).ToString(CultureInfo.InvariantCulture),
                    Sum(p, "cpu").ToString("F2", CultureInfo.InvariantCulture),
                    Bytes(Sum(p, "memory")),

                    // Called out because a pod with no memory limit is the usual cause of a
                    // whole node going under - see the NodePressure runbook.
                    LimitOrNone(p, "memory"),
                }),
                "  (none)",
                options.MaxRows));

            return sb.ToString();
        }).ConfigureAwait(false);

    // ------------------------------------------------------------------
    // Metrics, autoscalers, storage, endpoints, ownership
    // ------------------------------------------------------------------

    private async Task<string> GetResourceUsageAsync(
        string? @namespace = null,
        CancellationToken ct = default)
    {
        if (@namespace is { Length: > 0 } && Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetResourceUsageAsync), async () =>
        {
            try
            {
                if (@namespace is { Length: > 0 })
                {
                    var raw = await api.CustomObjects.ListNamespacedCustomObjectAsync(
                        "metrics.k8s.io", "v1beta1", @namespace, "pods", cancellationToken: ct).ConfigureAwait(false);

                    return RenderPodMetrics(raw, @namespace);
                }

                var nodeRaw = await api.CustomObjects.ListClusterCustomObjectAsync(
                    "metrics.k8s.io", "v1beta1", "nodes", cancellationToken: ct).ConfigureAwait(false);

                return RenderNodeMetrics(nodeRaw);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // metrics-server is optional. Saying so plainly stops the model retrying and
                // stops it treating the gap as a finding about the workload.
                return "the metrics.k8s.io API is not available on this cluster (metrics-server is "
                    + "not installed or not ready). Live usage is unavailable; use PromQL "
                    + "(container_memory_working_set_bytes, container_cpu_usage_seconds_total) instead.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<string> ListHpaAsync(string @namespace, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(ListHpaAsync), async () =>
        {
            var list = await api.Autoscaling
                .ListNamespacedHorizontalPodAutoscalerAsync(@namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            var now = time.GetUtcNow();

            return TextTable.Render(
                ["name", "target", "min", "max", "current", "desired", "metrics", "conditions", "age"],
                list.Items.Select(h => new string?[]
                {
                    h.Metadata?.Name,
                    $"{h.Spec?.ScaleTargetRef?.Kind}/{h.Spec?.ScaleTargetRef?.Name}",
                    (h.Spec?.MinReplicas ?? 1).ToString(CultureInfo.InvariantCulture),
                    h.Spec?.MaxReplicas.ToString(CultureInfo.InvariantCulture),
                    (h.Status?.CurrentReplicas ?? 0).ToString(CultureInfo.InvariantCulture),
                    (h.Status?.DesiredReplicas ?? 0).ToString(CultureInfo.InvariantCulture),
                    HpaMetrics(h),
                    h.Status?.Conditions is { Count: > 0 } conditions
                        ? string.Join(",", conditions.Select(c => $"{c.Type}={c.Status}"))
                        : "-",
                    TextTable.Age(h.Metadata?.CreationTimestamp, now),
                }),
                $"no HorizontalPodAutoscalers in namespace {@namespace}",
                options.MaxRows);
        }).ConfigureAwait(false);
    }

    private async Task<string> ListPvcsAsync(string @namespace, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(ListPvcsAsync), async () =>
        {
            var list = await api.Core
                .ListNamespacedPersistentVolumeClaimAsync(@namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            var now = time.GetUtcNow();

            return TextTable.Render(
                ["name", "phase", "capacity", "requested", "access modes", "storageclass", "volume", "age"],
                list.Items.Select(p => new string?[]
                {
                    p.Metadata?.Name,
                    p.Status?.Phase,
                    Quantity(p.Status?.Capacity, "storage"),
                    p.Spec?.Resources?.Requests is { } requests && requests.TryGetValue("storage", out var q)
                        ? q.ToString()
                        : "-",
                    p.Spec?.AccessModes is { Count: > 0 } modes ? string.Join(",", modes) : "-",
                    p.Spec?.StorageClassName,
                    p.Spec?.VolumeName,
                    TextTable.Age(p.Metadata?.CreationTimestamp, now),
                }),
                $"no PersistentVolumeClaims in namespace {@namespace}",
                options.MaxRows);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Endpoints for a Service, with emptiness stated rather than implied.
    /// </summary>
    /// <remarks>
    /// An empty Endpoints list is a top-5 root cause of "the service is down", and returning
    /// <c>[]</c> for it is the worst possible answer: it reads as "nothing to report" and the
    /// model moves on. So the empty case is a sentence that names the cause - no pod is both
    /// matching the selector and passing readiness - and then shows which pods do match and why
    /// each one is not ready, which is the next question either way.
    /// </remarks>
    private async Task<string> GetServiceEndpointsAsync(string @namespace, string name, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(GetServiceEndpointsAsync), async () =>
        {
            var service = await api.Core.ReadNamespacedServiceAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
            var selector = service.Spec?.Selector;

            V1Endpoints? endpoints = null;
            try
            {
                endpoints = await api.Core.ReadNamespacedEndpointsAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No Endpoints object at all is the same story as an empty one, told worse.
            }

            var subsets = endpoints?.Subsets ?? [];
            var ready = subsets.Sum(s => s.Addresses?.Count ?? 0);
            var notReady = subsets.Sum(s => s.NotReadyAddresses?.Count ?? 0);

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"service {@namespace}/{name} (type {service.Spec?.Type}, clusterIP {service.Spec?.ClusterIP})\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"selector: {(selector is { Count: > 0 } ? string.Join(",", selector.Select(kv => $"{kv.Key}={kv.Value}")) : "<none>")}\n");
            sb.Append(CultureInfo.InvariantCulture, $"ports: {Ports(service)}\n\n");

            if (ready == 0)
            {
                sb.Append("*** THIS SERVICE HAS NO READY ENDPOINTS ***\n");
                sb.Append(CultureInfo.InvariantCulture,
                    $"Every request to it fails at the network layer. {notReady} address(es) are present but NOT ready. This means no pod is simultaneously matching the selector and passing its readiness probe - so the cause is either the selector matching nothing, or the matching pods failing readiness.\n\n");

                if (selector is { Count: > 0 })
                {
                    var matching = await api.Core.ListNamespacedPodAsync(
                        @namespace,
                        labelSelector: string.Join(",", selector.Select(kv => $"{kv.Key}={kv.Value}")),
                        cancellationToken: ct).ConfigureAwait(false);

                    sb.Append(CultureInfo.InvariantCulture, $"pods matching the selector ({matching.Items.Count}):\n");
                    sb.Append(TextTable.Render(
                        ["pod", "phase", "ready", "reason", "restarts"],
                        matching.Items.Select(p => new string?[]
                        {
                            p.Metadata?.Name,
                            p.Status?.Phase,
                            ReadyRatio(p),
                            PodReason(p) ?? NotReadyReason(p),
                            (p.Status?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0).ToString(CultureInfo.InvariantCulture),
                        }),
                        "  NO PODS MATCH THIS SELECTOR AT ALL - the selector's labels do not exist on "
                        + "any pod in this namespace. Check the workload's pod template labels."));
                }
                else
                {
                    sb.Append("The service has no selector, so its Endpoints object is managed manually "
                        + "or by an external controller.\n");
                }

                return sb.ToString();
            }

            sb.Append(CultureInfo.InvariantCulture, $"{ready} ready endpoint(s), {notReady} not ready\n");
            sb.Append(TextTable.Render(
                ["address", "state", "target"],
                subsets.SelectMany(s =>
                    (s.Addresses ?? []).Select(a => new string?[] { a.Ip, "ready", Target(a) })
                        .Concat((s.NotReadyAddresses ?? []).Select(a => new string?[] { a.Ip, "NOT ready", Target(a) }))),
                "  (no addresses)",
                options.MaxRows));

            return sb.ToString();
        }).ConfigureAwait(false);
    }

    private async Task<string> WhoOwnsAsync(string @namespace, string kind, string name, CancellationToken ct)
    {
        if (Reject(@namespace) is { } error)
        {
            return error;
        }

        return await GuardAsync(nameof(WhoOwnsAsync), async () =>
        {
            var meta = await owners.FetchAsync(kind, @namespace, name, ct).ConfigureAwait(false);
            if (meta is null)
            {
                return $"no {kind} named {name} in namespace {@namespace}, or its kind is not one this "
                    + "tool can read (Pod, ReplicaSet, Deployment, StatefulSet, DaemonSet, Job, CronJob, Node).";
            }

            await owners.WarmAsync(meta, @namespace, ct).ConfigureAwait(false);

            var chain = new List<string> { $"{kind}/{name}" };
            var current = meta;

            for (var depth = 0; depth < OwnerWalker.MaxDepth; depth++)
            {
                var owner = current?.OwnerReferences?.FirstOrDefault(o => o.Controller == true)
                    ?? current?.OwnerReferences?.FirstOrDefault();

                if (owner is null)
                {
                    break;
                }

                chain.Add($"{owner.Kind}/{owner.Name}");
                current = owners.Lookup(owner.Kind, @namespace, owner.Name);
            }

            var top = OwnerWalker.TopController(meta, @namespace, owners.Lookup);

            return $"ownership chain: {string.Join(" -> ", chain)}\n"
                + $"top-level controller: {(top is { } t ? $"{t.Kind}/{t.Name}" : $"{kind}/{name} (it has no controller)")}\n"
                + "Reason about the top-level controller. Pod names change on every restart, so a "
                + "conclusion keyed on one is stale as soon as the pod is replaced.";
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Scoping and failure handling
    // ------------------------------------------------------------------

    /// <summary>
    /// Namespace scoping, applied <b>before</b> any request leaves the process.
    /// </summary>
    /// <remarks>
    /// Checking afterwards would mean the data has already been fetched, and a tool that
    /// fetches and then declines to return has still read it - into a process that is about to
    /// serialise something into a prompt. Returning a text error rather than throwing is
    /// deliberate too: an exception ends the tool call and often the investigation, while an
    /// error string is a result the model can read and correct.
    /// </remarks>
    private string? Reject(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return "ERROR: a namespace is required. Cluster-wide listing is not available through "
                + "this tool; name the namespace you mean.";
        }

        // Validated as a DNS-1123 label before it is ever concatenated into an API path.
        foreach (var c in @namespace)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.')
            {
                return $"ERROR: '{@namespace}' is not a valid Kubernetes namespace name.";
            }
        }

        if (@namespace.Length > 63)
        {
            return $"ERROR: '{@namespace}' is not a valid Kubernetes namespace name.";
        }

        if (options.DeniedNamespaces.Contains(@namespace))
        {
            return $"ERROR: namespace '{@namespace}' is out of scope for this agent and cannot be read.";
        }

        if (options.ReadableNamespaces.Count > 0 && !options.ReadableNamespaces.Contains(@namespace))
        {
            return $"ERROR: namespace '{@namespace}' is out of scope. Readable namespaces: "
                + $"{string.Join(", ", options.ReadableNamespaces.Order(StringComparer.Ordinal))}.";
        }

        return null;
    }

    /// <summary>
    /// Turns an API failure into a sentence the model can act on.
    /// </summary>
    /// <remarks>
    /// A 403 in particular has to be reported as itself: "forbidden" means the agent's RBAC is
    /// narrower than this tool needs, and a model told only "the call failed" concludes
    /// something about the cluster instead of about its own permissions.
    /// </remarks>
    private async Task<string> GuardAsync(string tool, Func<Task<string>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            var status = ex.Response?.StatusCode;
            logger.LogWarning(ex, "Kubernetes tool {Tool} failed with {Status}", tool, status);

            return status switch
            {
                System.Net.HttpStatusCode.NotFound => "ERROR: not found. The object does not exist - it may have "
                    + "been deleted, or the name or namespace is wrong.",
                System.Net.HttpStatusCode.Forbidden => "ERROR: forbidden. Hephaisto's RBAC does not grant this "
                    + "read. This is a limitation of the agent, not a fact about the cluster; say so rather than "
                    + "inferring anything from it.",
                _ => $"ERROR: the Kubernetes API returned {status}: {ex.Message}",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Kubernetes tool {Tool} failed", tool);
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // Formatting helpers
    // ------------------------------------------------------------------

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IEnumerable<V1ContainerStatus> AllContainerStatuses(V1Pod pod) =>
        (pod.Status?.InitContainerStatuses ?? []).Concat(pod.Status?.ContainerStatuses ?? []);

    private static string ReadyRatio(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null || statuses.Count == 0)
        {
            return "0/0";
        }

        return $"{statuses.Count(c => c.Ready)}/{statuses.Count}";
    }

    /// <summary>The reason a pod is unhealthy, preferring the container state over the pod phase.</summary>
    private static string? PodReason(V1Pod pod)
    {
        foreach (var status in AllContainerStatuses(pod))
        {
            if (status.State?.Waiting?.Reason is { Length: > 0 } waiting)
            {
                return waiting;
            }

            // The last termination reason survives the restart, which is what makes OOMKilled
            // visible in a list where the container is currently Waiting or even Running.
            if (status.LastState?.Terminated?.Reason is { Length: > 0 } terminated
                && terminated != "Completed")
            {
                return terminated;
            }
        }

        return pod.Status?.Reason;
    }

    private static string? NotReadyReason(V1Pod pod) =>
        pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready" && c.Status != "True")?.Reason;

    private static string StateName(V1ContainerState? state) => state switch
    {
        { Running: not null } => "running",
        { Waiting: not null } => "waiting",
        { Terminated: not null } => "terminated",
        _ => "-",
    };

    private static string? StateReason(V1ContainerState? state) =>
        state?.Waiting?.Reason ?? state?.Terminated?.Reason;

    private static string LastTermination(V1ContainerStatus status)
    {
        var terminated = status.LastState?.Terminated;
        return terminated is null
            ? "-"
            : $"exit {terminated.ExitCode} ({terminated.Reason})";
    }

    private static string Resources(IDictionary<string, ResourceQuantity>? map) =>
        map is null or { Count: 0 }
            ? "none"
            : string.Join(",", map.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>
    /// Surfaces generation drift as words. The two numbers matching is the invariant a reader
    /// needs; printing them raw makes it a puzzle.
    /// </summary>
    private static string Observed(long? generation, long? observed) =>
        generation == observed ? "yes" : $"NO ({observed} of {generation})";

    private static void Generation(StringBuilder sb, long? generation, long? observed)
    {
        if (generation == observed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"generation: {generation} (observed - the controller is acting on the current spec)\n");
            return;
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"generation: {generation}, observedGeneration: {observed} - THE CONTROLLER HAS NOT YET OBSERVED THE CURRENT SPEC. A rollout is in flight, or the controller is wedged. Status fields below describe the OLD spec.\n");
    }

    private static void Header(StringBuilder sb, string kind, string ns, string name, V1ObjectMeta? meta, DateTimeOffset now)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{kind} {ns}/{name}  age {TextTable.Age(meta?.CreationTimestamp, now)}\n");

        if (Annotation(meta, "deployment.kubernetes.io/revision") is { } revision)
        {
            sb.Append(CultureInfo.InvariantCulture, $"revision: {revision}\n");
        }
    }

    private static void Conditions(
        StringBuilder sb,
        IEnumerable<(string Type, string Status, string Reason, string Message, DateTime? Since)>? conditions,
        DateTimeOffset now)
    {
        sb.Append("\nconditions:\n");
        sb.Append(TextTable.Render(
            ["type", "status", "reason", "message", "since"],
            (conditions ?? []).Select(c => new string?[]
            {
                c.Type, c.Status, c.Reason, c.Message, TextTable.Age(c.Since, now),
            }),
            "  (none reported)"));
    }

    private static void Containers(StringBuilder sb, V1PodSpec? spec)
    {
        sb.Append("\ncontainers (from the pod template):\n");
        sb.Append(TextTable.Render(
            ["container", "image", "requests", "limits"],
            (spec?.Containers ?? []).Select(c => new string?[]
            {
                c.Name, c.Image, Resources(c.Resources?.Requests), Resources(c.Resources?.Limits),
            }),
            "  (no containers in the template)"));
    }

    private static string Images(V1PodSpec? spec) =>
        spec?.Containers is { Count: > 0 } containers
            ? string.Join(",", containers.Select(c => c.Image))
            : "-";

    private static string? Annotation(V1ObjectMeta? meta, string key) =>
        meta?.Annotations is { } annotations && annotations.TryGetValue(key, out var value) ? value : null;

    private static long Revision(V1ObjectMeta? meta) =>
        long.TryParse(
            Annotation(meta, "deployment.kubernetes.io/revision"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var revision)
            ? revision
            : 0;

    private static string Condition(V1Node node, string type) =>
        node.Status?.Conditions?.FirstOrDefault(c => c.Type == type)?.Status ?? "Unknown";

    private static string Pressure(V1Node node)
    {
        var active = (node.Status?.Conditions ?? [])
            .Where(c => c.Type is "MemoryPressure" or "DiskPressure" or "PIDPressure" && c.Status == "True")
            .Select(c => c.Type)
            .ToArray();

        return active.Length == 0 ? "none" : string.Join(",", active);
    }

    private static string Quantity(IDictionary<string, ResourceQuantity>? map, string key) =>
        map is not null && map.TryGetValue(key, out var quantity) ? quantity.ToString() : "-";

    private static double Value(IDictionary<string, ResourceQuantity>? map, string key) =>
        map is not null && map.TryGetValue(key, out var quantity) ? quantity.ToDouble() : 0;

    private static double Sum(V1Pod pod, string resource) =>
        (pod.Spec?.Containers ?? [])
            .Sum(c => c.Resources?.Requests is { } requests && requests.TryGetValue(resource, out var q) ? q.ToDouble() : 0);

    private static string LimitOrNone(V1Pod pod, string resource)
    {
        var containers = pod.Spec?.Containers ?? [];
        var unlimited = containers.Any(c => c.Resources?.Limits is null
            || !c.Resources.Limits.ContainsKey(resource));

        if (unlimited)
        {
            return "NONE";
        }

        return Bytes(containers.Sum(c => c.Resources!.Limits[resource].ToDouble()));
    }

    private static string Percent(double part, double whole) =>
        whole <= 0 ? "-" : (part / whole * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private static string Bytes(double value)
    {
        string[] units = ["B", "Ki", "Mi", "Gi", "Ti"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString("F1", CultureInfo.InvariantCulture) + units[index];
    }

    private static string Ports(V1Service service) =>
        service.Spec?.Ports is { Count: > 0 } ports
            ? string.Join(",", ports.Select(p => $"{p.Name ?? "port"}:{p.Port}->{p.TargetPort}"))
            : "<none>";

    private static string Target(V1EndpointAddress address) =>
        address.TargetRef is { } target ? $"{target.Kind}/{target.Name}" : "-";

    private static string HpaMetrics(V2HorizontalPodAutoscaler hpa)
    {
        var current = hpa.Status?.CurrentMetrics ?? [];
        var target = hpa.Spec?.Metrics ?? [];

        if (target.Count == 0)
        {
            return "-";
        }

        return string.Join(",", target.Select((m, i) =>
        {
            var name = m.Resource?.Name ?? m.Type;
            var want = m.Resource?.Target?.AverageUtilization?.ToString(CultureInfo.InvariantCulture) ?? "?";
            var have = i < current.Count
                ? current[i].Resource?.Current?.AverageUtilization?.ToString(CultureInfo.InvariantCulture) ?? "?"
                : "?";

            return $"{name} {have}/{want}%";
        }));
    }

    // ------------------------------------------------------------------
    // metrics.k8s.io - an aggregated API with no generated model
    // ------------------------------------------------------------------

    private static string RenderPodMetrics(object raw, string @namespace)
    {
        var root = AsElement(raw);
        if (!root.TryGetProperty("items", out var items))
        {
            return $"no pod metrics returned for namespace {@namespace}";
        }

        var rows = new List<IReadOnlyList<string?>>();
        foreach (var item in items.EnumerateArray())
        {
            var name = item.GetProperty("metadata").GetProperty("name").GetString();
            foreach (var container in item.GetProperty("containers").EnumerateArray())
            {
                var usage = container.GetProperty("usage");
                rows.Add([
                    name,
                    container.GetProperty("name").GetString(),
                    usage.TryGetProperty("cpu", out var cpu) ? cpu.GetString() : "-",
                    usage.TryGetProperty("memory", out var memory) ? memory.GetString() : "-",
                ]);
            }
        }

        return TextTable.Render(
            ["pod", "container", "cpu", "memory"],
            rows,
            $"no pod metrics for namespace {@namespace}. Pods that started in the last minute or so "
            + "have not been sampled yet.");
    }

    private static string RenderNodeMetrics(object raw)
    {
        var root = AsElement(raw);
        if (!root.TryGetProperty("items", out var items))
        {
            return "no node metrics returned";
        }

        var rows = items.EnumerateArray().Select(item =>
        {
            var usage = item.GetProperty("usage");
            return (IReadOnlyList<string?>)new string?[]
            {
                item.GetProperty("metadata").GetProperty("name").GetString(),
                usage.TryGetProperty("cpu", out var cpu) ? cpu.GetString() : "-",
                usage.TryGetProperty("memory", out var memory) ? memory.GetString() : "-",
            };
        });

        return TextTable.Render(["node", "cpu", "memory"], rows, "no node metrics returned");
    }

    /// <summary>
    /// The generated client returns aggregated-API responses as <see cref="object"/>. With
    /// System.Text.Json that is already a <see cref="JsonElement"/>; the round-trip is the
    /// fallback for any other serializer configuration rather than the expected path.
    /// </summary>
    private static JsonElement AsElement(object raw) =>
        raw is JsonElement element ? element : JsonSerializer.SerializeToElement(raw);
}
