using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Threading.Channels;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Microsoft.Extensions.Options;

using Watchtower.Agent.Web;
using Watchtower.Core.Domain;
using Watchtower.Core.Fingerprinting;
using Watchtower.Core.Telemetry;

namespace Watchtower.Agent.Kubernetes;

/// <summary>
/// Watches Pods, Events, Nodes and Jobs, classifies what it sees, and feeds the ingest seam.
/// </summary>
/// <remarks>
/// <para>
/// Almost all of this file is about the two ways a watcher fails in production, neither of
/// which is "it threw an exception".
/// </para>
/// <para>
/// <b>Watches die silently.</b> A watch is a long-lived HTTP response; a proxy, a NAT table or
/// the API server itself can drop it in a way that leaves the client happily awaiting a stream
/// that will never produce another byte. Nothing throws, no log line appears, and the agent
/// simply stops noticing that the cluster is on fire - which is indistinguishable, from the
/// inside, from a healthy cluster. The defences are all here: a server-side
/// <c>timeoutSeconds</c> so the connection is closed on purpose rather than left to rot,
/// bookmarks so a resumed watch does not need a full replay, a full relist every
/// <see cref="KubernetesOptions.RelistInterval"/> whether or not anything looks wrong, and a
/// reconnect logged at Information so a flapping watch is visible in the log rather than
/// inferable from the absence of signals.
/// </para>
/// <para>
/// <b>A node restart is a flood.</b> Hundreds of events arrive within seconds, all describing
/// one thing. Two mechanisms bound it: a bounded channel that drops the oldest signal rather
/// than growing (an unbounded queue turns a node restart into an OOM of the one process that
/// must survive it), and a storm circuit breaker that stops opening individual incidents and
/// emits a single aggregate instead. Forty investigations at roughly $0.30 each is a real cost
/// event, and forty incidents is also a worse description of the outage than one.
/// </para>
/// </remarks>
public sealed class KubernetesWatcherService : BackgroundService
{
    private readonly KubernetesApi api;
    private readonly KubernetesOptions options;
    private readonly ISignalSink sink;
    private readonly OwnerCache owners;
    private readonly TimeProvider time;
    private readonly ILogger<KubernetesWatcherService> logger;

    private readonly Channel<Signal> queue;
    private readonly PodTrendTracker trends;
    private readonly SignalThresholds thresholds;
    private readonly SeenEvents seenEvents = new();

    private readonly Counter<long> signalsReceived;
    private readonly Counter<long> signalsDropped;
    private readonly Counter<long> watchReconnects;

    public KubernetesWatcherService(
        KubernetesApi api,
        OwnerCache owners,
        ISignalSink sink,
        IOptions<KubernetesOptions> options,
        IMeterFactory meterFactory,
        TimeProvider time,
        ILogger<KubernetesWatcherService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meterFactory);

        this.api = api;
        this.owners = owners;
        this.sink = sink;
        this.options = options.Value;
        this.time = time;
        this.logger = logger;

        trends = new PodTrendTracker(this.options.RestartStormWindow, this.options.ReadinessFlapWindow);
        thresholds = new SignalThresholds(this.options.RestartStormThreshold, this.options.ReadinessFlapThreshold);

        var meter = meterFactory.Create(WatchtowerTelemetry.MeterName);
        signalsReceived = meter.CreateCounter<long>(WatchtowerTelemetry.Metrics.SignalsReceived);
        var dropped = meter.CreateCounter<long>(WatchtowerTelemetry.Metrics.SignalsDropped);
        signalsDropped = dropped;
        watchReconnects = meter.CreateCounter<long>("watchtower.kubernetes.watch_reconnects");

