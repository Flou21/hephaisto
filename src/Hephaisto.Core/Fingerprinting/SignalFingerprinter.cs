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
            cluster,
            target.Namespace,
            owner,
            signal.Reason);

        return Sha256Hex(material);
    }

    /// <summary>
    /// The coarser key. Two signals of different kinds on one workload - an OOMKill and a
    /// latency alert on the same Deployment - share this and are merged into one incident,
    /// which is almost always the right story: one cause, two symptoms.
    /// </summary>
    public static string CorrelationKey(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return $"{signal.Target.Namespace}/{OwnerIdentity(signal.Target)}";
    }

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
