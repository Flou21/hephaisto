namespace Hephaisto.Core.Notifications;

/// <summary>Which outbound cap the next delivery would break through, if any.</summary>
public enum NotificationLimit
{
    /// <summary>Nothing is exhausted; the delivery goes out.</summary>
    None = 0,

    /// <summary>Another message about this same workload went out too recently.</summary>
    CorrelationCooldown = 1,

    /// <summary>This channel has had its hour's worth.</summary>
    ChannelHour = 2,
}

/// <summary>
/// <paramref name="Reason"/> is populated even when nothing is exceeded, so the UI can show
/// "4 of 60 sent this hour" from the same call the dispatcher makes.
/// </summary>
public readonly record struct NotificationRateStatus(
    NotificationLimit Exceeded,
    int Used,
    int Limit,
    string Reason)
{
    public bool IsSuppressed => Exceeded is not NotificationLimit.None;
}

/// <summary>
/// The outbound rate limit, as a pure calculator over counts.
/// </summary>
/// <remarks>
/// <para>
/// Ingest has dedup, flap suppression and a storm circuit breaker. The outbound side inherits
/// none of it, and a storm that opens forty incidents would otherwise produce forty cards - so
/// the notifier amplifies exactly the event it exists to report.
/// </para>
/// <para>
/// Pure, and for the same reason as <c>ActionBudget</c>: a status page must be able to say why
/// a message did not go out using the identical arithmetic that decided it.
/// </para>
/// <para>
/// <b>The first message for a correlation key always goes out.</b> The cooldown suppresses the
/// second onward, so a human learns about the problem and is then spared the repeats. A cooldown
/// that could swallow the opening message would be a worse failure than the storm it prevents.
/// </para>
/// </remarks>
public static class NotificationRateLimit
{
    /// <summary>
    /// Checked narrowest-first, because "you were already told about this workload four minutes
    /// ago" is a more useful thing to record than "this channel is full".
    /// </summary>
    /// <param name="correlationKey">
    /// Empty for events that are about the agent rather than a workload. Those skip the cooldown
    /// entirely: autonomy coming back is not something to rate-limit against an unrelated
    /// incident that happened to share a quiet minute.
    /// </param>
    /// <param name="lastDeliveryForCorrelationKey">
    /// When something last went out for this key on this channel, or null if nothing has.
    /// </param>
    public static NotificationRateStatus Evaluate(
        string correlationKey,
        DateTimeOffset? lastDeliveryForCorrelationKey,
        int deliveredOnChannelLastHour,
        DateTimeOffset now,
        NotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(correlationKey)
            && lastDeliveryForCorrelationKey is { } last
            && now - last < options.CorrelationCooldown)
        {
            var age = now - last;

            return new NotificationRateStatus(
                NotificationLimit.CorrelationCooldown,
                deliveredOnChannelLastHour,
                options.MaxPerChannelPerHour,
                $"a message about {correlationKey} went out {(int)age.TotalSeconds}s ago, "
                    + $"inside the {(int)options.CorrelationCooldown.TotalMinutes}m cooldown");
        }

        if (deliveredOnChannelLastHour >= options.MaxPerChannelPerHour)
        {
            return new NotificationRateStatus(
                NotificationLimit.ChannelHour,
                deliveredOnChannelLastHour,
                options.MaxPerChannelPerHour,
                $"channel hourly delivery cap reached "
                    + $"({deliveredOnChannelLastHour}/{options.MaxPerChannelPerHour})");
        }

        return new NotificationRateStatus(
            NotificationLimit.None,
            deliveredOnChannelLastHour,
            options.MaxPerChannelPerHour,
            $"within the delivery budget ({deliveredOnChannelLastHour}/{options.MaxPerChannelPerHour} this hour)");
    }
}
