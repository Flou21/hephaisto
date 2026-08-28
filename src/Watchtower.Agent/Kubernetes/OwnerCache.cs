using System.Collections.Concurrent;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Watchtower.Agent.Kubernetes;

/// <summary>
/// Fetches and remembers the objects an ownerReferences walk needs.
/// </summary>
/// <remarks>
/// <para>
/// The walk itself is a pure function over an <see cref="OwnerLookup"/>, which is
/// synchronous, while resolving a ReplicaSet is an HTTP call. This class bridges the two by
/// fetching the whole chain up front (<see cref="WarmAsync"/>) and then serving the walk from
/// a dictionary. The alternative - blocking on an async call inside the lookup delegate - puts
/// a synchronous wait on the watch thread, which is how a watch stops delivering events
/// without anything appearing to be wrong.
/// </para>
/// <para>
/// Caching is safe because ownership does not change: a Pod's ReplicaSet and that ReplicaSet's
/// Deployment are fixed for the object's whole life. The entries therefore only expire to
/// bound memory, not for correctness. Negative results are cached too - a Pod whose ReplicaSet
/// has already been garbage-collected would otherwise be re-fetched on every observation, and
/// crash-looping pods are observed a lot.
/// </para>
/// </remarks>
public sealed class OwnerCache(KubernetesApi api, TimeProvider time, ILogger<OwnerCache> logger)
{
    private const int MaxEntries = 20_000;

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>
    /// A lookup that never performs I/O. Anything not already warmed reads as "cannot see
    /// that object", which ends the walk one link short rather than blocking.
    /// </summary>
    public OwnerLookup Lookup => TryGet;

    /// <summary>
    /// Fetches every object on <paramref name="meta"/>'s owner chain so a later
    /// <see cref="OwnerWalker.TopController"/> over <see cref="Lookup"/> reaches the top.
    /// </summary>
    public async Task WarmAsync(V1ObjectMeta? meta, string @namespace, CancellationToken ct)
    {
        var current = meta;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var depth = 0; depth < OwnerWalker.MaxDepth && current is not null; depth++)
        {
            var owner = current.OwnerReferences?.FirstOrDefault(o => o.Controller == true)
                ?? current.OwnerReferences?.FirstOrDefault();

            if (owner is null || string.IsNullOrEmpty(owner.Kind) || string.IsNullOrEmpty(owner.Name))
            {
                return;
            }

            if (!seen.Add($"{owner.Kind}/{owner.Name}"))
            {
                return;
            }

            current = await FetchAsync(owner.Kind, @namespace, owner.Name, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fetches one object's metadata, from cache when possible.</summary>
    public async Task<V1ObjectMeta?> FetchAsync(string kind, string @namespace, string name, CancellationToken ct)
    {
        var key = Key(kind, @namespace, name);

        if (entries.TryGetValue(key, out var cached) && cached.ExpiresAt > time.GetUtcNow())
        {
            return cached.Meta;
        }

        V1ObjectMeta? meta;
        try
        {
            meta = await ReadAsync(kind, @namespace, name, ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Garbage-collected between the pod being observed and this call. Cached as a
            // negative so the next thousand observations of the same pod do not re-ask.
            meta = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Owner lookup for {Kind}/{Name} in {Namespace} failed; the walk stops here", kind, name, @namespace);
            meta = null;
        }

        Store(key, meta);
        return meta;
    }

    private V1ObjectMeta? TryGet(string kind, string @namespace, string name) =>
        entries.TryGetValue(Key(kind, @namespace, name), out var entry) && entry.ExpiresAt > time.GetUtcNow()
            ? entry.Meta
            : null;

    /// <summary>
    /// Only the kinds that can actually own something Watchtower watches. An unknown kind
    /// returns null rather than guessing a plural and issuing a request that cannot succeed.
    /// </summary>
    private async Task<V1ObjectMeta?> ReadAsync(string kind, string @namespace, string name, CancellationToken ct) =>
        kind switch
        {
            "ReplicaSet" => (await api.Apps.ReadNamespacedReplicaSetAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "Deployment" => (await api.Apps.ReadNamespacedDeploymentAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "StatefulSet" => (await api.Apps.ReadNamespacedStatefulSetAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "DaemonSet" => (await api.Apps.ReadNamespacedDaemonSetAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "Job" => (await api.Batch.ReadNamespacedJobAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "CronJob" => (await api.Batch.ReadNamespacedCronJobAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "Pod" => (await api.Core.ReadNamespacedPodAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            "Node" => (await api.Core.ReadNodeAsync(name, cancellationToken: ct).ConfigureAwait(false)).Metadata,
            _ => null,
        };

    private void Store(string key, V1ObjectMeta? meta)
    {
        // A flat cap with a wholesale clear, rather than an LRU. The contents are pure
        // derivable state, so the worst a clear costs is one round of re-fetching, and an
        // eviction policy here would be more code than the thing it protects.
        if (entries.Count >= MaxEntries)
        {
            entries.Clear();
        }

        entries[key] = new Entry(meta, time.GetUtcNow() + Ttl);
    }

    private static string Key(string kind, string @namespace, string name) => $"{kind}/{@namespace}/{name}";

    private readonly record struct Entry(V1ObjectMeta? Meta, DateTimeOffset ExpiresAt);
}
