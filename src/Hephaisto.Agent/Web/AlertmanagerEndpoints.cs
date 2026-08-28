using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Hephaisto.Core.Classification;
using Hephaisto.Core.Domain;

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
            // SECURITY: both routes below are deliberately unauthenticated.
            //
            // Alertmanager's webhook_configs cannot send a custom header - there is no
            // bearer token, no HMAC and no signature in the receiver config - so a check
            // here would either reject every real delivery or be satisfied by anything that
            // can reach the port. The control is therefore at the network layer: a
            // NetworkPolicy admits ingress to these paths only from the observability
            // namespace, and nothing else in the cluster can open the connection at all.
            //
            // That means the NetworkPolicy is load-bearing, not defence in depth. If it is
            // ever removed or the pod is exposed through an Ingress, anything on the network
            // can inject signals - which is a way to make the agent investigate whatever an
            // attacker names, and in a future non-observe mode, to steer what it acts on.
            // Do not add an Ingress for /webhooks.
            .AllowAnonymous();

        group.MapPost("/alertmanager", ReceiveAlertsAsync)
            .WithName("AlertmanagerWebhook");

        group.MapPost("/watchdog", ReceiveWatchdogAsync)
            .WithName("WatchdogWebhook");

        return app;
    }

    /// <summary>
    /// Maps each alert to a <see cref="Signal"/> and hands it to the sink.
    /// </summary>
    /// <remarks>
    /// Returns 200 with a count and nothing else. Alertmanager retries on any non-2xx and
    /// re-sends the whole group on its repeat interval, so a handler that waits for a
    /// database is one slow query away from turning a single firing group into a delivery
    /// storm - each retry arriving before the previous one committed, each looking like a
    /// new observation. The sink's contract is to enqueue; see <see cref="ISignalSink"/>.
    /// </remarks>
    private static async Task<Ok<AlertIngestResult>> ReceiveAlertsAsync(
        [FromBody] AlertmanagerWebhook payload,
        ISignalSink sink,
        WatchdogMonitor watchdog,
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

            await sink.SubmitAsync(ToSignal(alert, payload), ct);
            accepted++;
        }

        logger.LogInformation(
            "Alertmanager group {GroupKey} ({Status}) from {Receiver}: {Accepted} signals accepted, watchdog={Watchdog}",
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

    private static bool IsWatchdog(AlertmanagerAlert alert) =>
        (alert.Labels.TryGetValue("alertname", out var name)
            && name.Contains("watchdog", StringComparison.OrdinalIgnoreCase))
        || (alert.Labels.TryGetValue(KindLabel, out var kind)
            && string.Equals(kind, nameof(SignalKind.Watchdog), StringComparison.OrdinalIgnoreCase));

    internal static Signal ToSignal(AlertmanagerAlert alert, AlertmanagerWebhook payload)
    {
        var labels = alert.Labels;
        var alertName = Label(labels, "alertname") ?? "UnknownAlert";

        var kind = ResolveKind(alertName, labels);

        var signal = new Signal
        {
            Source = SignalSource.Alertmanager,
            Kind = kind,
            Severity = ResolveSeverity(labels, kind),
            Target = ResolveTarget(labels),
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
    private static SignalKind ResolveKind(string alertName, IReadOnlyDictionary<string, string> labels) =>
        AlertClassifier.Kind(alertName, labels);

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
            Namespace = Label(labels, "namespace") ?? Label(labels, "exported_namespace") ?? string.Empty,
            NodeName = Label(labels, "node") ?? Label(labels, "instance"),
        };

        (target.Kind, target.Name) = ObjectIdentity(labels);
        target.Uid = Label(labels, "uid");

        (target.OwnerKind, target.OwnerName) = OwnerIdentity(labels, target);

        return target;
    }

    private static (string Kind, string Name) ObjectIdentity(IReadOnlyDictionary<string, string> labels)
    {
        if (Label(labels, "pod") is { } pod)
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

    private static string? Label(IReadOnlyDictionary<string, string> labels, string key) =>
        labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

public sealed record AlertIngestResult(int Accepted, int Received, bool WatchdogSeen);

public sealed record WatchdogResult(DateTimeOffset? LastSeenAt, long Receipts, int Alerts);
