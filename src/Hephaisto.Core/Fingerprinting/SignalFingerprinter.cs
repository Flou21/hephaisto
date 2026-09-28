using System.Security.Cryptography;
using System.Text;
using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Fingerprinting;

/// <summary>
/// Turns a <see cref="Signal"/> into the two keys the whole pipeline is built on.
/// </summary>
/// <remarks>
/// <para>
/// The pod name is deliberately absent from both. A Deployment in CrashLoopBackOff produces
/// a new pod name every couple of minutes; keyed on the pod, fifty observations of one broken
/// Deployment become fifty incidents, fifty investigations and fifty LLM bills, and the agent
/// never gets to notice it is looking at a single problem. Keyed on the owner it is one
/// incident whose signal count rises - which is also what makes the cooldown, the budget and
/// the oscillation detector mean anything, since all three are per workload.
/// </para>
/// <para>
/// The cluster name is in the hash so that two clusters reporting into one database cannot
/// collide, and so a fingerprint can never be replayed from staging into production's dedup.
/// </para>
/// </remarks>
public static class SignalFingerprinter
{
    /// <summary>
    /// Separator chosen because it cannot appear in a Kubernetes name, namespace or kind, so
    /// no two different field tuples can be flattened into the same string.
    /// </summary>
    private const char FieldSeparator = '|';

    /// <param name="signal">The signal. Its target's cluster, when set, wins.</param>
    /// <param name="cluster">The agent's own cluster, for a target that names none.</param>
    public static string Compute(Signal signal, string cluster)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var target = signal.Target;
        var owner = OwnerIdentity(target);

        // Enum members are written by name, not by number: renumbering SignalKind later must
        // not silently re-key every historical fingerprint.
        var material = string.Join(
            FieldSeparator,
            signal.Source.ToString(),
            signal.Kind.ToString(),
            ClusterOf(target, cluster),
            target.Namespace,
            owner,
            signal.Reason);

        // An alert that names no object is one incident per series of its rule, not one per
        // rule (#132). The owner above is "Alert/<alertname>" for every one of them.
        if (target.IsAlertOnly)
        {
            material += FieldSeparator + AlertIdentity.IdentityHash(signal.Labels);
        }

        return Sha256Hex(material);
    }

    /// <summary>
    /// The coarser key. Two signals of different kinds on one workload - an OOMKill and a
    /// latency alert on the same Deployment - share this and are merged into one incident,
    /// which is almost always the right story: one cause, two symptoms.
    /// </summary>
    /// <remarks>
    /// The cluster is in it (#131): the same Deployment name in two clusters is two workloads,
    /// and correlating them would fold one cluster's fault into the other's incident. So is the
    /// series of an alert that names no object (#132), because the notification cooldown is
    /// keyed on this, and two providers failing are two things to tell somebody about.
    /// </remarks>
    public static string CorrelationKey(Signal signal, string cluster = "")
    {
        ArgumentNullException.ThrowIfNull(signal);

        var key = $"{ClusterOf(signal.Target, cluster)}:{signal.Target.Namespace}/{OwnerIdentity(signal.Target)}";

        return signal.Target.IsAlertOnly
            ? $"{key}#{AlertIdentity.IdentityHash(signal.Labels)[..16]}"
            : key;
    }

    private static string ClusterOf(TargetRef target, string cluster) =>
        target.Cluster is { Length: > 0 } own ? own : cluster;

    /// <summary>
    /// Falls back to the object itself only when it genuinely has no controller (a bare Pod,
    /// a Node, a PVC). Anything with an owner is identified by the owner, never by the name.
    /// </summary>
    private static string OwnerIdentity(TargetRef target) =>
        target.OwnerKind is { Length: > 0 } ownerKind && target.OwnerName is { Length: > 0 } ownerName
            ? $"{ownerKind}/{ownerName}"
            : $"{target.Kind}/{target.Name}";

    private static string Sha256Hex(string material)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);
        return Convert.ToHexStringLower(hash);
    }
}
