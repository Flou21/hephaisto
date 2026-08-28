using Watchtower.Core.Domain;

namespace Watchtower.Core.Policy;

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

    /// <summary>Actions already taken on this workload inside the cooldown window.</summary>
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
public sealed record PolicyResult
{
    public required PolicyDecision Decision { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Set when the decision was downgraded rather than reached directly, e.g. Allow to RequireApproval on budget.</summary>
    public PolicyDecision? DowngradedFrom { get; init; }

    public static PolicyResult Deny(params string[] reasons) =>
        new() { Decision = PolicyDecision.Deny, Reasons = reasons };

    public static PolicyResult Approval(params string[] reasons) =>
        new() { Decision = PolicyDecision.RequireApproval, Reasons = reasons };

    public static PolicyResult Allow(params string[] reasons) =>
        new() { Decision = PolicyDecision.Allow, Reasons = reasons };
}
