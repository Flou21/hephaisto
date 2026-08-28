using k8s.Models;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>One distinct thing that happened, and how often.</summary>
public sealed record EventGroup(
    string Type,
    string Reason,
    string Message,
    int Count,
    int DistinctObjects,
    string SampleObject,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// Collapses a raw event list into distinct reason+message rows with counts.
/// </summary>
/// <remarks>
/// <para>
/// A namespace with one crash-looping Deployment returns a few hundred events that say
/// <c>BackOff: Back-off restarting failed container</c> and nothing else, one row per pod per
/// retry. Handed that list, a model spends its context on repetition and its attention on the
/// bulk - the one <c>FailedScheduling</c> row that explains the actual problem is buried in
/// the middle and reads exactly like its neighbours.
/// </para>
/// <para>
/// Collapsing also changes what the output says. "x214 over 26m across 3 pods" is a statement
/// about scope and duration that no individual row carries, and scope is usually the thing
/// that decides whether this is a workload problem or a node problem.
/// </para>
/// </remarks>
public static class EventDigest
{
    public static IReadOnlyList<EventGroup> Dedupe(IEnumerable<Corev1Event> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var groups = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var item in events)
        {
            var type = item.Type ?? "Normal";
            var reason = item.Reason ?? string.Empty;
            var message = (item.Message ?? string.Empty).ReplaceLineEndings(" ").Trim();

            // Grouped across objects, not per object. Twenty pods failing to pull one image is
            // one problem, and splitting it by pod name reproduces exactly the per-pod noise
            // this function exists to remove.
            var key = $"{type}{reason}{message}";

            var stamp = Timestamp(item);
            var objectRef = $"{item.InvolvedObject?.Kind}/{item.InvolvedObject?.Name}";

            // The event's own count already stands for many occurrences the API server merged;
            // counting rows instead of counts would understate a burst by an order of magnitude.
            var occurrences = item.Count is > 0 ? item.Count.Value : 1;

            if (groups.TryGetValue(key, out var existing))
            {
                existing.Count += occurrences;
                existing.Objects.Add(objectRef);
                existing.FirstSeen = stamp < existing.FirstSeen ? stamp : existing.FirstSeen;
                existing.LastSeen = stamp > existing.LastSeen ? stamp : existing.LastSeen;
                continue;
            }

            groups[key] = new Accumulator
            {
                Type = type,
                Reason = reason,
                Message = message,
                Count = occurrences,
                Objects = new HashSet<string>(StringComparer.Ordinal) { objectRef },
                SampleObject = objectRef,
                FirstSeen = stamp,
                LastSeen = stamp,
            };
        }

        return groups.Values
            // Newest first, then loudest. What happened most recently is what the
            // investigation is about; the count breaks ties within the same moment.
            .OrderByDescending(g => g.LastSeen)
            .ThenByDescending(g => g.Count)
            .Select(g => new EventGroup(
                g.Type,
                g.Reason,
                g.Message,
                g.Count,
                g.Objects.Count,
                g.SampleObject,
                g.FirstSeen,
                g.LastSeen))
            .ToArray();
    }

    private static DateTimeOffset Timestamp(Corev1Event item)
    {
        var value = item.LastTimestamp ?? item.EventTime ?? item.FirstTimestamp ?? item.Metadata?.CreationTimestamp;

        return value is null
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }

    private sealed class Accumulator
    {
        public required string Type { get; init; }

        public required string Reason { get; init; }

        public required string Message { get; init; }

        public required int Count { get; set; }

        public required HashSet<string> Objects { get; init; }

        public required string SampleObject { get; init; }

        public required DateTimeOffset FirstSeen { get; set; }

        public required DateTimeOffset LastSeen { get; set; }
    }
}
