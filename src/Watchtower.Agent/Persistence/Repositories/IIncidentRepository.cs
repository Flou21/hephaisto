using Watchtower.Core.Domain;

namespace Watchtower.Agent.Persistence.Repositories;

/// <summary>
/// Deliberately thin. Everything that decides anything lives in Watchtower.Core as a pure
/// function over facts; this only knows how to fetch those facts efficiently and how to
/// write the result down.
/// </summary>
public interface IIncidentRepository
{
    Task<Incident?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>With signals, events and actions loaded - what the incident page renders.</summary>
    Task<Incident?> GetWithDetailAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Incident>> GetOpenAsync(CancellationToken ct);

    /// <summary>
    /// Dedup. An identical signal arriving while its incident is still open is a repeat of
    /// the same problem, not a new one - see <see cref="Signal.Fingerprint"/>.
    /// </summary>
    Task<Incident?> FindByFingerprintAsync(string fingerprint, TimeSpan within, CancellationToken ct);

    /// <summary>
    /// Correlation. Distinct from fingerprint dedup: this is what merges an OOMKill
    /// incident and a latency incident on the same workload into one thing to read.
    /// </summary>
    Task<Incident?> FindByCorrelationKeyAsync(string correlationKey, CancellationToken ct);

    /// <summary>
    /// Flap detection. Counts incidents opened for the same workload inside the window,
    /// keyed on the owning controller rather than the object - a crash-looping Deployment
    /// produces a new pod name every couple of minutes, so a count keyed on the pod is
    /// always 1 and nothing ever looks like flapping.
    /// </summary>
    Task<int> CountRecentForWorkloadAsync(TargetRef target, TimeSpan window, CancellationToken ct);

    /// <summary>Most recent digests for a workload, for "has this happened before" context.</summary>
    Task<IReadOnlyList<IncidentDigest>> GetDigestsForWorkloadAsync(string workloadKey, int limit, CancellationToken ct);

    Task AddAsync(Incident incident, CancellationToken ct);

    /// <summary>
    /// Marks a signal as a NEW row before it is attached to an already-persisted incident.
    /// </summary>
    /// <remarks>
    /// Adding to <c>incident.Signals</c> alone is not enough, and the way it fails is silent.
    /// <see cref="Signal.Id"/> is assigned at construction (<c>Guid.CreateVersion7()</c>), so
    /// the key is never the CLR default. When change detection discovers the signal through
    /// the navigation it sees a set key, concludes the row already exists, and issues an
    /// UPDATE - which matches nothing and throws DbUpdateConcurrencyException:
    /// "expected to affect 1 row(s), but actually affected 0".
    ///
    /// The first signal on an incident is unaffected, because it rides in on
    /// <see cref="AddAsync"/>, which marks the whole graph Added. So the bug hides until the
    /// SECOND signal - meaning it breaks exactly deduplication and correlation, the two paths
    /// that define whether repeated symptoms become one incident or none.
    /// </remarks>
    void AddSignal(Signal signal);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
