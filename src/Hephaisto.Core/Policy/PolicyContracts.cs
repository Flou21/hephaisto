using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Policy;

/// <summary>A request to change something, as judged by the policy engine.</summary>
public sealed record ActionRequest
{
    public required Guid ActionId { get; init; }

    public required Guid IncidentId { get; init; }

    public required ActionType Type { get; init; }

    public required TargetRef Target { get; init; }

    public required RiskTier Risk { get; init; }

    /// <summary>How many pods this is expected to disturb. Drives the blast-radius check.</summary>
    public int AffectedPodCount { get; init; } = 1;

    /// <summary>True when undoing a previous action. Bypasses budget, never bypasses RBAC or self-protection.</summary>
    public bool IsRollback { get; init; }

    public bool HasRollbackSpec { get; init; }

    /// <summary>Findings backing this action, after grounding verification. Empty means unjustified.</summary>
    public IReadOnlyList<Guid> GroundedFindingIds { get; init; } = [];
}

/// <summary>
/// Everything the policy engine is allowed to know about the world, gathered by the caller
/// immediately before the decision. Passing facts in rather than letting the engine fetch
/// them is what keeps it a pure function - and therefore exhaustively unit-testable without
/// a cluster.
/// </summary>
public sealed record ClusterFacts
{
    public required DateTimeOffset Now { get; init; }

    public required AgentMode Mode { get; init; }

    public WorkloadFacts? Workload { get; init; }

    public NodeFacts? Node { get; init; }

