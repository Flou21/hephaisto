using k8s.Models;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>
/// Resolves one link of an ownerReferences chain. Returning <c>null</c> means "I cannot see
/// that object", which ends the walk at the last link that was resolvable.
/// </summary>
/// <remarks>
/// A delegate rather than a client handle so that <see cref="OwnerWalker"/> and
/// <see cref="SignalMapper"/> stay pure functions over facts: the watcher supplies a
/// cache-backed lookup, a test supplies a dictionary, and neither the classification nor the
/// walk can accidentally acquire an I/O dependency.
/// </remarks>
public delegate V1ObjectMeta? OwnerLookup(string kind, string @namespace, string name);

/// <summary>A resolved controller, flattened out of <see cref="V1OwnerReference"/>.</summary>
public readonly record struct OwnerRef(string Kind, string Name, string? Uid);

/// <summary>
/// Walks ownerReferences from an object up to the controller at the top of the chain.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between one incident and fifty. A Pod's direct owner is a
/// ReplicaSet whose name carries the pod-template hash, so a rollout changes it; keyed on the
/// ReplicaSet, a Deployment that crash-loops through two deploys looks like two unrelated
/// problems, and every cooldown, budget and oscillation check - all of which are per workload
/// - silently stops applying. Only the Deployment at the top is stable.
/// </para>
/// <para>
/// The walk is deliberately tolerant: a missing intermediate object stops it and returns the
/// deepest link it did resolve, which is still better than the pod name. Being coarse is
/// recoverable; being wrong is not.
/// </para>
/// </remarks>
public static class OwnerWalker
{
    /// <summary>
    /// Pod → ReplicaSet → Deployment is three; CronJob → Job → Pod is three. Eight is slack
    /// for a custom operator, and a bound is required because ownerReferences are just
    /// annotations of intent - nothing in the API server prevents a cycle.
    /// </summary>
    public const int MaxDepth = 8;

    public static OwnerRef? TopController(V1ObjectMeta? meta, string @namespace, OwnerLookup? lookup)
    {
        var current = meta;
        OwnerRef? deepest = null;

        // Kind/name pairs already visited, so a cycle terminates at the first repeat rather
        // than only when MaxDepth runs out.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var depth = 0; depth < MaxDepth && current is not null; depth++)
        {
            if (Controller(current) is not { } owner
                || string.IsNullOrEmpty(owner.Kind)
                || string.IsNullOrEmpty(owner.Name))
            {
                break;
            }

            var key = $"{owner.Kind}/{owner.Name}";
            if (!seen.Add(key))
            {
                break;
            }

            deepest = new OwnerRef(owner.Kind, owner.Name, owner.Uid);
            current = lookup?.Invoke(owner.Kind, @namespace, owner.Name);
        }

        return deepest;
    }

    /// <summary>
    /// Prefers the reference marked <c>controller: true</c>. An object may carry several
    /// ownerReferences - a Job owned by a CronJob and referenced by something else - but only
    /// one controls it, and that is the one whose lifecycle the object follows.
    /// </summary>
    private static V1OwnerReference? Controller(V1ObjectMeta meta)
    {
        var refs = meta.OwnerReferences;
        if (refs is null || refs.Count == 0)
        {
            return null;
        }

        foreach (var candidate in refs)
        {
            if (candidate.Controller == true)
            {
                return candidate;
            }
        }

        return refs[0];
    }
}
