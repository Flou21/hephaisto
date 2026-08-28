namespace Watchtower.Agent.Kubernetes;

/// <summary>
/// How strictly <see cref="RbacSelfCheck"/> reacts to holding a verb it must never hold.
/// </summary>
public enum RbacEnforcement
{
    /// <summary>Refuse to boot. The only correct value in a manifest.</summary>
    Enforce = 0,

    /// <summary>
    /// Log the violation and continue. Exists for one reason: a developer's kubeconfig on
    /// this machine is cluster-admin, so every forbidden probe comes back allowed and the
    /// agent could never be run outside the cluster at all. Setting this in a Deployment
    /// removes the boot-time guarantee that a fat-fingered RoleBinding is caught in seconds
    /// rather than during an incident.
    /// </summary>
    WarnOnly = 1,
}

/// <summary>
/// Everything the Kubernetes layer needs that is an operational choice rather than a fact
/// about the cluster API. Bound from the <c>Kubernetes</c> section.
/// </summary>
public sealed class KubernetesOptions
{
    public const string SectionName = "Kubernetes";

    /// <summary>
    /// Goes into every fingerprint, so two clusters reporting into one database cannot
    /// collide - see <c>SignalFingerprinter</c>. Changing it re-keys every future signal,
    /// which means historical incidents stop deduping against new ones; treat it as
    /// immutable once a cluster has reported.
    /// </summary>
    public string ClusterName { get; set; } = "default";

    /// <summary>Only consulted outside the cluster. Null means the ambient KUBECONFIG.</summary>
    public string? KubeconfigPath { get; set; }

    /// <summary>
    /// Null means the kubeconfig's current context. Named explicitly because this machine
    /// calls the cluster <c>rancher-desktop</c> and the laptop calls the same one
    /// <c>studio-rancher-desktop</c>.
    /// </summary>
    public string? KubeconfigContext { get; set; }

    /// <summary>
    /// Namespaces the read tools may look at. Empty means every namespace, which is the
    /// intended default: reading is cluster-wide by design (a node-pressure diagnosis has to
    /// see pods it does not own), and the write boundary is RBAC plus the policy engine, not
    /// this list. It exists for a deployment that wants the read surface narrowed as well.
    /// </summary>
    public HashSet<string> ReadableNamespaces { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Always refused, whatever <see cref="ReadableNamespaces"/> says. Empty by default -
    /// hiding kube-system from the agent would blind it to exactly the node-level events the
    /// NodePressure runbook is built around.
    /// </summary>
    public HashSet<string> DeniedNamespaces { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Bounded on purpose. A node restart emits hundreds of events in seconds; an unbounded
    /// channel turns that into an OOM of the agent itself, which is the one failure this
    /// process must not have. Overflow drops the oldest and is counted as
    /// <c>watchtower.signals.dropped</c>.
    /// </summary>
    public int SignalQueueCapacity { get; set; } = 2_048;

    /// <summary>
    /// A full relist happens on this cadence even when the watch looks healthy. Watches die
    /// silently - a half-open connection delivers nothing and reports nothing - and the only
    /// cheap defence is to stop trusting them.
    /// </summary>
    public TimeSpan RelistInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Sent as <c>timeoutSeconds</c>, so the API server closes the watch itself and the
    /// client notices. Shorter than the typical idle timeout of anything in between.
    /// </summary>
    public TimeSpan WatchTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// More than this many signals inside <see cref="StormWindow"/> stops individual
    /// incidents and emits one aggregate instead. Forty investigations at roughly $0.30 each
    /// is a real cost event, and forty incidents about one node restart is not a useful
    /// description of what happened.
    /// </summary>
    public int StormThreshold { get; set; } = 50;

    public TimeSpan StormWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a still-running storm re-reports itself while it lasts.</summary>
    public TimeSpan StormAggregateInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Restarts inside <see cref="RestartStormWindow"/> before a pod is called a storm.</summary>
    public int RestartStormThreshold { get; set; } = 3;

    public TimeSpan RestartStormWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ready flips inside <see cref="ReadinessFlapWindow"/>. Four, not two: one ready-&gt;
    /// not-ready-&gt;ready cycle is a rolling update or a slow start, not a flap.
    /// </summary>
    public int ReadinessFlapThreshold { get; set; } = 4;

    public TimeSpan ReadinessFlapWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Lines requested from the kubelet before digestion. Generous because
    /// <c>LogDigester</c> collapses repetition anyway, and the failure is usually near the
    /// start of a crash loop rather than the end.
    /// </summary>
    public int LogTailLines { get; set; } = 2_000;

    /// <summary>Hard ceiling on the transfer itself, so a chatty pod cannot stall a tool call.</summary>
    public int LogLimitBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Rows any list tool will print before truncating with a count.</summary>
    public int MaxRows { get; set; } = 200;

    public RbacEnforcement RbacMode { get; set; } = RbacEnforcement.Enforce;
}
