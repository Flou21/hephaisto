using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Classification;

/// <summary>
/// Maps an Alertmanager alertname and its labels onto a <see cref="SignalKind"/> and a
/// <see cref="Severity"/>.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Core, and is shared, because it has two callers that arrived independently:
/// the HTTP webhook, which receives an Alertmanager payload, and the signal mapper, which
/// serves non-HTTP callers. Both grew a byte-identical copy of this switch. Two copies of a
/// classification table do not stay identical - someone adds a rule named
/// <c>HephaistoLlmBudgetExhausted</c>, teaches one copy about it, and the same alert is then
/// classified differently depending on which door it came through. Since the whole point of
/// <see cref="SignalKind"/> is to select a runbook, that divergence is invisible until an
/// investigation gets the wrong instructions.
/// </para>
/// <para>
/// It is a pure function over strings, so it belongs here rather than in either caller.
/// </para>
/// </remarks>
public static class AlertClassifier
{
    /// <summary>
    /// An alert may state its own kind, which beats any guessing. Set it on a PrometheusRule
    /// when the alertname does not contain an obvious keyword.
    /// </summary>
    public const string KindLabel = "hephaisto_kind";

    /// <summary>
    /// Substring matching rather than exact alertnames, because the same condition is called
    /// <c>KubePodCrashLooping</c> by kube-prometheus and <c>PodCrashLoopBackOff</c> by whoever
    /// wrote the last rule. Being wrong here costs a runbook lookup, not a safety property -
    /// which is why loose matching is the right trade.
    /// </summary>
    public static SignalKind Kind(string alertName, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (Lookup(labels, KindLabel) is { } explicitKind
            && Enum.TryParse<SignalKind>(explicitKind, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return (alertName ?? string.Empty) switch
        {
            var n when Has(n, "crashloop") => SignalKind.CrashLoopBackOff,
            var n when Has(n, "oom") => SignalKind.OomKilled,
            var n when Has(n, "imagepull") || Has(n, "errimage") => SignalKind.ImagePullBackOff,
            var n when Has(n, "unschedulable") || Has(n, "pending") => SignalKind.Unschedulable,
            var n when Has(n, "configerror") || Has(n, "createcontainerconfig") => SignalKind.ConfigError,
            var n when Has(n, "readiness") => SignalKind.ReadinessFlapping,
            // "not ready" is a stuck pod, not a flapping one - the same split the
            // shipped rules make since backlog #70. Dead code for anything carrying a
            // hephaisto_kind label, which every rule in this repo does, and kept honest
            // anyway because the guess is what a foreign rule falls back to.
            var n when Has(n, "notready") || Has(n, "podnotready") => SignalKind.PodNotReady,
            var n when Has(n, "jobfailed") || Has(n, "jobfailure") => SignalKind.JobFailed,
            var n when Has(n, "restart") => SignalKind.RestartStorm,
            var n when Has(n, "nodepressure") || Has(n, "nodememory") || Has(n, "nodedisk") => SignalKind.NodePressure,
            var n when Has(n, "pvc") || Has(n, "volumefill") => SignalKind.PvcNearlyFull,
            var n when Has(n, "replica") => SignalKind.ReplicaMismatch,
            var n when Has(n, "targetdown") || Has(n, "targetmissing") => SignalKind.TargetDown,
            var n when Has(n, "errorrate") || Has(n, "5xx") => SignalKind.HighErrorRate,
            var n when Has(n, "latency") || Has(n, "slo") => SignalKind.HighLatency,
            _ => SignalKind.Unknown,
        };

        static bool Has(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>severity</c> label when present, otherwise what the kind implies.
    /// </summary>
    /// <remarks>
    /// An unlabelled alert falls back to the kind rather than to <see cref="Severity.Info"/>.
    /// An unclassified alert that turns out to matter is worse than one investigated for
    /// nothing, and in observe mode the cost of the latter is a few cents.
    /// </remarks>
    public static Severity SeverityOf(IReadOnlyDictionary<string, string> labels, SignalKind kind)
    {
        ArgumentNullException.ThrowIfNull(labels);

        return Lookup(labels, "severity")?.ToLowerInvariant() switch
        {
            "critical" or "page" or "emergency" => Severity.Critical,
            "warning" or "warn" => Severity.Warning,
            "info" or "none" => Severity.Info,
            _ => SeverityFor(kind),
        };
    }

    public static Severity SeverityFor(SignalKind kind) => kind switch
    {
        SignalKind.OomKilled or SignalKind.CrashLoopBackOff or SignalKind.NodePressure
            or SignalKind.TargetDown => Severity.Critical,
        _ => Severity.Warning,
    };

    private static string? Lookup(IReadOnlyDictionary<string, string> labels, string key) =>
        labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
