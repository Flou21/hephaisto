using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Hephaisto.Core.Classification;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;

namespace Hephaisto.Agent.Web;

// ----------------------------------------------------------------------
// The v4 webhook payload, bound to records rather than taken as a JsonElement.
//
// A JsonElement here would move every field name in this contract from compile time into
// string literals scattered through the mapping, and Alertmanager's payload is the one input
// this system does not control the shape of. Binding it means a version bump that renames a
// field fails at a deserialisation boundary with a null, in one place, instead of silently
// producing signals with an empty namespace.
//
// Names match the wire format case-insensitively, which is what ASP.NET's web JSON defaults
// give us - "generatorURL" binds to GeneratorUrl without an attribute.
// ----------------------------------------------------------------------

public sealed record AlertmanagerWebhook
{
    /// <summary>Alertmanager sends this as a string ("4"), not a number.</summary>
    public string? Version { get; init; }

    public string? GroupKey { get; init; }

    /// <summary>Non-zero when Alertmanager dropped alerts from this POST to stay under its
    /// size limit. Worth logging: it means the agent is seeing an incomplete group.</summary>
    public int TruncatedAlerts { get; init; }

    /// <summary><c>firing</c> or <c>resolved</c>, for the group as a whole.</summary>
    public string? Status { get; init; }

    public string? Receiver { get; init; }

    public string? ExternalUrl { get; init; }

    public Dictionary<string, string> GroupLabels { get; init; } = [];

    public Dictionary<string, string> CommonLabels { get; init; } = [];

    public Dictionary<string, string> CommonAnnotations { get; init; } = [];

    public List<AlertmanagerAlert> Alerts { get; init; } = [];
}

public sealed record AlertmanagerAlert
{
    public string? Status { get; init; }

    public Dictionary<string, string> Labels { get; init; } = [];

    public Dictionary<string, string> Annotations { get; init; } = [];

    public DateTimeOffset StartsAt { get; init; }

    /// <summary>Zero time while firing. Alertmanager sends <c>0001-01-01T00:00:00Z</c>, not null.</summary>
    public DateTimeOffset EndsAt { get; init; }

    public string? GeneratorUrl { get; init; }

    /// <summary>Alertmanager's own fingerprint over the label set. Deliberately not reused as
    /// <see cref="Signal.Fingerprint"/>: that one is keyed on the owning controller and
    /// excludes the pod name, so an Alertmanager fingerprint would defeat the dedup.</summary>
    public string? Fingerprint { get; init; }

    public bool IsResolved => string.Equals(Status, "resolved", StringComparison.OrdinalIgnoreCase);
}

public static class AlertmanagerEndpoints
{
    /// <summary>
    /// The mapping is a switch over alertname, so a rule that predates Hephaisto still lands
    /// somewhere useful. <c>hephaisto_kind</c> is the explicit override for rules written
    /// for this agent; everything else is inferred, and Unknown is a legitimate outcome that
    /// routes to the default runbook rather than being dropped.
    /// </summary>
    private const string KindLabel = AlertClassifier.KindLabel;

    private static readonly JsonSerializerOptions RawPayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapAlertmanagerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/webhooks")
            // SECURITY: outside OIDC, because Alertmanager cannot sign in.
            //
            // Two controls instead. A bearer token, checked by WebhookTokenFilter when
            // Web:WebhookToken is set (backlog #138): Alertmanager sends one with
            // http_config.authorization on the receiver. This comment used to say it could
            // not, and for five releases nothing checked a credential because of it. And the
            // network: a NetworkPolicy admits ingress to these paths only from the
            // observability namespace.
            //
            // Without a token the NetworkPolicy is the whole protection, not defence in depth.
            // Anything that can reach these routes can inject signals - which makes the agent
            // investigate whatever an attacker names and, as the only incident system, tell a
            // person about it in the agent's name. Do not add an Ingress for /webhooks.
            .AllowAnonymous();

        group.MapPost("/alertmanager", ReceiveAlertsAsync)
            .WithName("AlertmanagerWebhook");

