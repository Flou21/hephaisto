using Watchtower.Core.Domain;

namespace Watchtower.Core.Policy;

/// <summary>
/// Bound via IOptionsMonitor so it hot-reloads from the ConfigMap. Every reload writes an
/// audit row - a silent policy change is indistinguishable from an attack.
/// </summary>
public sealed class PolicyOptions
{
    public const string SectionName = "Policy";

    /// <summary>
    /// Namespaces the agent may act in. An allowlist, deliberately: a denylist fails open
    /// for every namespace created after it was written. Empty means act nowhere, which is
    /// the correct default for a process that can delete pods.
    /// </summary>
    public HashSet<string> AllowedNamespaces { get; set; } = [];

    /// <summary>
    /// Never actionable, whatever the allowlist says. Watchtower may not act on itself or
    /// on the observability stack it depends on to see - a self-inflicted outage would
    /// also blind the agent to the fact that it caused one.
    /// </summary>
    public HashSet<string> ProtectedNamespaces { get; set; } =
        ["kube-system", "kube-public", "kube-node-lease", "watchtower", "watchtower-obs"];

    /// <summary>Action types that may execute without a human in Auto mode. Promoted one at a time.</summary>
    public HashSet<ActionType> AutoEnabledActionTypes { get; set; } = [];

    /// <summary>An object carrying any of these labels is never touched.</summary>
    public Dictionary<string, string> ProtectedLabels { get; set; } = new()
    {
        ["watchtower.io/protected"] = "true",
    };

    /// <summary>Opt-in escape hatch for restarting the only replica of a single-replica workload.</summary>
    public string AllowSingleReplicaRestartLabel { get; set; } = "watchtower.io/allow-single-replica-restart";

    public int MaxPodsPerAction { get; set; } = 10;

    public double MaxWorkloadFraction { get; set; } = 0.5;

    public int MaxActionsPerIncident { get; set; } = 3;

    public int MaxActionsPerWorkloadPerHour { get; set; } = 2;

    public int MaxActionsPerHour { get; set; } = 10;

    public int MaxActionsPerDay { get; set; } = 20;

    public TimeSpan WorkloadCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A pod younger than this has not had a fair chance to become healthy.</summary>
    public TimeSpan MinPodAgeBeforeAction { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Above this fraction of unhealthy pods, everything escalates: it is a cluster event.</summary>
    public double ClusterUnhealthyCeiling { get; set; } = 0.3;

    /// <summary>A revision younger than this is a fresh deploy, so rolling back is the obvious move.</summary>
    public TimeSpan RollbackFreshRevisionWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The previous revision must have been healthy at least this long to roll back to it unattended.</summary>
    public TimeSpan RollbackPreviousHealthyMinimum { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Ceiling for an unattended scale-up, and the maximum step size.</summary>
    public int MaxAutoScaleReplicas { get; set; } = 10;

    public int MaxAutoScaleStep { get; set; } = 2;
}
