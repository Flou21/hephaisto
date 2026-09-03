using k8s.Models;

using Hephaisto.Core.Classification;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>
/// What the watcher remembers about a pod between two observations, reduced to the two
/// numbers that cannot be read off a single snapshot.
/// </summary>
/// <remarks>
/// A restart count of 40 says nothing on its own - it may have been 40 for a week. What
/// matters is how many of those happened recently, and a snapshot cannot know that. The
/// watcher keeps the window; the mapper stays a pure function of what it is handed.
/// </remarks>
public readonly record struct PodTrend(int RestartsInWindow, int ReadyTransitionsInWindow)
{
    /// <summary>For a first observation, a relist, or any caller with no history.</summary>
    public static PodTrend None => default;
}

/// <summary>
/// The counts at which a trend becomes a signal. Passed in rather than read from options so
/// the mapper has no configuration dependency and a test can state the threshold it means.
/// </summary>
public sealed record SignalThresholds(int RestartStormCount = 3, int ReadinessFlapCount = 4)
{
    public static SignalThresholds Default { get; } = new();
}

/// <summary>
/// Turns a Kubernetes object into a <see cref="Signal"/>. Pure: every input is a fact the
/// caller already fetched, and nothing here opens a connection.
/// </summary>
/// <remarks>
/// <para>
/// Classification order is the substance of this file, not an implementation detail. An
/// OOMKilled container is <b>also</b> in CrashLoopBackOff - the kubelet backs it off like any
/// other repeated failure - so a mapper that tests the waiting reason first labels every
/// memory problem a crash loop. That sends the investigation to the wrong runbook, which
/// tells it to read the previous container's logs, and an OOMKilled process wrote none
/// because the kernel killed it without warning. The investigation then concludes "no logs,
/// unknown cause". The OomKilled runbook calls this out as the single most common
/// misdiagnosis; the ordering below is the fix.
/// </para>
/// <para>
/// Every returned signal is fingerprinted here, because a signal without one cannot be
/// deduped and a caller that forgets is indistinguishable from a caller with a genuinely new
/// problem every few seconds.
/// </para>
/// </remarks>
public static class SignalMapper
{
    private const string OomKilledReason = "OOMKilled";
    private const string EvictedReason = "Evicted";

    /// <summary>
    /// Classifies a pod snapshot. Returns null when nothing is wrong, which is the common
    /// case - the watcher sees every pod update, not only the broken ones.
    /// </summary>
    public static Signal? FromPod(
        V1Pod pod,
        string cluster,
        DateTimeOffset now,
        PodTrend trend,
        OwnerLookup? lookup = null,
        SignalThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(pod);

        thresholds ??= SignalThresholds.Default;

        var statuses = ContainerStatuses(pod);
        var classified = Classify(pod, statuses, trend, thresholds);
        if (classified is not { } outcome)
        {
            return null;
        }

        var target = PodTarget(pod, lookup);
        var labels = PodLabels(pod, statuses, outcome.Container);

        return Finish(
            new Signal
            {
                Source = SignalSource.KubernetesWatch,
                Kind = outcome.Kind,
                Severity = SeverityFor(outcome.Kind),
                Target = target,
                Reason = outcome.Reason,
                Message = outcome.Message,
                FirstSeen = Timestamp(pod.Metadata?.CreationTimestamp) ?? now,
                LastSeen = now,
                Labels = labels,
            },
            cluster);
    }

