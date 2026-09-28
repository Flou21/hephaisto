namespace Hephaisto.Agent.Options;

/// <summary>Tuning for the ingest hot path. Bound from <c>Ingest:*</c>.</summary>
public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>
    /// Included in every fingerprint, and must match the <c>cluster</c> external label on
    /// Tempo's metrics-generator and the OTel collector. A mismatch does not error - it
    /// silently returns nothing from every correlation query that filters on cluster.
    /// </summary>
    /// <remarks>
    /// Filled from <c>Cluster:Name</c> by <see cref="ClusterServiceCollectionExtensions.AddHephaistoCluster"/>
    /// (backlog #139). Empty by default: it used to be the development machine's name, which
    /// every other install then inherited.
    /// </remarks>
    public string ClusterName { get; set; } = string.Empty;

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
    /// An alert that fires again within this long of its incident closing reopens that incident
    /// rather than opening another (#129, decided 2026-09-28).
    /// </summary>
    /// <remarks>
    /// Only for Alertmanager signals, whose resolve is what closes an incident in the first
    /// place. Twenty-four hours: an alert that clears overnight and returns in the morning is the
    /// same fault somebody already has context on; one that returns next week is not.
    /// </remarks>
    public TimeSpan ReopenWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Namespaces whose signals are always escalated and never auto-actionable. The agent
    /// alerting on itself is intended - the agent acting on itself is a feedback loop.
    /// </summary>
    public HashSet<string> SelfNamespaces { get; set; } = ["hephaisto", "hephaisto-obs", "hephaisto-coder"];
}
