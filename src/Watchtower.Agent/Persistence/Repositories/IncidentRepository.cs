using Microsoft.EntityFrameworkCore;
using Watchtower.Core.Abstractions;
using Watchtower.Core.Domain;

namespace Watchtower.Agent.Persistence.Repositories;

public sealed class IncidentRepository(WatchtowerDbContext db, IClock clock) : IIncidentRepository
{
    public Task<Incident?> GetAsync(Guid id, CancellationToken ct) =>
        db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<Incident?> GetWithDetailAsync(Guid id, CancellationToken ct) =>
        db.Incidents
            .Include(i => i.Signals)
            .Include(i => i.Events)
            .Include(i => i.Actions)
            // Split, because three collection Includes on one query is a cartesian product:
            // 40 signals x 20 events x 3 actions is 2 400 rows to materialise 63 objects.
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Incident>> GetOpenAsync(CancellationToken ct) =>
        await db.Incidents
            .Where(i => WatchtowerDbContext.OpenStates.Contains(i.State))
            .OrderByDescending(i => i.OpenedAt)
            .ToListAsync(ct);

    public Task<Incident?> FindByFingerprintAsync(string fingerprint, TimeSpan within, CancellationToken ct)
    {
        var cutoff = clock.UtcNow - within;

        return db.Signals
            .Where(s => s.Fingerprint == fingerprint && s.IncidentId != null)
            .Select(s => s.Incident!)
            .Where(i => WatchtowerDbContext.OpenStates.Contains(i.State) && i.LastSignalAt >= cutoff)
            .OrderByDescending(i => i.LastSignalAt)
            .FirstOrDefaultAsync(ct);
    }

    public Task<Incident?> FindByCorrelationKeyAsync(string correlationKey, CancellationToken ct) =>
        db.Incidents
            .Where(i => i.CorrelationKey == correlationKey && WatchtowerDbContext.OpenStates.Contains(i.State))
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

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
