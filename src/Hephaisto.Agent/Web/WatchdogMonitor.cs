using System.Diagnostics.Metrics;

using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Web;

/// <summary>
/// Last time the always-firing <c>AgentWatchdog</c> alert reached us.
/// </summary>
/// <remarks>
/// <para>
/// The alert is configured to fire permanently and is routed to
/// <c>/webhooks/watchdog</c>. A permanently-firing alert carries no information about the
/// cluster; the information is entirely in its <i>arrival</i>. If it stops coming, one of
/// Prometheus, the rule evaluator, Alertmanager, the receiver config, the NetworkPolicy or
/// this pod's HTTP surface is broken - and every one of those failures is silent from
/// inside the agent, because the symptom is that nothing happens.
/// </para>
/// <para>
/// Deliberately in memory rather than in Postgres. This measures "is the alert path
/// currently delivering to this process", which is a fact about this process and resets
/// correctly on restart: a freshly started pod genuinely has not been told anything yet, and
/// a value restored from disk would let it claim otherwise. The cost of getting it wrong is
/// asymmetric - a stale-looking watchdog after a restart is a false alarm that clears in one
/// scrape interval, while a fresh-looking one hides a dead alert path indefinitely.
/// </para>
/// </remarks>
public sealed class WatchdogMonitor
{
    private readonly IClock clock;
    private long _lastSeenTicks;
    private long _receiptCount;

    public WatchdogMonitor(IClock clock)
        : this(clock, null)
    {
    }

    /// <param name="meters">
    /// When given, the age of the last delivery is published as
    /// <c>hephaisto_watchdog_age_seconds</c> (#137). The status page is read by somebody who is
    /// already looking; the chart's <c>HephaistoAlertPathSilent</c> rule reads this, and is routed
    /// around the agent, so a silent alert path reaches a person who is not.
    /// </param>
    public WatchdogMonitor(IClock clock, IMeterFactory? meters)
    {
        this.clock = clock;
        StartedAt = clock.UtcNow;

        meters?.Create(HephaistoTelemetry.MeterName).CreateObservableGauge(
            HephaistoTelemetry.Metrics.WatchdogAge,
            () => (Age ?? clock.UtcNow - StartedAt).TotalSeconds,
            unit: "s",
            description: "Seconds since the AgentWatchdog alert last reached the agent, or since start if it never has.");
    }

    /// <summary>
    /// How long the alert may be absent before the path is considered broken. Alertmanager's
    /// <c>repeat_interval</c> for this route is 1 minute; three missed deliveries is a
    /// signal, one is a blip.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? LastSeenAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSeenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public long ReceiptCount => Interlocked.Read(ref _receiptCount);

    /// <summary>Null until the first delivery - which is itself the interesting state on a
    /// pod that has been up for an hour.</summary>
    public TimeSpan? Age => LastSeenAt is { } seen ? clock.UtcNow - seen : null;

    /// <summary>
    /// True when the alert path should be treated as broken. Also true before the first
    /// delivery, once the pod has been up longer than the tolerance - an agent that has
    /// never heard from Alertmanager is in exactly the failure this exists to catch.
    /// </summary>
    public bool IsStale =>
        Age is { } age ? age > StaleAfter : clock.UtcNow - StartedAt > StaleAfter;

    public void Record()
    {
        Interlocked.Exchange(ref _lastSeenTicks, clock.UtcNow.ToUniversalTime().Ticks);
        Interlocked.Increment(ref _receiptCount);
    }
}
