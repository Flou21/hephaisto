namespace Hephaisto.Core.Notifications;

/// <summary>
/// When to try a failed delivery again, as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <b>The outbox is the only retry authority.</b> <c>ServiceDefaults</c> applies
/// <c>AddStandardResilienceHandler</c> to every client the HTTP factory builds, so a channel
/// registered without opting out would retry inside the handler and again out here, and the
/// attempts would multiply. The outbox owns it because the outbox is the only layer that
/// survives a pod restart - which is the failure this whole milestone is about.
/// </para>
/// <para>
/// Jitter is a parameter rather than an internal <c>Random</c> so the schedule is testable. It
/// is not decoration: a shared outage fails every pending delivery at once, and an unjittered
/// exponential schedule would then retry them all at once too, for as long as the outage lasts.
/// </para>
/// </remarks>
public static class NotificationBackoff
{
    /// <summary>Beyond this the doubling has long since hit the ceiling; it exists to stop the shift overflowing.</summary>
    private const int MaxExponent = 16;

    /// <summary>
    /// Capped exponential, then scaled into the top half of the interval by
    /// <paramref name="jitter"/>. The bottom half is left alone deliberately - spreading retries
    /// is worth doing, making them arrive sooner than the schedule says is not.
    /// </summary>
    /// <param name="attempt">Attempts already made. 1 immediately after the first failure.</param>
    /// <param name="jitter">In [0, 1]. Clamped rather than rejected, since a caller feeding it
    /// from a random source should not be able to fail a delivery with a rounding error.</param>
    public static TimeSpan Delay(int attempt, NotificationOptions options, double jitter)
    {
        ArgumentNullException.ThrowIfNull(options);

        var exponent = Math.Clamp(attempt - 1, 0, MaxExponent);
        var scaled = options.FirstRetryDelay * Math.Pow(2, exponent);
        var capped = scaled < options.MaxRetryDelay ? scaled : options.MaxRetryDelay;

        // Math.Clamp PROPAGATES NaN rather than clamping it, and TimeSpan refuses to multiply
        // by one - so clamping alone would turn a caller's rounding error into an exception on
        // the delivery path. NaN is treated as the most conservative value rather than rejected.
        var bounded = double.IsNaN(jitter) ? 0 : Math.Clamp(jitter, 0, 1);
        var factor = 0.5 + (0.5 * bounded);

        return capped * factor;
    }

    /// <summary>
    /// Whether a retryable failure has any attempts left. A permanent failure never reaches
    /// here - retrying a 400 forever is how an outbox becomes a landfill.
    /// </summary>
    public static bool HasAttemptsLeft(int attempt, NotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return attempt < options.MaxAttempts;
    }
}