    /// <summary>
    /// A node's own conditions. Node signals matter out of proportion to their count: the
    /// correlation logic absorbs pod signals on a pressured node into this one, so the pods
    /// evicted as a consequence do not each open an incident of their own.
    /// </summary>
    public static Signal? FromNode(V1Node node, string cluster, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(node);

        var conditions = node.Status?.Conditions;
        if (conditions is null)
        {
            return null;
        }

        string? reason = null;
        string message = string.Empty;

        foreach (var condition in conditions)
        {
            var pressure = condition.Type is "MemoryPressure" or "DiskPressure" or "PIDPressure"
                && IsTrue(condition.Status);

            // Ready=Unknown is the kubelet having stopped reporting, which is worse than
            // Ready=False, not better - the node may still be running workloads nobody can see.
            var notReady = condition.Type == "Ready" && !IsTrue(condition.Status);

            if (!pressure && !notReady)
            {
                continue;
            }

            reason = pressure ? condition.Type : "NodeNotReady";
            message = $"{condition.Type}={condition.Status}: {condition.Reason} {condition.Message}".Trim();
            break;
        }

        if (reason is null)
        {
            return null;
        }

        var name = node.Metadata?.Name ?? "unknown";

        return Finish(
            new Signal
            {
                Source = SignalSource.KubernetesWatch,
                Kind = SignalKind.NodePressure,
                Severity = Severity.Critical,
                Target = new TargetRef
                {
                    Namespace = string.Empty,
                    Kind = "Node",
                    Name = name,
                    Uid = node.Metadata?.Uid,
                    NodeName = name,
                },
                Reason = reason,
                Message = message,
                FirstSeen = Timestamp(node.Metadata?.CreationTimestamp) ?? now,
                LastSeen = now,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["node"] = name,
                    ["condition"] = reason,
                },
            },
            cluster);
    }

    /// <summary>
    /// A Job that exhausted its backoffLimit. The Job object records only that it failed -
    /// the JobFailed runbook sends the investigation to a failed pod's previous logs for why.
    /// </summary>
    public static Signal? FromJob(
        V1Job job,
        string cluster,
        DateTimeOffset now,
        OwnerLookup? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var failed = job.Status?.Conditions?.FirstOrDefault(c => c.Type == "Failed" && IsTrue(c.Status));
        if (failed is null)
        {
            return null;
        }

        var ns = job.Metadata?.NamespaceProperty ?? string.Empty;
        var name = job.Metadata?.Name ?? "unknown";

        var target = new TargetRef
        {
            Namespace = ns,
            Kind = "Job",
            Name = name,
            Uid = job.Metadata?.Uid,
        };

        // A CronJob child resolves up to the CronJob, so a run of nightly failures is one
        // incident with a rising count rather than one per night.
        Apply(target, OwnerWalker.TopController(job.Metadata, ns, lookup));

        return Finish(
            new Signal
            {
                Source = SignalSource.KubernetesWatch,
                Kind = SignalKind.JobFailed,
                Severity = Severity.Warning,
                Target = target,
                Reason = string.IsNullOrEmpty(failed.Reason) ? "JobFailed" : failed.Reason,
                Message = $"{failed.Message} (failed={job.Status?.Failed ?? 0}, "
                    + $"succeeded={job.Status?.Succeeded ?? 0}, backoffLimit={job.Spec?.BackoffLimit?.ToString() ?? "?"})",
                FirstSeen = Timestamp(job.Status?.StartTime) ?? now,
                LastSeen = now,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["namespace"] = ns,
                    ["job_name"] = name,
                },
            },
            cluster);
    }

    /// <summary>
    /// Warning events. The scheduler's refusal reason exists <b>only</b> here - no metric
    /// carries it - which is why events are watched at all rather than inferred from pod
    /// status. Normal-type events are ignored: they are the majority and none of them is a
    /// problem.
    /// </summary>
    public static Signal? FromEvent(Corev1Event kubeEvent, string cluster, OwnerLookup? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(kubeEvent);

        if (!string.Equals(kubeEvent.Type, "Warning", StringComparison.Ordinal))
        {
            return null;
        }

        var reason = kubeEvent.Reason ?? string.Empty;
        var message = kubeEvent.Message ?? string.Empty;

        // Count, not just reason: see EventKind's Unhealthy arm. Kubernetes aggregates
        // repeated identical events, so Count IS the evidence of repetition, and a null
        // Count is treated as one occurrence rather than assumed to be many.
        if (EventKind(reason, message, kubeEvent.Count ?? 1) is not { } kind)
        {
            return null;
        }

        var involved = kubeEvent.InvolvedObject;
        var ns = involved?.NamespaceProperty ?? kubeEvent.Metadata?.NamespaceProperty ?? string.Empty;

        var target = new TargetRef
        {
            Namespace = ns,
            Kind = involved?.Kind ?? "Unknown",
            Name = involved?.Name ?? "unknown",
            Uid = involved?.Uid,
        };

        // An event carries only a reference, not the object, so the involved object has to be
        // fetched before its ownerReferences can be walked at all. That matters more here than
        // anywhere else: the involved object is usually a Pod, and a pod name is exactly what
        // must not reach the fingerprint. With no lookup the owner stays null and WorkloadKey
        // falls back to the pod itself, so a caller that has an API to hand should supply one.
        var involvedMeta = lookup?.Invoke(target.Kind, ns, target.Name);
        Apply(target, OwnerWalker.TopController(involvedMeta, ns, lookup));

        var last = Timestamp(kubeEvent.LastTimestamp)
            ?? Timestamp(kubeEvent.EventTime)
            ?? Timestamp(kubeEvent.FirstTimestamp)
            ?? DateTimeOffset.UtcNow;

        return Finish(
            new Signal
            {
                Source = SignalSource.KubernetesWatch,
                Kind = kind,
                Severity = SeverityFor(kind),
                Target = target,
                Reason = reason,
                Message = message,
                FirstSeen = Timestamp(kubeEvent.FirstTimestamp) ?? last,
                LastSeen = last,

                // The event's own count is carried through: one event row can already stand
                // for a hundred occurrences, and discarding that understates the problem.
                Count = kubeEvent.Count is > 0 ? kubeEvent.Count.Value : 1,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["namespace"] = ns,
                    ["involved_kind"] = target.Kind,
                    ["involved_name"] = target.Name,
                    ["reason"] = reason,
                },
            },
            cluster);
    }

    /// <summary>
    /// An alert, from any source that has already parsed one into labels and annotations.
    /// </summary>
    /// <remarks>
    /// The Alertmanager webhook in <c>Web/AlertmanagerEndpoints</c> binds the HTTP payload
    /// and builds its own signal, deliberately leaving the fingerprint empty for the ingest
    /// pipeline to stamp. This overload serves the callers that are not that webhook - the
    /// PromQL sweep, and any test that wants an alert-shaped signal - and it does stamp the
    /// fingerprint, because those callers have the cluster name in hand and no pipeline
    /// behind them. The alertname vocabulary must stay in step between the two.
    /// </remarks>
    public static Signal FromAlert(
        string alertName,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> annotations,
        DateTimeOffset firstSeen,
        DateTimeOffset lastSeen,
        string cluster)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(annotations);

        var kind = AlertKind(alertName, labels);

        return Finish(
            new Signal
            {
                Source = SignalSource.Alertmanager,
                Kind = kind,
                Severity = AlertSeverity(labels, kind),
                Target = AlertTarget(labels),
                Reason = alertName,
                Message = Value(annotations, "description")
                    ?? Value(annotations, "summary")
                    ?? Value(annotations, "message")
                    ?? alertName,
                FirstSeen = firstSeen,
                LastSeen = lastSeen,
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            },
            cluster);
    }

    // ------------------------------------------------------------------
    // Pod classification
    // ------------------------------------------------------------------

    private readonly record struct Outcome(SignalKind Kind, string Reason, string Message, string? Container);

    private static Outcome? Classify(
        V1Pod pod,
        IReadOnlyList<V1ContainerStatus> statuses,
        PodTrend trend,
        SignalThresholds thresholds)
    {
        // 1. OOMKilled, before anything else. See the remarks on this class: the same
        //    container is simultaneously in CrashLoopBackOff, and whichever test runs first
        //    decides the runbook.
        foreach (var status in statuses)
        {
            var terminated = status.LastState?.Terminated ?? status.State?.Terminated;
            if (terminated is null || !string.Equals(terminated.Reason, OomKilledReason, StringComparison.Ordinal))
            {
                continue;
            }

            return new Outcome(
                SignalKind.OomKilled,
                OomKilledReason,
                $"container {status.Name} was OOMKilled (exitCode {terminated.ExitCode}, "
                    + $"restartCount {status.RestartCount}); memory limit "
                    + $"{Limit(status, "memory") ?? "not set"}",
                status.Name);
        }

        // 2. Eviction is a node-level story wearing a pod's clothes. Calling it a pod problem
        //    invites a restart, and the NodePressure runbook is explicit that restarting one
        //    evicted pod while the node is out of memory treats a symptom on the wrong object.
        if (string.Equals(pod.Status?.Reason, EvictedReason, StringComparison.Ordinal))
        {
            return new Outcome(
                SignalKind.NodePressure,
                EvictedReason,
                pod.Status?.Message ?? "pod was evicted",
                null);
        }

        foreach (var status in statuses)
        {
            var waiting = status.State?.Waiting;
            if (waiting?.Reason is not { Length: > 0 } waitingReason)
            {
                continue;
            }

            // 3-5. The container state reason is the discriminator, not the message. The
            //      ImagePullBackOff and ConfigError runbooks both say so, because the three
            //      look identical from outside: Waiting, no logs, no restarts.
            var kind = waitingReason switch
            {
                "CrashLoopBackOff" => SignalKind.CrashLoopBackOff,
                "ImagePullBackOff" or "ErrImagePull" => SignalKind.ImagePullBackOff,
                "CreateContainerConfigError" => SignalKind.ConfigError,
                _ => (SignalKind?)null,
            };

            if (kind is not { } resolved)
            {
                continue;
            }

            var detail = resolved == SignalKind.CrashLoopBackOff
                ? Termination(status)
                : waiting.Message ?? string.Empty;

            return new Outcome(
                resolved,
                waitingReason,
                $"container {status.Name}: {waitingReason}. {detail}".TrimEnd(),
                status.Name);
        }

        // 6. Pending with the scheduler having refused. The message enumerates every node and
        //    why each was rejected; it is the whole diagnosis and is carried through verbatim.
        if (string.Equals(pod.Status?.Phase, "Pending", StringComparison.Ordinal))
        {
            var unschedulable = pod.Status?.Conditions?.FirstOrDefault(c =>
                string.Equals(c.Reason, "Unschedulable", StringComparison.Ordinal));

            if (unschedulable is not null)
            {
                return new Outcome(
                    SignalKind.Unschedulable,
                    "Unschedulable",
                    unschedulable.Message ?? "pod cannot be scheduled",
                    null);
            }
        }

        // 7. Restarting fast without currently sitting in CrashLoopBackOff - the container
        //    starts successfully each time, so the kubelet never backs it off, and only the
        //    rate gives it away.
        if (trend.RestartsInWindow >= thresholds.RestartStormCount)
        {
            return new Outcome(
                SignalKind.RestartStorm,
                "RestartStorm",
                $"{trend.RestartsInWindow} restarts observed in the trend window "
                    + $"(total {statuses.Sum(s => s.RestartCount)})",
                null);
        }

        // 8. Ready oscillating while the container keeps running. Deliberately last: if
        //    restarts are climbing too, this is the wrong runbook and the checks above own it.
        if (trend.ReadyTransitionsInWindow >= thresholds.ReadinessFlapCount)
        {
            return new Outcome(
                SignalKind.ReadinessFlapping,
                "ReadinessFlapping",
                $"pod readiness changed {trend.ReadyTransitionsInWindow} times in the trend window "
                    + "without restarting",
                null);
        }

        return null;
    }

    private static IReadOnlyList<V1ContainerStatus> ContainerStatuses(V1Pod pod)
    {
        var main = pod.Status?.ContainerStatuses;
        var init = pod.Status?.InitContainerStatuses;

        if (init is null || init.Count == 0)
        {
            return main is null ? [] : [.. main];
        }

        // Init containers first: a pod whose init container cannot pull its image never
        // reaches the app container at all, so the init failure is the story.
        return [.. init, .. main ?? []];
    }

    private static string Termination(V1ContainerStatus status)
    {
        var terminated = status.LastState?.Terminated;
        if (terminated is null)
        {
            return $"restartCount {status.RestartCount}";
        }

        return $"last exit code {terminated.ExitCode}"
            + (string.IsNullOrEmpty(terminated.Reason) ? string.Empty : $" ({terminated.Reason})")
            + $", restartCount {status.RestartCount}";
    }

    private static string? Limit(V1ContainerStatus status, string resource) =>
        status.Resources?.Limits is { } limits && limits.TryGetValue(resource, out var quantity)
            ? quantity.ToString()
            : null;

    private static TargetRef PodTarget(V1Pod pod, OwnerLookup? lookup)
    {
        var ns = pod.Metadata?.NamespaceProperty ?? string.Empty;

        var target = new TargetRef
        {
            Namespace = ns,
            Kind = "Pod",
            Name = pod.Metadata?.Name ?? "unknown",
            Uid = pod.Metadata?.Uid,
            NodeName = pod.Spec?.NodeName,
        };

        Apply(target, OwnerWalker.TopController(pod.Metadata, ns, lookup));

        return target;
    }

    private static Dictionary<string, string> PodLabels(
        V1Pod pod,
        IReadOnlyList<V1ContainerStatus> statuses,
        string? container)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["namespace"] = pod.Metadata?.NamespaceProperty ?? string.Empty,
            ["pod"] = pod.Metadata?.Name ?? string.Empty,
            ["phase"] = pod.Status?.Phase ?? string.Empty,
            ["restart_count"] = statuses.Sum(s => s.RestartCount).ToString(),
        };

        if (pod.Spec?.NodeName is { Length: > 0 } node)
        {
            labels["node"] = node;
        }

        if (container is { Length: > 0 })
        {
            labels["container"] = container;

            // The image tag is what an ImagePullBackOff diagnosis is about, and it is also
            // the identifier a hybrid history search matches on exactly.
            if (statuses.FirstOrDefault(s => s.Name == container)?.Image is { Length: > 0 } image)
            {
                labels["image"] = image;
            }
        }

        return labels;
    }

    // ------------------------------------------------------------------
    // Event and alert vocabularies
    // ------------------------------------------------------------------

    /// <summary>
    /// How many times a readiness probe must have failed before it is called <b>flapping</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the same number as <see cref="SignalThresholds.ReadinessFlapCount"/>, which
    /// governs the other detector for this same kind three hundred lines above. That one counts
    /// ready-transitions in a window and refuses to call anything flapping below four. This one
    /// used to claim it from <b>one</b> warning event, so the same file held two detectors for
    /// one <see cref="SignalKind"/> with thresholds of four and of one.
    /// </para>
    /// <para>
    /// What that cost, measured on the v0.7.0 gate: a readiness probe fails once on every pod
    /// that takes longer to start than its <c>initialDelaySeconds</c>, which is every ordinary
    /// rollout. c14's incident was opened 21 seconds after its deliberate bad deploy by an
    /// <c>Unhealthy</c> event, classified <c>ReadinessFlapping</c>, and every later signal -
    /// including the error-rate alert the fixture exists to raise - correlated into an incident
    /// already labelled as a flap. The investigation was then handed the flap runbook, whose
    /// entire argument is that the fault is intermittent and that restarting will not help,
    /// against a fixture whose correct answer is a rollback.
    /// </para>
    /// <para>
    /// A pod that is genuinely stuck not-ready still reports: the event repeats, Count climbs,
    /// and the shipped <c>KubePodNotReady</c> rule covers it at two minutes as
    /// <see cref="SignalKind.PodNotReady"/>. Nothing is lost by declining to call one failure a
    /// flap; a claim of oscillation needs evidence of oscillation.
    /// </para>
    /// </remarks>
    private const int ReadinessFlapEventCount = 4;

    private static SignalKind? EventKind(string reason, string message, int count) => reason switch
    {
        "FailedScheduling" => SignalKind.Unschedulable,
        "OOMKilling" or OomKilledReason => SignalKind.OomKilled,
        EvictedReason or "NodeHasMemoryPressure" or "NodeHasDiskPressure" or "NodeHasPIDPressure"
            or "EvictionThresholdMet" or "FreeDiskSpaceFailed" => SignalKind.NodePressure,
        "BackOff" => SignalKind.CrashLoopBackOff,
        "ErrImageNeverPull" or "InspectFailed" => SignalKind.ImagePullBackOff,
        "CreateContainerConfigError" => SignalKind.ConfigError,

        // FailedMount is nearly always a ConfigMap or Secret reference that does not resolve,
        // which is the same diagnosis and the same escalation as CreateContainerConfigError.
        "FailedMount" => SignalKind.ConfigError,
        "BackoffLimitExceeded" or "DeadlineExceeded" => SignalKind.JobFailed,

        // "Failed" is overloaded: the kubelet uses it for image pulls and for container
        // creation alike, so it is the one reason where the message has to be read.
        "Failed" when message.Contains("ImagePull", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ErrImagePull", StringComparison.OrdinalIgnoreCase) => SignalKind.ImagePullBackOff,
        "Failed" when message.Contains("CreateContainerConfigError", StringComparison.OrdinalIgnoreCase)
            => SignalKind.ConfigError,

        // Flapping means INTERMITTENT, and one probe failure is not intermittent - it is a
        // pod starting up. See ReadinessFlapEventCount.
        "Unhealthy" when message.Contains("Readiness probe", StringComparison.OrdinalIgnoreCase)
            && count >= ReadinessFlapEventCount
            => SignalKind.ReadinessFlapping,

        // Everything else is a warning about something Hephaisto has no runbook for.
        // Ingesting it would add cost and noise without adding a diagnosis.
        _ => null,
    };

    // Shared with Web/AlertmanagerEndpoints via Hephaisto.Core.Classification.AlertClassifier.
    // Both paths carried a byte-identical copy of this table; since SignalKind selects the
    // runbook, a divergence between them would silently hand an investigation the wrong
    // instructions depending on which door the alert arrived through.
    private static SignalKind AlertKind(string alertName, IReadOnlyDictionary<string, string> labels) =>
        AlertClassifier.Kind(alertName, labels);

    private static Severity AlertSeverity(IReadOnlyDictionary<string, string> labels, SignalKind kind) =>
        AlertClassifier.SeverityOf(labels, kind);

    private static TargetRef AlertTarget(IReadOnlyDictionary<string, string> labels)
    {
        var target = new TargetRef
        {
            Namespace = Value(labels, "namespace") ?? Value(labels, "exported_namespace") ?? string.Empty,
            NodeName = Value(labels, "node"),
            Uid = Value(labels, "uid"),
        };

        (target.Kind, target.Name) = labels switch
        {
            _ when Value(labels, "pod") is { } pod => ("Pod", pod),
            _ when Value(labels, "deployment") is { } d => ("Deployment", d),
            _ when Value(labels, "statefulset") is { } s => ("StatefulSet", s),
            _ when Value(labels, "daemonset") is { } ds => ("DaemonSet", ds),

            // "job_name" and not "job": every Prometheus series carries a "job" label naming
            // the scrape job, so treating it as a Kubernetes Job would mislabel almost
            // everything in the cluster.
            _ when Value(labels, "job_name") is { } j => ("Job", j),
            _ when Value(labels, "persistentvolumeclaim") is { } p => ("PersistentVolumeClaim", p),
            _ when Value(labels, "node") is { } n => ("Node", n),
            _ when Value(labels, "service") is { } svc => ("Service", svc),
            _ => ("Alert", Value(labels, "alertname") ?? "unknown"),
        };

        foreach (var (label, kind) in AlertWorkloadLabels)
        {
            if (Value(labels, label) is not { } name)
            {
                continue;
            }

            // When the object already IS the controller, leaving the owner null keeps
            // WorkloadKey from being derived from the same name twice over.
            if (!string.Equals(target.Kind, kind, StringComparison.Ordinal))
            {
                target.OwnerKind = kind;
                target.OwnerName = name;
            }

            break;
        }

        return target;
    }

    private static readonly (string Label, string Kind)[] AlertWorkloadLabels =
    [
        ("deployment", "Deployment"),
        ("statefulset", "StatefulSet"),
        ("daemonset", "DaemonSet"),
        ("job_name", "Job"),
        ("cronjob", "CronJob"),
        ("replicaset", "ReplicaSet"),
    ];

    // ------------------------------------------------------------------
    // Shared
    // ------------------------------------------------------------------

    /// <summary>
    /// Severity by kind rather than by source. It is a statement about what the failure does
    /// to traffic: a crash loop and an OOM kill are serving nothing, while a pull or config
    /// error is a pod that never started - equally broken, but almost always a fresh deploy
    /// that has not taken over from a working one.
    /// </summary>
    private static Severity SeverityFor(SignalKind kind) => AlertClassifier.SeverityFor(kind);

    private static void Apply(TargetRef target, OwnerRef? owner)
    {
        if (owner is not { } resolved)
        {
            return;
        }

        target.OwnerKind = resolved.Kind;
        target.OwnerName = resolved.Name;
    }

    private static Signal Finish(Signal signal, string cluster)
    {
        signal.Fingerprint = SignalFingerprinter.Compute(signal, cluster);
        return signal;
    }

    private static bool IsTrue(string? status) => string.Equals(status, "True", StringComparison.Ordinal);

    /// <summary>
    /// The generated models expose timestamps as <see cref="DateTime"/> with an unspecified
    /// kind even though the API server only ever sends UTC. Without the explicit kind the
    /// conversion silently applies this machine's offset, and every window measured from the
    /// result is wrong by hours in a way nothing surfaces.
    /// </summary>
    private static DateTimeOffset? Timestamp(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static string? Value(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
