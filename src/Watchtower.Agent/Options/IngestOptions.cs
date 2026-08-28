namespace Watchtower.Agent.Options;

/// <summary>Tuning for the ingest hot path. Bound from <c>Ingest:*</c>.</summary>
public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>
    /// Included in every fingerprint, and must match the <c>cluster</c> external label on
    /// Tempo's metrics-generator and the OTel collector. A mismatch does not error - it
    /// silently returns nothing from every correlation query that filters on cluster.
    /// </summary>
    public string ClusterName { get; set; } = "studio-rancher-desktop";

    /// <summary>
    /// Identical signals arriving inside this window are the same problem restated, not a
    /// new one. Kept short: beyond a few minutes a repeat is genuinely worth re-examining.
    /// </summary>
    public TimeSpan BurstWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>More than this many incidents for one workload in <see cref="FlapWindow"/> means flapping.</summary>
    public int FlapThreshold { get; set; } = 3;

    public TimeSpan FlapWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a flapping workload stays suppressed. Long enough that a human looks at it.</summary>
    public TimeSpan FlapCooldown { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Signals on one workload inside this window join the same incident.</summary>
    public TimeSpan CorrelationWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Namespaces whose signals are always escalated and never auto-actionable. The agent
    /// alerting on itself is intended - the agent acting on itself is a feedback loop.
    /// </summary>
    public HashSet<string> SelfNamespaces { get; set; } = ["watchtower", "watchtower-obs"];
}
