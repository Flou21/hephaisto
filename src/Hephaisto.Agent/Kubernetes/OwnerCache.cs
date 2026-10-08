using System.Collections.Concurrent;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Hephaisto.Agent.Kubernetes;

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
/// Deployment are fixed for the object's whole life. An entry for an object that was found
/// therefore only expires to bound memory, not for correctness.
/// </para>
/// <para>
/// <b>"Not there" is remembered too, but only for <see cref="NegativeTtl"/>.</b> It has to be
/// remembered at all: a Pod whose ReplicaSet has already been garbage-collected would otherwise
/// be re-fetched on every observation, and crash-looping pods are observed a lot. It must not
/// be remembered for long, because unlike ownership, absence does change - and a name is not an
/// identity. A Deployment that is deleted and created again keeps its name, and so does its
/// ReplicaSet, whose name is a hash of the unchanged pod template. A lookup made while they
/// were gone (the watcher makes them for the events it is replayed after a restart) used to be
/// held for the hour: the new Pod's walk then stopped at the ReplicaSet, and its incident was
/// filed under <c>ns/ReplicaSet/name-hash</c> instead of the Deployment. An API error is the
/// same answer with the same lifetime - one refused request must not decide an hour of
/// attributions either.
/// </para>
/// </remarks>
public sealed class OwnerCache(KubernetesApi api, TimeProvider time, ILogger<OwnerCache> logger)
{
    private const int MaxEntries = 20_000;

    /// <summary>How long an object that was found is served from memory.</summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>
    /// How long "not found" and "could not be read" are served from memory. Long enough that a
    /// crash loop is one request per half minute and not one per observation; short enough that
    /// an object created under a name that was just free is seen.
    /// </summary>
    internal static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

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
        var gone = false;

        try
        {
            meta = await ReadAsync(kind, @namespace, name, ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Garbage-collected between the pod being observed and this call. Cached as a
            // negative so the next thousand observations of the same pod do not re-ask - for
            // NegativeTtl, not for the hour: the name may be somebody's again by then.
            meta = null;
            gone = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not an answer about the object at all. Held as briefly as "not found", so that an
            // API server that is failing is not asked per observation either.
            logger.LogDebug(ex, "Owner lookup for {Kind}/{Name} in {Namespace} failed; the walk stops here", kind, name, @namespace);
            meta = null;
        }

        Store(key, meta, gone);
        return meta;
    }

    /// <summary>
    /// Whether the API server said, a moment ago, that there is no such object: a 404, no older
    /// than <see cref="NegativeTtl"/>. Never performs I/O.
    /// </summary>
    /// <remarks>
    /// False for everything that is not that answer - an object that was found, one nobody
    /// asked about, a kind this cache cannot read, a request that failed. "Could not be read" is
    /// not "gone", and what a caller does with gone (drop a warning about it) must not happen
    /// because the API server was slow.
    /// </remarks>
    public bool IsGone(string kind, string @namespace, string name) =>
        entries.TryGetValue(Key(kind, @namespace, name), out var entry) && entry.ExpiresAt > time.GetUtcNow() && entry.Gone;

    private V1ObjectMeta? TryGet(string kind, string @namespace, string name) =>
        entries.TryGetValue(Key(kind, @namespace, name), out var entry) && entry.ExpiresAt > time.GetUtcNow()
            ? entry.Meta
            : null;

    /// <summary>
    /// Only the kinds that can actually own something Hephaisto watches. An unknown kind
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

    private void Store(string key, V1ObjectMeta? meta, bool gone)
    {
        // A flat cap with a wholesale clear, rather than an LRU. The contents are pure
        // derivable state, so the worst a clear costs is one round of re-fetching, and an
        // eviction policy here would be more code than the thing it protects.
        if (entries.Count >= MaxEntries)
        {
            entries.Clear();
        }

        entries[key] = new Entry(meta, time.GetUtcNow() + (meta is null ? NegativeTtl : Ttl), gone && meta is null);
    }

    private static string Key(string kind, string @namespace, string name) => $"{kind}/{@namespace}/{name}";

    /// <param name="Gone">The API server answered 404 - as opposed to not answering, or a kind that is not read.</param>
    private readonly record struct Entry(V1ObjectMeta? Meta, DateTimeOffset ExpiresAt, bool Gone);
}
