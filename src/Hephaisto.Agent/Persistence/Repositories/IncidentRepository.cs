using Microsoft.EntityFrameworkCore;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence.Repositories;

public sealed class IncidentRepository(HephaistoDbContext db, IClock clock) : IIncidentRepository
{
    public Task<Incident?> GetAsync(Guid id, CancellationToken ct) =>
        db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<Incident?> GetWithDetailAsync(Guid id, CancellationToken ct) =>
        db.Incidents
            .Include(i => i.Signals)
            .Include(i => i.Events)
            .Include(i => i.Actions)
            .Include(i => i.CodeFixAttempts)
            // Split, because three collection Includes on one query is a cartesian product:
            // 40 signals x 20 events x 3 actions is 2 400 rows to materialise 63 objects.
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Incident>> GetOpenAsync(CancellationToken ct) =>
        await db.Incidents
            .Where(i => HephaistoDbContext.OpenStates.Contains(i.State))
            .OrderByDescending(i => i.OpenedAt)
            .ToListAsync(ct);

    public Task<Incident?> FindByFingerprintAsync(string fingerprint, TimeSpan within, CancellationToken ct)
    {
        var cutoff = clock.UtcNow - within;

        return db.Signals
            .Where(s => s.Fingerprint == fingerprint && s.IncidentId != null)
            .Select(s => s.Incident!)
            .Where(i => HephaistoDbContext.OpenStates.Contains(i.State) && i.LastSignalAt >= cutoff)
            .OrderByDescending(i => i.LastSignalAt)
            .FirstOrDefaultAsync(ct);
    }

    public Task<Incident?> FindByCorrelationKeyAsync(string correlationKey, CancellationToken ct) =>
        db.Incidents
            .Where(i => i.CorrelationKey == correlationKey && HephaistoDbContext.OpenStates.Contains(i.State))
            .OrderByDescending(i => i.LastSignalAt)
            .FirstOrDefaultAsync(ct);

    public Task<int> CountRecentForWorkloadAsync(TargetRef target, TimeSpan window, CancellationToken ct)
    {
        var cutoff = clock.UtcNow - window;

        return WorkloadQuery
            .ForWorkload(db.Incidents, target)
            .CountAsync(i => i.OpenedAt >= cutoff, ct);
    }

    public async Task<IReadOnlyList<IncidentDigest>> GetDigestsForWorkloadAsync(
        string workloadKey,
        int limit,
        CancellationToken ct) =>
        await db.IncidentDigests
            .Where(d => d.WorkloadKey == workloadKey)
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(Incident incident, CancellationToken ct) =>
        await db.Incidents.AddAsync(incident, ct);

    public void AddSignal(Signal signal) => db.Signals.Add(signal);

    public Task<Incident?> FindOpenByFingerprintAsync(string fingerprint, CancellationToken ct) =>
        db.Incidents
            .Include(i => i.Actions)
            .Where(i => HephaistoDbContext.OpenStates.Contains(i.State)
                && i.Signals.Any(s => s.Fingerprint == fingerprint))
            .OrderByDescending(i => i.LastSignalAt)
            .FirstOrDefaultAsync(ct);

    public Task<Incident?> FindLastEndedByFingerprintAsync(string fingerprint, CancellationToken ct) =>
        db.Incidents
            .Where(i => (i.State == IncidentState.Closed || i.State == IncidentState.Resolved)
                && i.Signals.Any(s => s.Fingerprint == fingerprint))
            .OrderByDescending(i => i.ClosedAt ?? i.ResolvedAt ?? i.LastSignalAt)
            .FirstOrDefaultAsync(ct);

    public Task<Signal?> FindAlertRowAsync(Guid incidentId, string alertKey, CancellationToken ct) =>
        db.Signals
            .Where(s => s.IncidentId == incidentId && s.AlertKey == alertKey)
            .OrderByDescending(s => s.LastSeen)
            .FirstOrDefaultAsync(ct);

    public Task<Signal?> FindLatestAlertRowAsync(string fingerprint, string alertKey, CancellationToken ct) =>
        db.Signals
            .Where(s => s.Fingerprint == fingerprint && s.AlertKey == alertKey && s.IncidentId != null)
            .OrderByDescending(s => s.LastSeen)
            .FirstOrDefaultAsync(ct);

    // Alert rows only: a Kubernetes watch signal correlated onto the incident has no resolve, and
    // counting it as still firing would keep every such incident open for good.
    public Task<bool> HasOtherFiringAlertsAsync(Guid incidentId, Guid exceptSignalId, CancellationToken ct) =>
        db.Signals.AnyAsync(
            s => s.IncidentId == incidentId
                && s.Id != exceptSignalId
                && s.AlertKey != null
                && s.Status == SignalStatus.Firing,
            ct);

    public Task<int> CountReopensAsync(Guid incidentId, DateTimeOffset since, CancellationToken ct) =>
        db.IncidentEvents.CountAsync(
            e => e.IncidentId == incidentId
                && e.At >= since
                && e.To == IncidentState.Triaging
                && (e.From == IncidentState.Closed || e.From == IncidentState.Resolved),
            ct);

    public void TrackNewIncidentChildren(
        Incident incident,
        int fromEventIndex = 0,
        Investigation? newInvestigation = null) =>
        db.TrackNewIncidentChildren(incident, fromEventIndex, newInvestigation);

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