    /// <summary>Labels on the target object itself. Checked against the protected-label list.</summary>
    public IReadOnlyDictionary<string, string> TargetLabels { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Labels on the target's <b>namespace</b>, which is a different question from
    /// <see cref="TargetLabels"/> and needs its own field rather than being merged into it.
    /// </summary>
    /// <remarks>
    /// The allowlist is set by whoever installs the chart; the namespace label is set by
    /// whoever owns the namespace. Requiring both means neither party can opt a namespace in
    /// alone. Merging the two label sets would let a workload label satisfy a namespace
    /// requirement, which is precisely the confusion the second confirmation exists to avoid.
    /// </remarks>
    public IReadOnlyDictionary<string, string> NamespaceLabels { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Actions taken on this workload in the last hour, counted against
    /// <see cref="PolicyOptions.MaxActionsPerWorkloadPerHour"/>.
    /// </summary>
    /// <remarks>
    /// Count over one hour, not over <see cref="PolicyOptions.WorkloadCooldown"/> - the two
    /// windows are different and independent. The cooldown is a separate, shorter freeze
    /// evaluated from <see cref="LastActionOnWorkloadAt"/>; this is the budget. Filling this
    /// field from the 15-minute cooldown window instead would silently under-report and let
    /// a workload take several times its hourly allowance.
    /// </remarks>
    public int RecentActionsOnWorkload { get; init; }

    public DateTimeOffset? LastActionOnWorkloadAt { get; init; }

    public int ActionsOnIncident { get; init; }

    public int ActionsClusterWideLastHour { get; init; }

    public int ActionsClusterWideLastDay { get; init; }

    /// <summary>Set by the oscillation detector; while in the future nothing may be done to this workload.</summary>
    public DateTimeOffset? QuarantinedUntil { get; init; }

    /// <summary>
    /// Fraction of pods cluster-wide that are unhealthy. Above the configured ceiling this
    /// is a cluster-level event, and restarting one pod is the wrong response to it.
    /// </summary>
    public double ClusterUnhealthyFraction { get; init; }

    public bool InMaintenanceWindow { get; init; }
}

/// <summary>Facts about the workload owning the target, read at decision time.</summary>
public sealed record WorkloadFacts
{
    public required string Key { get; init; }

    public required string Kind { get; init; }

    public int DesiredReplicas { get; init; }

    public int ReadyReplicas { get; init; }

    public int UpdatedReplicas { get; init; }

    public long Generation { get; init; }

    /// <summary>
    /// Lags <see cref="Generation"/> while the controller is still reconciling. A gap means
    /// a rollout is in flight - most likely a human deploying. Do not fight it.
    /// </summary>
    public long ObservedGeneration { get; init; }

    /// <summary>Age of the youngest pod. Very new pods have not had a chance to become healthy yet.</summary>
    public TimeSpan? YoungestPodAge { get; init; }

    /// <summary>Age of the current revision. Drives whether a rollback is obviously safe.</summary>
    public TimeSpan? CurrentRevisionAge { get; init; }

    /// <summary>How long the previous revision stayed healthy. A rollback to a bad revision helps nobody.</summary>
    public TimeSpan? PreviousRevisionHealthyFor { get; init; }

    public bool RolloutInFlight => ObservedGeneration < Generation || UpdatedReplicas != DesiredReplicas;
}

public sealed record NodeFacts
{
    public required string Name { get; init; }

    public bool Unschedulable { get; init; }

    public int PodCount { get; init; }

    public bool MemoryPressure { get; init; }

    public bool DiskPressure { get; init; }
}

/// <summary>
/// The verdict. <see cref="Reasons"/> is populated on allow as well as deny, because
/// "why did it think this was fine" is the question asked after something goes wrong.
/// </summary>
/// <summary>
/// Why the policy engine decided what it decided, as a closed vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the human-readable reasons could not be a metric label and their absence
/// left a real question unanswerable. <c>hephaisto.policy.decisions</c> used to carry the
/// verdict's first reason as a label value, and those reasons are prose written for a person -
/// "workload is quarantined until 2026-08-30T12:34:56.789Z", "pod is 45s old, younger than the
/// 120s minimum". Timestamps and ages in a label are unbounded series on a counter that fires
/// for every proposed action, which is backlog #12. Taking the prose out was urgent; what went
/// with it was the per-gate breakdown, and "how often does the cooldown bite versus the
/// namespace allowlist" is a genuinely useful question when tuning.
/// </para>
/// <para>
/// <b>Carried beside the human text at each site, never derived from it.</b> Parsing a code back
/// out of prose would be brittle in the one place brittleness is least acceptable, and it would
/// silently start producing the wrong answer the moment somebody improved a sentence.
/// </para>
/// </remarks>
public enum PolicyReasonCode
{
    /// <summary>No specific gate. Present so a default-constructed value is not a lie about one.</summary>
    None = 0,

    // --- denials, in the order the gates run ---
    NeverApprovable = 1,
    ProtectedNamespace = 2,
    NamespaceNotAllowed = 3,
    NamespaceLabelMissing = 4,
    ProtectedLabel = 5,
    AgentOff = 6,
    ObserveMode = 7,
    Quarantined = 8,
    Ungrounded = 9,
    RolloutInFlight = 10,
    PodTooYoung = 11,
    MaintenanceWindow = 12,
    ClusterWideEvent = 13,
    BlastRadiusPods = 14,
    BlastRadiusFraction = 15,
    LastReadyReplica = 16,
    WorkloadCooldown = 17,
    NoRoutingRule = 18,

    // --- downgrades: allow-eligible, but not unattended ---
    NotAllowEligible = 30,
    NotAutoMode = 31,
    TypeNotAutoEnabled = 32,
    BudgetExhausted = 33,
    NoRollbackSpec = 34,
}

public sealed record PolicyResult
{
    public required PolicyDecision Decision { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>
    /// The gates that fired, in the order they were checked, alongside <see cref="Reasons"/>.
    /// </summary>
    /// <remarks>
    /// Safe as a metric label where <see cref="Reasons"/> is not. The FIRST entry is the one
    /// worth labelling on: the gates run cheapest-and-most-certain first, so it is both the most
    /// specific answer and the one a human would give.
    /// </remarks>
    public IReadOnlyList<PolicyReasonCode> Codes { get; init; } = [];

    /// <summary>The gate to attribute this decision to, or <c>None</c> for a clean allow.</summary>
    public PolicyReasonCode PrimaryCode => Codes.Count > 0 ? Codes[0] : PolicyReasonCode.None;

    /// <summary>Set when the decision was downgraded rather than reached directly, e.g. Allow to RequireApproval on budget.</summary>
    public PolicyDecision? DowngradedFrom { get; init; }

    public static PolicyResult Deny(params string[] reasons) =>
        new() { Decision = PolicyDecision.Deny, Reasons = reasons };

    public static PolicyResult Approval(params string[] reasons) =>
        new() { Decision = PolicyDecision.RequireApproval, Reasons = reasons };

    public static PolicyResult Allow(params string[] reasons) =>
        new() { Decision = PolicyDecision.Allow, Reasons = reasons };
}
