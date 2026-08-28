using System.Collections.Concurrent;

using k8s.Models;

namespace Watchtower.Agent.Kubernetes;

/// <summary>
/// Turns a stream of pod snapshots into the two rates a snapshot cannot express.
/// </summary>
/// <remarks>
/// <para>
/// <c>restartCount: 40</c> is not a problem statement. Forty restarts over three weeks is a
/// workload that occasionally hiccups; forty in ten minutes is an incident. The same is true
/// of readiness: a pod that has been ready and not-ready once is starting up, and one that has
/// flipped six times in ten minutes is flapping. Both distinctions need memory, so it lives
/// here and <see cref="SignalMapper"/> stays a pure function.
/// </para>
/// <para>
/// Keyed on the pod UID, not on namespace/name: a recreated pod with the same name is a
/// different pod, and inheriting the old one's restart history would report a storm the moment
/// a Deployment is rolled.
/// </para>
/// </remarks>
public sealed class PodTrendTracker(TimeSpan restartWindow, TimeSpan readinessWindow)
{
    private readonly ConcurrentDictionary<string, State> states = new(StringComparer.Ordinal);

    public int TrackedPods => states.Count;

    public PodTrend Observe(V1Pod pod, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var key = pod.Metadata?.Uid ?? $"{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}";
        var state = states.GetOrAdd(key, _ => new State());

        var restarts = 0;
        if (pod.Status?.ContainerStatuses is { } containers)
        {
            foreach (var container in containers)
            {
                restarts += container.RestartCount;
            }
        }

        var ready = pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready") is { } condition
            && string.Equals(condition.Status, "True", StringComparison.Ordinal);

        lock (state)
        {
            if (state.LastRestartCount is { } previous && restarts > previous)
            {
                // Each restart gets its own timestamp so the window count is a count of
                // restarts, not of observations that happened to notice one.
                for (var i = 0; i < restarts - previous; i++)
                {
                    state.Restarts.Enqueue(now);
                }
            }

            state.LastRestartCount = restarts;

            if (state.LastReady is { } wasReady && wasReady != ready)
            {
                state.ReadyFlips.Enqueue(now);
            }

            state.LastReady = ready;
            state.LastSeen = now;

            Trim(state.Restarts, now - restartWindow);
            Trim(state.ReadyFlips, now - readinessWindow);

            return new PodTrend(state.Restarts.Count, state.ReadyFlips.Count);
        }
    }

    /// <summary>
    /// Called on a delete event. Without it the map grows for the life of the process, and on
    /// a cluster whose pods churn that is a slow leak in the one process that must not OOM.
    /// </summary>
    public void Forget(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var key = pod.Metadata?.Uid ?? $"{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}";
        states.TryRemove(key, out _);
    }

    /// <summary>
    /// Drops pods that have not been seen for a while. A pod deleted while the watch was
    /// disconnected never produces a delete event, so <see cref="Forget"/> alone is not enough.
    /// </summary>
    public void Sweep(DateTimeOffset now, TimeSpan idleFor)
    {
        foreach (var (key, state) in states)
        {
            lock (state)
            {
                if (now - state.LastSeen > idleFor)
                {
                    states.TryRemove(key, out _);
                }
            }
        }
    }

    private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset cutoff)
    {
        while (queue.Count > 0 && queue.Peek() < cutoff)
        {
            queue.Dequeue();
        }
    }

    private sealed class State
    {
        public int? LastRestartCount { get; set; }

        public bool? LastReady { get; set; }

        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

        public Queue<DateTimeOffset> Restarts { get; } = new();

        public Queue<DateTimeOffset> ReadyFlips { get; } = new();
    }
}
