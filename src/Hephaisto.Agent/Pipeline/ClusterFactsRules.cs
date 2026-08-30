using System.Globalization;
using k8s.Models;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// The judgement calls inside <see cref="ClusterFactsGatherer"/>, separated from the I/O that
/// feeds them.
/// </summary>
/// <remarks>
/// <para>
/// Same bargain the policy engine makes, one layer down: the decisions are pure functions over
/// values, so they can be tested exhaustively without a cluster, and what is left in the
/// gatherer is API calls with nothing to get wrong except which one to make.
/// </para>
/// <para>
/// These live in the Agent rather than in Core because they take <c>k8s.Models</c> types, and
/// Core references nothing that talks to anything. That rule is what keeps the policy engine
/// unit-testable, and it is worth more than putting every pure function in one project.
/// </para>
/// </remarks>
public static class ClusterFactsRules
{
    public const string RevisionAnnotation = "deployment.kubernetes.io/revision";

    /// <summary>
    /// Whether a pod counts against the cluster-wide unhealthy fraction.
    /// </summary>
    /// <remarks>
    /// <c>Succeeded</c> is excluded by <see cref="UnhealthyFraction"/> before this is called:
    /// a completed Job pod is a finished job, not a casualty, and counting them would make a
    /// cluster that runs CronJobs look permanently unhealthy - which would freeze the agent
    /// via gate 7 exactly where it is most useful.
    /// </remarks>
    public static bool IsUnhealthy(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        if (string.Equals(pod.Status?.Phase, "Failed", StringComparison.Ordinal) ||
            string.Equals(pod.Status?.Phase, "Pending", StringComparison.Ordinal))
        {
            return true;
        }

        // A Running pod whose container is not Ready is the CrashLoopBackOff case, and it is
        // the one this fraction exists to notice.
        return pod.Status?.ContainerStatuses?.Any(c => c.Ready != true) == true;
    }

    /// <summary>
    /// The fraction of pods that are unhealthy, ignoring ones that have finished successfully.
    /// </summary>
    public static double UnhealthyFraction(IEnumerable<V1Pod> pods)
    {
        ArgumentNullException.ThrowIfNull(pods);

        var considered = pods
            .Where(p => !string.Equals(p.Status?.Phase, "Succeeded", StringComparison.Ordinal))
            .ToList();

        return considered.Count == 0 ? 0 : (double)considered.Count(IsUnhealthy) / considered.Count;
    }

    /// <summary>
    /// A <c>matchLabels</c> selector rendered as the API's query syntax, or null when there is
    /// nothing to select on.
    /// </summary>
    /// <remarks>
    /// Null rather than empty string, and the caller must not list on it. An empty selector
    /// means "every pod in the namespace", so returning "" would silently widen a question
    /// about one workload into a question about all of them - and the youngest pod in the
    /// namespace would then gate an action on an unrelated workload.
    /// <c>matchExpressions</c> is not supported: no controller this agent acts on uses it,
    /// and quietly ignoring one would produce the same over-wide selector.
    /// </remarks>
    public static string? LabelSelector(V1LabelSelector? selector) =>
        selector?.MatchLabels is { Count: > 0 } m
            ? string.Join(",", m.Select(kv => $"{kv.Key}={kv.Value}"))
            : null;

    /// <summary>The revision number a Deployment controller stamps on each ReplicaSet.</summary>
    public static long RevisionOf(V1ReplicaSet replicaSet)
    {
        ArgumentNullException.ThrowIfNull(replicaSet);

        return replicaSet.Metadata?.Annotations is { } a &&
               a.TryGetValue(RevisionAnnotation, out var raw) &&
               long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            ? revision
            : 0;
    }

    public static bool HasCondition(V1Node node, string type)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Status?.Conditions?.Any(c =>
            string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Status, "True", StringComparison.OrdinalIgnoreCase)) == true;
    }
}