        queue = Channel.CreateBounded<Signal>(
            new BoundedChannelOptions(this.options.SignalQueueCapacity)
            {
                // DropOldest, not DropWrite: under overflow the newest observations describe
                // what the cluster is doing now, and the oldest are the ones most likely to
                // have been superseded by a later observation of the same object.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            // Drops are otherwise invisible: DropOldest makes every TryWrite succeed, so
            // nothing on the write path can tell that the queue is shedding. This callback is
            // the only honest place to count it, and a rising watchtower.signals.dropped is
            // what says the agent is behind rather than the cluster being quiet.
            itemDropped: signal => dropped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "queue_full"),
                new KeyValuePair<string, object?>("kind", signal.Kind.ToString())));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Kubernetes watcher starting for cluster {Cluster}: queue capacity {Capacity}, relist every {Relist}, "
            + "storm breaker at {StormThreshold} signals per {StormWindow}",
            options.ClusterName,
            options.SignalQueueCapacity,
            options.RelistInterval,
            options.StormThreshold,
            options.StormWindow);

        var work = new List<Task>
        {
            ConsumeAsync(stoppingToken),
            WatchPodsAsync(stoppingToken),
            WatchEventsAsync(stoppingToken),
            WatchNodesAsync(stoppingToken),
            WatchJobsAsync(stoppingToken),
        };

        try
        {
            await Task.WhenAll(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }

    // ------------------------------------------------------------------
    // The four watches
    // ------------------------------------------------------------------

    private Task WatchPodsAsync(CancellationToken ct) =>
        RunWatchAsync<V1Pod, V1PodList>(
            "pods",
            token => api.Core.ListPodForAllNamespacesAsync(cancellationToken: token),
            (rv, token) => api.Core.ListPodForAllNamespacesWithHttpMessagesAsync(
                allowWatchBookmarks: true,
                resourceVersion: rv,
                timeoutSeconds: (int)options.WatchTimeout.TotalSeconds,
                watch: true,
                cancellationToken: token),
            HandlePodAsync,
            pod => trends.Forget(pod),
            ct);

    private Task WatchEventsAsync(CancellationToken ct) =>
        RunWatchAsync<Corev1Event, Corev1EventList>(
            "events",
            token => api.Core.ListEventForAllNamespacesAsync(cancellationToken: token),
            (rv, token) => api.Core.ListEventForAllNamespacesWithHttpMessagesAsync(
                allowWatchBookmarks: true,
                resourceVersion: rv,
                timeoutSeconds: (int)options.WatchTimeout.TotalSeconds,
                watch: true,
                cancellationToken: token),
            HandleEventAsync,
            static _ => { },
            ct);

    private Task WatchNodesAsync(CancellationToken ct) =>
        RunWatchAsync<V1Node, V1NodeList>(
            "nodes",
            token => api.Core.ListNodeAsync(cancellationToken: token),
            (rv, token) => api.Core.ListNodeWithHttpMessagesAsync(
                allowWatchBookmarks: true,
                resourceVersion: rv,
                timeoutSeconds: (int)options.WatchTimeout.TotalSeconds,
                watch: true,
                cancellationToken: token),
            HandleNodeAsync,
            static _ => { },
            ct);

    private Task WatchJobsAsync(CancellationToken ct) =>
        RunWatchAsync<V1Job, V1JobList>(
            "jobs",
            token => api.Batch.ListJobForAllNamespacesAsync(cancellationToken: token),
            (rv, token) => api.Batch.ListJobForAllNamespacesWithHttpMessagesAsync(
                allowWatchBookmarks: true,
                resourceVersion: rv,
                timeoutSeconds: (int)options.WatchTimeout.TotalSeconds,
                watch: true,
                cancellationToken: token),
            HandleJobAsync,
            static _ => { },
            ct);

    /// <summary>
    /// Relist, then watch from the resulting resourceVersion, forever.
    /// </summary>
    private async Task RunWatchAsync<TObject, TList>(
        string what,
        Func<CancellationToken, Task<TList>> listAsync,
        Func<string, CancellationToken, Task<HttpOperationResponse<TList>>> watchAsync,
        Func<TObject, CancellationToken, Task> onChanged,
        Action<TObject> onDeleted,
        CancellationToken ct)
        where TList : IMetadata<V1ListMeta>, IItems<TObject>
        where TObject : IMetadata<V1ObjectMeta>
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var list = await listAsync(ct).ConfigureAwait(false);
                var relistedAt = time.GetUtcNow();

                foreach (var item in list.Items ?? [])
                {
                    await onChanged(item, ct).ConfigureAwait(false);
                }

                var resourceVersion = list.Metadata?.ResourceVersion;
                logger.LogInformation(
                    "Kubernetes watch on {What}: relisted {Count} objects at resourceVersion {ResourceVersion}",
                    what,
                    list.Items?.Count ?? 0,
                    resourceVersion);

                // A successful relist is what resets the backoff, not a successful connect:
                // a watch that connects and immediately dies is still failing.
                attempt = 0;

                while (!ct.IsCancellationRequested
                    && resourceVersion is { Length: > 0 }
                    && time.GetUtcNow() - relistedAt < options.RelistInterval)
                {
                    // CS0618: WatcherExt.WatchAsync is marked obsolete with a message that points
                    // at itself, and KubernetesClient 19 ships no replacement for the typed
                    // operations - the only alternative, GenericClient, gives up the generated
                    // models and the bookmark/timeout parameters this loop depends on. Suppressed
                    // deliberately; revisit when the client actually offers a successor.
#pragma warning disable CS0618
                    var stream = watchAsync(resourceVersion, ct).WatchAsync<TObject, TList>(cancellationToken: ct);
#pragma warning restore CS0618

                    await foreach (var (type, item) in stream.ConfigureAwait(false))
                    {
                        if (type == WatchEventType.Error)
                        {
                            // The API server delivers "resourceVersion too old" as an ERROR
                            // event on an otherwise healthy HTTP 200 stream, so this is the
                            // normal path for a 410 - not an exceptional one.
                            throw new WatchResetException($"the {what} watch received an ERROR event");
                        }

                        // Bookmarks exist so a watch can advance its resourceVersion during a
                        // quiet period. Without them, a watch that sees no changes for an hour
                        // resumes from an hour-old version and is immediately 410'd, which
                        // turns every reconnect into a full relist.
                        if (type == WatchEventType.Bookmark)
                        {
                            resourceVersion = item.Metadata?.ResourceVersion ?? resourceVersion;
                            continue;
                        }

                        resourceVersion = item.Metadata?.ResourceVersion ?? resourceVersion;

                        if (type == WatchEventType.Deleted)
                        {
                            onDeleted(item);
                            continue;
                        }

                        await onChanged(item, ct).ConfigureAwait(false);
                    }

                    // The stream ended cleanly: the server-side timeout fired. Resume from the
                    // version we hold rather than paying for a relist.
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                watchReconnects.Add(1, new KeyValuePair<string, object?>("resource", what));

                var delay = Backoff(attempt);

                // Information, not Debug. A watch that reconnects every few seconds is a real
                // fault with no other symptom - the agent keeps working, just blind between
                // reconnects - and it has to be visible without turning on debug logging.
                logger.LogInformation(
                    ex,
                    "Kubernetes watch on {What} ended ({Reason}); resyncing with a full relist in {Delay} (attempt {Attempt})",
                    what,
                    IsGone(ex) ? "410 Gone - resourceVersion too old" : ex.GetType().Name,
                    delay,
                    attempt);

                try
                {
                    await Task.Delay(delay, time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Exponential with full jitter. The jitter is not decoration: four watches that all lose
    /// their connection to the same API server restart would otherwise reconnect in lockstep
    /// forever, and each reconnect starts with a full relist.
    /// </summary>
    private TimeSpan Backoff(int attempt)
    {
        var exponential = options.ReconnectBaseDelay * Math.Pow(2, Math.Min(attempt - 1, 10));
        var capped = exponential > options.ReconnectMaxDelay ? options.ReconnectMaxDelay : exponential;

        return capped * (0.5 + (Random.Shared.NextDouble() * 0.5));
    }

    private static bool IsGone(Exception ex) => ex switch
    {
        WatchResetException => true,
        KubernetesException k8sEx => k8sEx.Status?.Code == (int)HttpStatusCode.Gone,
        HttpOperationException http => http.Response?.StatusCode == HttpStatusCode.Gone,
        _ => ex.Message.Contains("too old resource version", StringComparison.OrdinalIgnoreCase),
    };

    // ------------------------------------------------------------------
    // Classification
    // ------------------------------------------------------------------

    private async Task HandlePodAsync(V1Pod pod, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var trend = trends.Observe(pod, now);

        // Warm before mapping, so the walk inside the mapper reaches the Deployment rather
        // than stopping at the ReplicaSet. Skipped entirely when the pod looks healthy: the
        // watch sees every pod update in the cluster, and warming all of them would be a
        // constant stream of API calls that never produces a signal.
        if (SignalMapper.FromPod(pod, options.ClusterName, now, trend, thresholds: thresholds) is null)
        {
            return;
        }

        var ns = pod.Metadata?.NamespaceProperty ?? string.Empty;
        await owners.WarmAsync(pod.Metadata, ns, ct).ConfigureAwait(false);

        if (SignalMapper.FromPod(pod, options.ClusterName, now, trend, owners.Lookup, thresholds) is { } signal)
        {
            Enqueue(signal);
        }
    }

    private async Task HandleEventAsync(Corev1Event kubeEvent, CancellationToken ct)
    {
        // A relist returns every event still inside the API server's retention, so without
        // this the ten-minute relist would re-emit the same warnings ten minutes apart
        // forever. Keyed on uid plus count, so a genuinely repeating event still gets through.
        if (!seenEvents.IsNew(kubeEvent))
        {
            return;
        }

        if (SignalMapper.FromEvent(kubeEvent, options.ClusterName) is null)
        {
            return;
        }

        var involved = kubeEvent.InvolvedObject;
        var ns = involved?.NamespaceProperty ?? string.Empty;

        if (involved?.Kind is { Length: > 0 } kind && involved.Name is { Length: > 0 } name)
        {
            var meta = await owners.FetchAsync(kind, ns, name, ct).ConfigureAwait(false);
            await owners.WarmAsync(meta, ns, ct).ConfigureAwait(false);
        }

        if (SignalMapper.FromEvent(kubeEvent, options.ClusterName, owners.Lookup) is { } signal)
        {
            Enqueue(signal);
        }
    }

    private Task HandleNodeAsync(V1Node node, CancellationToken ct)
    {
        if (SignalMapper.FromNode(node, options.ClusterName, time.GetUtcNow()) is { } signal)
        {
            Enqueue(signal);
        }

        return Task.CompletedTask;
    }

    private async Task HandleJobAsync(V1Job job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (SignalMapper.FromJob(job, options.ClusterName, now) is null)
        {
            return;
        }

        var ns = job.Metadata?.NamespaceProperty ?? string.Empty;
        await owners.WarmAsync(job.Metadata, ns, ct).ConfigureAwait(false);

        if (SignalMapper.FromJob(job, options.ClusterName, now, owners.Lookup) is { } signal)
        {
            Enqueue(signal);
        }
    }

    private void Enqueue(Signal signal)
    {
        signalsReceived.Add(
            1,
            new KeyValuePair<string, object?>("kind", signal.Kind.ToString()),
            new KeyValuePair<string, object?>("source", signal.Source.ToString()));

        // Always succeeds while the channel is open: overflow evicts the oldest item and is
        // counted by the itemDropped callback registered in the constructor. A false here is
        // shutdown, and is counted separately so the two are never confused.
        if (!queue.Writer.TryWrite(signal))
        {
            signalsDropped.Add(1, new KeyValuePair<string, object?>("reason", "writer_closed"));
        }
    }

    // ------------------------------------------------------------------
    // Consumer and storm circuit breaker
    // ------------------------------------------------------------------

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var window = new Queue<DateTimeOffset>();
        var storm = new StormState();

        try
        {
            await foreach (var signal in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var now = time.GetUtcNow();

                window.Enqueue(now);
                var cutoff = now - options.StormWindow;
                while (window.Count > 0 && window.Peek() < cutoff)
                {
                    window.Dequeue();
                }

                if (window.Count > options.StormThreshold)
                {
                    if (!storm.Active)
                    {
                        storm.Begin(now);
                        logger.LogWarning(
                            "Signal storm: {Count} signals in {Window}. Individual incidents are suspended; "
                            + "emitting one aggregate signal instead.",
                            window.Count,
                            options.StormWindow);
                    }

                    storm.Record(signal);

                    if (now - storm.LastEmitted >= options.StormAggregateInterval || storm.LastEmitted == default)
                    {
                        await sink.SubmitAsync(Aggregate(storm, now), ct).ConfigureAwait(false);
                        storm.LastEmitted = now;
                    }

                    continue;
                }

                if (storm.Active)
                {
                    // Hysteresis: leaving at the same count it was entered at would flap the
                    // breaker itself, alternating aggregate and individual signals.
                    if (window.Count > options.StormThreshold / 2)
                    {
                        storm.Record(signal);
                        continue;
                    }

                    await sink.SubmitAsync(Aggregate(storm, now), ct).ConfigureAwait(false);
                    logger.LogInformation(
                        "Signal storm over after {Duration}: {Count} signals were aggregated into one.",
                        now - storm.StartedAt,
                        storm.Count);

                    storm.Reset();
                }

                await sink.SubmitAsync(signal, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// One signal standing for the whole burst.
    /// </summary>
    /// <remarks>
    /// Targeted at the cluster, not at any workload: during a node restart the individual
    /// targets are consequences, and pinning the aggregate to whichever one happened to arrive
    /// first would put a cooldown on an innocent Deployment. <see cref="Signal.Reason"/> is
    /// <c>SignalStorm</c>, which is what the ingest pipeline matches on to escalate straight to
    /// a human with <see cref="EscalationReason.StormCircuitBreaker"/> rather than investigate.
    /// </remarks>
    private Signal Aggregate(StormState storm, DateTimeOffset now)
    {
        var dominant = storm.Kinds.OrderByDescending(k => k.Value).ThenBy(k => k.Key).First();

        var signal = new Signal
        {
            Source = SignalSource.KubernetesWatch,
            Kind = dominant.Key,
            Severity = Severity.Critical,
            Target = new TargetRef
            {
                Namespace = string.Empty,
                Kind = "Cluster",
                Name = options.ClusterName,
            },
            Reason = "SignalStorm",
            Message =
                $"{storm.Count} signals in {(now - storm.StartedAt).TotalSeconds:F0}s across "
                + $"{storm.Namespaces.Count} namespace(s) and {storm.Workloads.Count} workload(s). "
                + $"Most common: {dominant.Key} ({dominant.Value}). Individual incidents suppressed.",
            FirstSeen = storm.StartedAt,
            LastSeen = now,
            Count = storm.Count,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["storm_count"] = storm.Count.ToString(CultureInfo.InvariantCulture),
                ["storm_dominant_kind"] = dominant.Key.ToString(),
                ["storm_namespaces"] = string.Join(",", storm.Namespaces.Take(20)),
                ["storm_kinds"] = string.Join(",", storm.Kinds.Select(k => $"{k.Key}={k.Value}")),
            },
        };

        signal.Fingerprint = SignalFingerprinter.Compute(signal, options.ClusterName);
        return signal;
    }

    private sealed class StormState
    {
        public bool Active { get; private set; }

        public DateTimeOffset StartedAt { get; private set; }

        public DateTimeOffset LastEmitted { get; set; }

        public int Count { get; private set; }

        public Dictionary<SignalKind, int> Kinds { get; } = [];

        public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Workloads { get; } = new(StringComparer.Ordinal);

        public void Begin(DateTimeOffset now)
        {
            Active = true;
            StartedAt = now;
            LastEmitted = default;
        }

        public void Record(Signal signal)
        {
            Count++;
            Kinds[signal.Kind] = Kinds.GetValueOrDefault(signal.Kind) + 1;
            Namespaces.Add(signal.Target.Namespace);
            Workloads.Add(signal.Target.WorkloadKey);
        }

        public void Reset()
        {
            Active = false;
            Count = 0;
            Kinds.Clear();
            Namespaces.Clear();
            Workloads.Clear();
            LastEmitted = default;
        }
    }

    /// <summary>
    /// Remembers which event rows have already been turned into a signal.
    /// </summary>
    /// <remarks>
    /// Bounded and cleared wholesale rather than evicted individually: the worst a clear costs
    /// is one duplicated round of signals, which the fingerprint dedup absorbs, and an unbounded
    /// set here would be a leak in the process that must not run out of memory.
    /// </remarks>
    private sealed class SeenEvents
    {
        private const int MaxEntries = 20_000;

        private readonly HashSet<string> keys = new(StringComparer.Ordinal);

        public bool IsNew(Corev1Event kubeEvent)
        {
            var key = $"{kubeEvent.Metadata?.Uid}:{kubeEvent.Count ?? 1}";

            lock (keys)
            {
                if (keys.Count >= MaxEntries)
                {
                    keys.Clear();
                }

                return keys.Add(key);
            }
        }
    }
}

/// <summary>
/// Signals "this watch cannot be resumed; relist". Carried as an exception because it has to
/// unwind out of the middle of an <c>await foreach</c> over the event stream.
/// </summary>
public sealed class WatchResetException(string message) : Exception(message);
