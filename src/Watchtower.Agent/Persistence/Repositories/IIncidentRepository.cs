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

    Task<int> SaveChangesAsync(CancellationToken ct);
}
