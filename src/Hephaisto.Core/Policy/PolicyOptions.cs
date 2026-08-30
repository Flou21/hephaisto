using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Policy;

/// <summary>
/// Bound via IOptionsMonitor so it hot-reloads from the ConfigMap.
/// </summary>
/// <remarks>
/// This type is the policy engine's entire configuration surface, so treat a change to it as
/// a change to the safety model. Every reload writes a <c>policy.changed</c> audit row naming
/// what moved - see <c>PolicyChangeAuditor</c> - because a silent policy change is
/// indistinguishable from an attack.
/// </remarks>
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
    /// Never actionable, whatever the allowlist says. Hephaisto may not act on itself or
    /// on the observability stack it depends on to see - a self-inflicted outage would
    /// also blind the agent to the fact that it caused one.
    /// </summary>
    public HashSet<string> ProtectedNamespaces { get; set; } =
        ["kube-system", "kube-public", "kube-node-lease", "hephaisto", "hephaisto-obs"];

    /// <summary>Action types that may execute without a human in Auto mode. Promoted one at a time.</summary>
    public HashSet<ActionType> AutoEnabledActionTypes { get; set; } = [];

    /// <summary>An object carrying any of these labels is never touched.</summary>
    public Dictionary<string, string> ProtectedLabels { get; set; } = new()
    {
        ["hephaisto.io/protected"] = "true",
    };

    /// <summary>
    /// A namespace must carry this label set to <c>true</c> before anything in it may be acted
    /// on - the "second, independent confirmation" the RBAC manifests have described since
    /// before any code read it. Set to empty to disable the check.
    /// </summary>
    /// <remarks>
    /// It is deliberately a different authority from <see cref="AllowedNamespaces"/>. The
    /// allowlist ships in the chart, set by whoever installs Hephaisto; this label is on the
    /// namespace object, set by whoever owns that namespace. Requiring both means a platform
    /// engineer cannot opt someone else's namespace in, and a team cannot opt itself in
    /// without the operator. Note the cost of that: a namespace added to the allowlist and
    /// never labelled is denied, and the reason says so.
    /// </remarks>
    public string RequiredNamespaceLabel { get; set; } = "hephaisto.io/destructive-actions-allowed";

    /// <summary>Opt-in escape hatch for restarting the only replica of a single-replica workload.</summary>
    public string AllowSingleReplicaRestartLabel { get; set; } = "hephaisto.io/allow-single-replica-restart";

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
    /// <summary>
    /// Windows during which nothing may be acted on. Empty means no freeze, which is the
    /// default: an invented window is a control nobody asked for.
    /// </summary>
    public List<MaintenanceWindow> MaintenanceWindows { get; set; } = [];

    public int MaxAutoScaleReplicas { get; set; } = 10;

    public int MaxAutoScaleStep { get; set; } = 2;
}
