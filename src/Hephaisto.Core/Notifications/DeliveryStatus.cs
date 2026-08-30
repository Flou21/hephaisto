namespace Hephaisto.Core.Notifications;

/// <summary>
/// Where one outbox row got to. Persisted by name, and read by the UI as well as the dispatcher.
/// </summary>
/// <remarks>
/// <b>Nothing here is deleted on the way through.</b> A row that could not be delivered ends as
/// <see cref="Failed"/> and stays visible, because "escalated, and nobody was told" is the worst
/// failure this system has and the only thing worse would be losing the evidence of it. Ageing
/// out is the retention sweep's job, on the same schedule as everything else.
/// </remarks>
public enum DeliveryStatus
{
    /// <summary>Queued, or waiting out a backoff. The dispatcher's working set.</summary>
    Pending = 0,

    /// <summary>The channel accepted it.</summary>
    Delivered = 1,

    /// <summary>
    /// Permanently rejected, or out of attempts. Terminal, loud, and audited - this is the state
    /// that means a human was not reached.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Deliberately not sent, because the outbound rate limit swallowed it. Recorded rather than
    /// skipped: the ingest side counts its drops instead of discarding them silently, and a
    /// suppressed page is at least as worth counting as a dropped signal.
    /// </summary>
    Suppressed = 3,
}
