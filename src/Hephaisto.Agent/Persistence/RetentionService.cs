using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Options;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.Persistence;

/// <summary>
/// Expires the bulky, reproducible rows and never touches the small, irreplaceable ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retention here is asymmetric on purpose.</b> <c>evidence_blobs</c> hold the
/// untruncated output of every tool call - roughly 1 MB each - and expire at 30 days.
/// <c>incident_digests</c> are roughly 2 KB, and are kept indefinitely along with their
/// embeddings. They are not the same kind of data wearing different sizes: the blobs are raw
/// material that only matters while someone might re-examine a specific investigation, while
/// the digests are the accumulated operational knowledge of this cluster - the only thing
/// that lets the agent answer "we have seen this before, and here is what fixed it" a year
/// from now.
/// </para>
/// <para>
/// This asymmetry is exactly why a digest has to stand on its own. It is written to be
/// readable with none of the evidence behind it still in existence; a digest that says
/// "see the attached logs" becomes worthless on day 31, and history that stops being
/// searchable is history the agent cannot learn from.
/// </para>
/// <para>
/// Steps keep pointing at deleted blobs deliberately - <c>investigation_steps.raw_blob_id</c>
/// carries no foreign key for this reason. A dangling pointer resolves to "expired", which
/// is true and cheap; the alternatives are cascading the step log away with the blob, or a
/// constraint that blocks the sweep entirely.
/// </para>
/// </remarks>
public sealed class RetentionService(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptionsMonitor<PersistenceOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep is a disk-space problem later, never a correctness problem
                // now, so it must not take the agent down with it.
                logger.LogError(ex, "Retention sweep failed; will retry on the next interval");
            }

            try
            {
                await Task.Delay(options.CurrentValue.RetentionSweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = clock.UtcNow;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        // ExpiresAt is set by the writer, so a caller can keep a specific blob longer.
        // The age fallback catches rows written before that was set at all - without it a
        // single bad write pins a megabyte forever.
        var ageCutoff = now - o.EvidenceBlobRetention;

        var blobs = await DeleteInBatchesAsync(
            () => db.EvidenceBlobs
                .Where(b => b.ExpiresAt <= now || b.CreatedAt <= ageCutoff)
                .Select(b => b.Id),
            ids => db.EvidenceBlobs.Where(b => ids.Contains(b.Id)),
            o.RetentionBatchSize,
            ct);

        var usageCutoff = now - o.LlmUsageRetention;

        var usage = await DeleteInBatchesAsync(
            () => db.LlmUsage.Where(u => u.At <= usageCutoff).Select(u => u.Id),
            ids => db.LlmUsage.Where(u => ids.Contains(u.Id)),
            o.RetentionBatchSize,
            ct);

        var breaches = await DeleteInBatchesAsync(
            () => db.LlmBudgetBreaches.Where(b => b.At <= usageCutoff).Select(b => b.Id),
            ids => db.LlmBudgetBreaches.Where(b => ids.Contains(b.Id)),
            o.RetentionBatchSize,
            ct);

        var deliveryCutoff = now - o.NotificationRetention;

        // Delivered and suppressed only. A FAILED delivery is the evidence that somebody was
        // not told, which is the same class of record as an audit event - and a pending one has
        // not happened yet, so ageing either out would delete exactly the rows worth keeping.
        var deliveries = await DeleteInBatchesAsync(
            () => db.NotificationDeliveries
                .Where(d => d.CreatedAt <= deliveryCutoff
                    && (d.Status == DeliveryStatus.Delivered || d.Status == DeliveryStatus.Suppressed))
                .Select(d => d.Id),
            ids => db.NotificationDeliveries.Where(d => ids.Contains(d.Id)),
            o.RetentionBatchSize,
            ct);

        // Nothing here deletes an incident, a digest, an audit event or an action. Those
        // are the record of what happened and what was done about it, and they are small.

        if (blobs + usage + breaches + deliveries > 0)
        {
            logger.LogInformation(
                "Retention sweep removed {Blobs} evidence blobs, {Usage} usage rows, {Breaches} breach rows, {Deliveries} notification deliveries",
                blobs,
                usage,
                breaches,
                deliveries);
        }
    }

    /// <summary>
    /// Batched, because one DELETE over a month of blobs holds a write lock long enough to
    /// stall the ingest path trying to insert new ones.
    /// </summary>
    /// <remarks>
    /// Ids first, then delete by id: Postgres has no DELETE ... LIMIT, so ExecuteDelete
    /// cannot express a bounded delete on its own. The extra round trip per batch is the
    /// price of never taking an unbounded lock on the largest table in the database.
    /// </remarks>
    private static async Task<int> DeleteInBatchesAsync<T>(
        Func<IQueryable<Guid>> candidates,
        Func<List<Guid>, IQueryable<T>> byIds,
        int batchSize,
        CancellationToken ct)
        where T : class
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var ids = await candidates().Take(batchSize).ToListAsync(ct);

            if (ids.Count == 0)
            {
                break;
            }

            total += await byIds(ids).ExecuteDeleteAsync(ct);

            if (ids.Count < batchSize)
            {
                break;
            }
        }

        return total;
    }
}
