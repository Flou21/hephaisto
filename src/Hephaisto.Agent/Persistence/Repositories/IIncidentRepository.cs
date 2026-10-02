using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence.Repositories;

/// <summary>
/// Deliberately thin. Everything that decides anything lives in Hephaisto.Core as a pure
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

    /// <summary>A notification that is not a transition's - a severity raise (#148) - staged into the same commit.</summary>
    void EnlistNotification(NotificationDelivery delivery);

    /// <summary>
    /// The open incident - Escalated included - carrying a signal with this fingerprint, however
    /// long ago it last heard from it (#130). With its actions, which a clearing alert may have
    /// to expire.
    /// </summary>
    Task<Incident?> FindOpenByFingerprintAsync(string fingerprint, CancellationToken ct);

    /// <summary>
    /// The most recently ended (Closed or Resolved) incident carrying this fingerprint, or null.
    /// </summary>
    Task<Incident?> FindLastEndedByFingerprintAsync(string fingerprint, CancellationToken ct);

    /// <summary>The signal row for one alert instance on one incident, tracked.</summary>
    Task<Signal?> FindAlertRowAsync(Guid incidentId, string alertKey, CancellationToken ct);

    /// <summary>
    /// The newest signal row for this alert instance under this fingerprint, on any incident. For
    /// a resolve whose incident a person already closed.
    /// </summary>
    Task<Signal?> FindLatestAlertRowAsync(string fingerprint, string alertKey, CancellationToken ct);

    /// <summary>Whether any alert instance on the incident other than this one still fires.</summary>
    Task<bool> HasOtherFiringAlertsAsync(Guid incidentId, Guid exceptSignalId, CancellationToken ct);

    /// <summary>How many times the incident reopened since <paramref name="since"/>.</summary>
    /// <summary>
    /// Open incidents the Kubernetes watcher still has a firing signal on, longest silent first
    /// (#158). The watcher asks for these because what it opened is not something it can
    /// remember: its memory ends with the process, and the incidents do not.
    /// </summary>
    Task<IReadOnlyList<WatchedIncident>> GetOpenWatchedAsync(int max, CancellationToken ct);

    /// <summary>
    /// Marks every firing watcher signal on an incident resolved, at once and outside the unit
    /// of work: a crash loop of a week is thousands of rows.
    /// </summary>
    Task<int> ResolveWatchSignalsAsync(Guid incidentId, CancellationToken ct);

    Task<int> CountReopensAsync(Guid incidentId, DateTimeOffset since, CancellationToken ct);

    /// <summary>
    /// Marks children created since <paramref name="fromEventIndex"/> as Added, so they
    /// INSERT. See <c>HephaistoDbContext.TrackNewIncidentChildren</c> for why change
    /// detection cannot be relied on for entities with client-assigned keys.
    /// </summary>
    void TrackNewIncidentChildren(Incident incident, int fromEventIndex = 0, Investigation? newInvestigation = null);

    Task<int> SaveChangesAsync(CancellationToken ct);
}

/// <summary>An open incident of the watcher's, and one fingerprint that finds it again.</summary>
public sealed record WatchedIncident(Guid Id, SignalKind Kind, TargetRef Target, string Fingerprint);