        group.MapPost("/watchdog", ReceiveWatchdogAsync)
            .WithName("WatchdogWebhook");

        return app;
    }

    /// <summary>
    /// Writes each alert, then answers: 200 when every one of them was committed, 503 when one
    /// could not be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until v0.10.0 this enqueued and answered 200 before anything was written (#136), on the
    /// argument that a handler waiting for a database turns one slow query into a retry storm.
    /// That argument held while a repeat was a new incident. Now a repeat is absorbed into the
    /// incident it belongs to, so a retry is harmless, and Alertmanager's retry is the only queue
    /// that survives this pod restarting - which the in-memory one did not.
    /// </para>
    /// <para>
    /// The first failure to write stops the loop: the database being unreachable is the likely
    /// cause, and every alert after it would fail the same way. Alertmanager re-sends the whole
    /// group, and the ones written before the failure are absorbed as repeats.
    /// </para>
    /// <para>
    /// A NUL in a label is removed rather than refused - Postgres cannot store one, and retrying
    /// an alert that can never be written would block its group for ever.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<AlertIngestResult>, ProblemHttpResult>> ReceiveAlertsAsync(
        [FromBody] AlertmanagerWebhook payload,
        ISignalSink sink,
        WatchdogMonitor watchdog,
        HephaistoMetrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(AlertmanagerEndpoints));

        if (payload.TruncatedAlerts > 0)
        {
            logger.LogWarning(
                "Alertmanager truncated {Count} alerts from group {GroupKey}; this group is incomplete",
                payload.TruncatedAlerts,
                payload.GroupKey);
        }

        var accepted = 0;
        var watchdogSeen = false;

        foreach (var alert in payload.Alerts)
        {
            // The watchdog also arrives here when the operator routes everything to one
            // receiver, so it is recognised on both paths rather than only on /watchdog.
            if (IsWatchdog(alert))
            {
                watchdog.Record();
                watchdogSeen = true;
                continue;
            }

            try
            {
                await sink.IngestAsync(ToSignal(alert, payload), ct);
                accepted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                metrics.SignalDropped(SignalSource.Alertmanager, "ingest-failed");
                logger.LogError(ex,
                    "Could not write alert {AlertName} from group {GroupKey}; answering 503 so Alertmanager retries the group",
                    Label(alert.Labels, "alertname"),
                    payload.GroupKey);

                return TypedResults.Problem(
                    title: "The alert could not be written",
                    detail: "Hephaisto could not persist this group. Alertmanager will retry it; alerts already written are absorbed as repeats.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        logger.LogInformation(
            "Alertmanager group {GroupKey} ({Status}) from {Receiver}: {Accepted} signals written, watchdog={Watchdog}",
            payload.GroupKey,
            payload.Status,
            payload.Receiver,
            accepted,
            watchdogSeen);

        return TypedResults.Ok(new AlertIngestResult(accepted, payload.Alerts.Count, watchdogSeen));
    }

    /// <summary>
    /// The dedicated watchdog route.
    /// </summary>
    /// <remarks>
    /// It records a timestamp and produces no signal, because a permanently-firing alert says
    /// nothing about the cluster - the information is that it arrived. Its absence is what
    /// the agent reports, which is the only way it can notice that its own alert path has
    /// broken: every failure between Prometheus and this handler is silent from in here.
    /// </remarks>
    private static Ok<WatchdogResult> ReceiveWatchdogAsync(
        [FromBody] AlertmanagerWebhook payload,
        WatchdogMonitor watchdog,
        CancellationToken ct)
    {
        watchdog.Record();

        return TypedResults.Ok(new WatchdogResult(
            watchdog.LastSeenAt,
            watchdog.ReceiptCount,
            payload.Alerts.Count));
    }

    /// <summary>
    /// The rule named <c>Watchdog</c>, or one that says it is one.
    /// </summary>
    /// <remarks>
    /// An exact name, not a substring (#151). "Any name containing watchdog" swallowed a
    /// consumer's <c>...WatchdogStalled</c> alert as a heartbeat: a fault recorded as proof that
    /// the alert path works, and told to nobody.
    /// </remarks>
    internal static bool IsWatchdog(AlertmanagerAlert alert) =>
        (alert.Labels.TryGetValue("alertname", out var name)
            && string.Equals(name, "Watchdog", StringComparison.OrdinalIgnoreCase))
        || (alert.Labels.TryGetValue(KindLabel, out var kind)
            && string.Equals(kind, nameof(SignalKind.Watchdog), StringComparison.OrdinalIgnoreCase));

    internal static Signal ToSignal(AlertmanagerAlert alert, AlertmanagerWebhook payload)
    {
        alert = WithoutNuls(alert);
        var labels = alert.Labels;
        var alertName = Label(labels, "alertname") ?? "UnknownAlert";

        var target = ResolveTarget(labels);
        var kind = ResolveKind(alertName, labels, target);

        var signal = new Signal
        {
            Source = SignalSource.Alertmanager,
            Kind = kind,
            Severity = ResolveSeverity(labels, kind),
            Target = target,
            Status = alert.IsResolved ? SignalStatus.Resolved : SignalStatus.Firing,
            AlertKey = AlertIdentity.AlertKey(labels),
            Reason = alertName,
            Message = Label(alert.Annotations, "description")
                ?? Label(alert.Annotations, "summary")
                ?? Label(alert.Annotations, "message")
                ?? alertName,
            FirstSeen = alert.StartsAt,

            // A firing alert has no end, so "last seen" is now. Using EndsAt would stamp
            // every live alert with the year 1, and every window measured from LastSeen -
            // dedup, correlation, expiry - would treat it as ancient.
            LastSeen = alert.IsResolved && alert.EndsAt > DateTimeOffset.UnixEpoch
                ? alert.EndsAt
                : DateTimeOffset.UtcNow,

            Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            Annotations = new Dictionary<string, string>(alert.Annotations, StringComparer.Ordinal),
            RawPayload = JsonSerializer.Serialize(alert, RawPayloadJson),
        };

        // Fingerprint is left empty on purpose: SignalFingerprinter.Compute needs the cluster
        // name, which is ingest configuration rather than anything in this payload. The sink
        // owns fingerprinting, dedup and correlation - see ISignalSink.

        if (!string.IsNullOrEmpty(payload.ExternalUrl))
        {
            signal.Labels["hephaisto_alertmanager_url"] = payload.ExternalUrl;
        }

        if (!string.IsNullOrEmpty(alert.GeneratorUrl))
        {
            signal.Labels["hephaisto_generator_url"] = alert.GeneratorUrl;
        }

        return signal;
    }

    // Kind and severity classification is shared with Kubernetes/SignalMapper via
    // Hephaisto.Core.Classification.AlertClassifier. Both callers used to carry a
    // byte-identical copy of the switch, which is a table that does not stay identical.
    /// <remarks>
    /// An alert that names no Kubernetes object is <see cref="SignalKind.Pipeline"/> unless it
    /// states a kind with <c>hephaisto_kind</c> (#134). Guessing from the name is for alerts about
    /// an object, where a wrong guess costs a runbook; for one about a feed or an export every
    /// Kubernetes runbook starts from a pod it does not have.
    /// </remarks>
    private static SignalKind ResolveKind(
        string alertName,
        IReadOnlyDictionary<string, string> labels,
        TargetRef target)
    {
        if (target.IsAlertOnly
            && !(Label(labels, KindLabel) is { } stated && Enum.TryParse<SignalKind>(stated, ignoreCase: true, out _)))
        {
            return SignalKind.Pipeline;
        }

        return AlertClassifier.Kind(alertName, labels);
    }

    private static Severity ResolveSeverity(IReadOnlyDictionary<string, string> labels, SignalKind kind) =>
        AlertClassifier.SeverityOf(labels, kind);

    /// <summary>
    /// Resolves the object and, where the labels allow, its controller.
    /// </summary>
    /// <remarks>
    /// The owner fields are the ones that matter - fingerprinting, correlation, cooldowns and
    /// oscillation detection are all keyed on them, and a pod name changes every couple of
    /// minutes under CrashLoopBackOff. kube-state-metrics rules carry <c>deployment</c>,
    /// <c>statefulset</c>, <c>daemonset</c> or <c>job_name</c>; when none is present the
    /// owner is left null and <see cref="TargetRef.WorkloadKey"/> falls back to the object,
    /// which is correct for a Node or a bare Pod and merely coarse for anything else.
    /// </remarks>
    private static TargetRef ResolveTarget(IReadOnlyDictionary<string, string> labels)
    {
        var target = new TargetRef
        {
            // The alert's own cluster (#131). Empty means the agent's, which the ingest pipeline
            // fills in - this mapper has no configuration to know it by.
            Cluster = Label(labels, "cluster") ?? string.Empty,

            // Three spellings, because the shipped rules genuinely disagree and the incident is
            // useless without this. kube-state-metrics rules say `namespace`; a recording rule
            // that has been through a relabel says `exported_namespace`; and the OTel
            // spanmetrics rules group by `k8s_namespace_name`, which is neither.
            //
            // An empty namespace is not cosmetic (backlog #33). It is part of the signal
            // fingerprint, it is what Policy:AllowedNamespaces is checked against, it is what
            // every tool call needs as an argument, it is what a notification route filters
            // on - and it is what made an incident card tell the model to investigate
            // `//faulty-service`. It also cost two release candidates: the harness matched
            // fixtures by namespace and reported c10 as having opened no incident across rc3
            // and rc4 while the incident existed the whole time.
            Namespace = Label(labels, "namespace")
                ?? Label(labels, "exported_namespace")
                ?? Label(labels, "k8s_namespace_name")
                ?? string.Empty,
            // `node` only, and NOT `instance`. This fell back to `instance` for three
            // releases, and `instance` is a scrape target address - `10.244.0.6:8080` for
            // anything scraped per-pod. A TargetRef carrying that as its node name reaches
            // ClusterFactsGatherer.ReadNodeAsync, which asks the API server for a Node called
            // `10.244.0.6:8080`, gets a 404, and throws. The gatherer turns any throw into
            // ClusterFactsUnavailable, which is default-denied - so EVERY action proposed on
            // an alert without a `node` label was refused, permanently, with the reason
            // "cluster facts could not be read, so no action can be judged" and nothing
            // anywhere naming the label that did it.
            //
            // It is the same shape as #33 one field over: a label mapped to a place it does
            // not belong, silently. The Kubernetes watch path never had this - SignalMapper
            // reads `node` and stops - so the two ingestion paths disagreed about what a node
            // name is, and only the Alertmanager one could poison an incident. See #92.
            //
            // Null is a shape everything downstream already handles: NodeFacts is nullable
            // and ReadNodeAsync returns null for a missing name. A node the alert did not
            // name is a fact we do not have, which is different from a fact we cannot read.
            NodeName = Label(labels, "node"),
        };

        (target.Kind, target.Name) = ObjectIdentity(labels);
        target.Uid = Label(labels, "uid");

        (target.OwnerKind, target.OwnerName) = OwnerIdentity(labels, target);

        return target;
    }

    private static (string Kind, string Name) ObjectIdentity(IReadOnlyDictionary<string, string> labels)
    {
        if (Label(labels, "pod") is { } pod && IsTheSubject(pod, labels))
        {
            return ("Pod", pod);
        }

        foreach (var (label, kind) in WorkloadLabels)
        {
            if (Label(labels, label) is { } name)
            {
                return (kind, name);
            }
        }

        if (Label(labels, "persistentvolumeclaim") is { } pvc)
        {
            return ("PersistentVolumeClaim", pvc);
        }

        if (Label(labels, "node") is { } node)
        {
            return ("Node", node);
        }

        if (Label(labels, "service") is { } service)
        {
            return ("Service", service);
        }

        // Nothing in the label set names a Kubernetes object - a recording-rule alert on an
        // aggregate, for example. Kind and Name are required columns, so the alert names
        // itself and the target is honestly "not an object".
        return ("Alert", Label(labels, "alertname") ?? "unknown");
    }

    private static (string? Kind, string? Name) OwnerIdentity(
        IReadOnlyDictionary<string, string> labels,
        TargetRef target)
    {
        foreach (var (label, kind) in WorkloadLabels)
        {
            if (Label(labels, label) is { } name)
            {
                // The object IS the controller: leave the owner null so WorkloadKey does not
                // become "ns/Deployment/api" derived from itself twice over.
                return string.Equals(target.Kind, kind, StringComparison.Ordinal) ? (null, null) : (kind, name);
            }
        }

        if (Label(labels, "owner_kind") is { } ownerKind && Label(labels, "owner_name") is { } ownerName)
        {
            return (ownerKind, ownerName);
        }

        return (null, null);
    }

    /// <summary>
    /// Whether a <c>pod</c> label names the pod the alert is about.
    /// </summary>
    /// <remarks>
    /// Backlog #126. kube-state-metrics exports its own pod name in <c>pod</c> on every series
    /// that has no pod of its own, so "a deployment has no available replicas" arrived naming the
    /// exporter as its target. On a kube-state-metrics series the label is kept only when it looks
    /// like a pod of the workload the series is about - <c>{workload}-</c> as a prefix - or when
    /// the series names no workload at all, which is how the per-pod ones look. Any other source
    /// is believed.
    /// </remarks>
    private static bool IsTheSubject(string pod, IReadOnlyDictionary<string, string> labels)
    {
        if (!AlertIdentity.IsFromKubeStateMetrics(labels))
        {
            return true;
        }

        var workload = WorkloadLabels
            .Select(w => Label(labels, w.Label))
            .FirstOrDefault(n => n is not null);

        return workload is null
            ? !pod.Contains("kube-state-metrics", StringComparison.OrdinalIgnoreCase)
            : pod.StartsWith(workload + "-", StringComparison.Ordinal);
    }

    /// <summary>Ordered: the first match wins, so a pod labelled with both its Job and the
    /// CronJob above it resolves to the Job.</summary>
    private static readonly (string Label, string Kind)[] WorkloadLabels =
    [
        ("deployment", "Deployment"),
        ("statefulset", "StatefulSet"),
        ("daemonset", "DaemonSet"),
        // "job_name" and not "job": every Prometheus series carries a "job" label naming the
        // scrape job, so treating it as a Kubernetes Job would label almost every alert in
        // the cluster as being about a Job that does not exist.
        ("job_name", "Job"),
        ("cronjob", "CronJob"),
        ("replicaset", "ReplicaSet"),
    ];

    /// <summary>
    /// The alert with every NUL removed from its labels and annotations. Postgres stores neither
    /// text nor jsonb with one in it, so an alert carrying one could never be written - and a
    /// write that can never succeed, answered with 503, would block its group for ever.
    /// </summary>
    private static AlertmanagerAlert WithoutNuls(AlertmanagerAlert alert)
    {
        static bool Has(Dictionary<string, string> d) =>
            d.Any(kv => kv.Key.Contains('\0', StringComparison.Ordinal) || kv.Value.Contains('\0', StringComparison.Ordinal));

        static Dictionary<string, string> Clean(Dictionary<string, string> d)
        {
            var clean = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in d)
            {
                clean[key.Replace("\0", string.Empty, StringComparison.Ordinal)] =
                    value.Replace("\0", string.Empty, StringComparison.Ordinal);
            }

            return clean;
        }

        return Has(alert.Labels) || Has(alert.Annotations)
            ? alert with { Labels = Clean(alert.Labels), Annotations = Clean(alert.Annotations) }
            : alert;
    }

    private static string? Label(IReadOnlyDictionary<string, string> labels, string key) =>
        labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

public sealed record AlertIngestResult(int Accepted, int Received, bool WatchdogSeen);

public sealed record WatchdogResult(DateTimeOffset? LastSeenAt, long Receipts, int Alerts);
