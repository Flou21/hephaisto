using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.Agent.Persistence.Repositories;

/// <summary>
/// Everything the outbound rate limit needs, in one round trip.
/// </summary>
/// <param name="DeliveredOnChannelLastHour">Feeds the per-channel hourly cap.</param>
/// <param name="LastDeliveryForKey">
/// When something last went out about this workload on this channel, or null if nothing has -
/// which is what lets the FIRST message through unconditionally.
/// </param>
/// <param name="SuppressedSinceLastDelivery">
/// How many the cooldown has swallowed since. Rides on the next message that does go out, so a
/// suppressed burst is visible where a human is already looking.
/// </param>
public readonly record struct OutboundBudget(
    int DeliveredOnChannelLastHour,
    DateTimeOffset? LastDeliveryForKey,
    int SuppressedSinceLastDelivery);

/// <summary>
/// Reads and writes the outbox. Everything that decides <i>whether</i> to send lives in
/// <c>Hephaisto.Core.Notifications</c> as pure functions; this only fetches the counts they
/// need and records what happened.
/// </summary>
public interface INotificationOutbox
{
    /// <summary>
    /// Stages a row into the ambient <see cref="HephaistoDbContext"/> <b>without saving</b>.
    /// </summary>
    /// <remarks>
    /// Same discipline as <c>IAuditRepository.Enlist</c>, and for the same reason: the caller is
    /// mid-way through building an incident graph, and a <c>SaveChangesAsync</c> here would
    /// commit a half-stated one. It also is the whole point of the outbox - the delivery and the
    /// state change that caused it must land in one transaction, or a crash between them
    /// recreates the failure this table exists to remove.
    /// </remarks>
    void Enlist(NotificationDelivery delivery);

    /// <summary>Pending rows whose backoff has elapsed, oldest first.</summary>
    Task<IReadOnlyList<NotificationDelivery>> DueAsync(int limit, DateTimeOffset now, CancellationToken ct);

    Task<OutboundBudget> BudgetAsync(string channel, string correlationKey, DateTimeOffset now, CancellationToken ct);

    Task MarkDeliveredAsync(NotificationDelivery delivery, CancellationToken ct);

    Task MarkSuppressedAsync(NotificationDelivery delivery, string reason, CancellationToken ct);

    /// <summary>Retryable failure: keep it pending and come back later.</summary>
    Task RetryLaterAsync(NotificationDelivery delivery, string error, DateTimeOffset nextAttemptAt, CancellationToken ct);

    /// <summary>Terminal. Nobody was told, and the row stays as the evidence of it.</summary>
    Task MarkFailedAsync(NotificationDelivery delivery, string error, CancellationToken ct);
}

public sealed class NotificationOutbox(HephaistoDbContext db, IClock clock) : INotificationOutbox
{
    public void Enlist(NotificationDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        db.NotificationDeliveries.Add(delivery);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> DueAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken ct)
    {
        return await db.NotificationDeliveries
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<OutboundBudget> BudgetAsync(
        string channel,
        string correlationKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var hourAgo = now - TimeSpan.FromHours(1);

        var deliveredLastHour = await db.NotificationDeliveries
            .CountAsync(
                d => d.Channel == channel
                    && d.Status == DeliveryStatus.Delivered
                    && d.DeliveredAt >= hourAgo,
                ct)
            .ConfigureAwait(false);

        // An event about the agent rather than a workload has no key, and skips the cooldown -
        // so there is nothing to look up and no reason to pay for the query.
        if (string.IsNullOrWhiteSpace(correlationKey))
        {
            return new OutboundBudget(deliveredLastHour, null, 0);
        }

        var lastForKey = await db.NotificationDeliveries
            .Where(d => d.Channel == channel
                && d.CorrelationKey == correlationKey
                && d.Status == DeliveryStatus.Delivered)
            .MaxAsync(d => (DateTimeOffset?)d.DeliveredAt, ct)
            .ConfigureAwait(false);

        var since = lastForKey ?? DateTimeOffset.MinValue;

        var suppressed = await db.NotificationDeliveries
            .CountAsync(
                d => d.Channel == channel
                    && d.CorrelationKey == correlationKey
                    && d.Status == DeliveryStatus.Suppressed
                    && d.CreatedAt > since,
                ct)
            .ConfigureAwait(false);

        return new OutboundBudget(deliveredLastHour, lastForKey, suppressed);
    }

    public async Task MarkDeliveredAsync(NotificationDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        delivery.Status = DeliveryStatus.Delivered;
        delivery.DeliveredAt = clock.UtcNow;
        delivery.AttemptCount++;
        delivery.LastError = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkSuppressedAsync(NotificationDelivery delivery, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        // Not an error, and not a delivery. The row survives so the count is answerable from
        // the table rather than only from a metric somebody has to go and find.
        delivery.Status = DeliveryStatus.Suppressed;
        delivery.LastError = Truncate(reason);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RetryLaterAsync(
        NotificationDelivery delivery,
        string error,
        DateTimeOffset nextAttemptAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        delivery.AttemptCount++;
        delivery.LastError = Truncate(error);
        delivery.NextAttemptAt = nextAttemptAt;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(NotificationDelivery delivery, string error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        delivery.Status = DeliveryStatus.Failed;
        delivery.AttemptCount++;
        delivery.LastError = Truncate(error);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The column is bounded, and the text is somebody else's - an HTML error page or a proxy's
    /// opinion. Truncating here rather than letting Postgres refuse the write means a delivery
    /// that failed for a verbose reason still records that it failed.
    /// </summary>
    private static string Truncate(string text) =>
        string.IsNullOrEmpty(text) || text.Length <= HephaistoDbContext.MaxErrorLength
            ? text
            : text[..HephaistoDbContext.MaxErrorLength];
}
