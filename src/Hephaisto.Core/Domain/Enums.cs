namespace Hephaisto.Core.Domain;

/// <summary>Where a <see cref="Signal"/> entered the system.</summary>
public enum SignalSource
{
    /// <summary>A Kubernetes watch on pods, events, nodes or jobs.</summary>
    KubernetesWatch = 0,

    /// <summary>An Alertmanager webhook POST.</summary>
    Alertmanager = 1,

    /// <summary>The periodic PromQL sweep.</summary>
    PromQlSweep = 2,

    /// <summary>
    /// Hephaisto's own telemetry alerting on Hephaisto. Signals from this source are
    /// hard-coded to escalate and are never auto-actionable - otherwise the agent can
    /// act on itself in a feedback loop.
    /// </summary>
    SelfMonitoring = 3,
}

/// <summary>
/// The classified shape of a problem. This is the key the runbook lookup and the
/// chaos fixtures are both written against, so adding a member means adding a runbook.
/// </summary>
public enum SignalKind
{
    Unknown = 0,
    CrashLoopBackOff = 1,
    OomKilled = 2,
    ImagePullBackOff = 3,
    Unschedulable = 4,
    ConfigError = 5,
    ReadinessFlapping = 6,
    JobFailed = 7,
    RestartStorm = 8,
    NodePressure = 9,
    PvcNearlyFull = 10,
    ReplicaMismatch = 11,
    TargetDown = 12,
    HighErrorRate = 13,
    HighLatency = 14,
    ObservabilityDegraded = 15,
    BudgetExhausted = 16,
    Watchdog = 17,
}

public enum Severity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// Incident lifecycle. Transitions happen only through <c>IncidentStateMachine</c>,
/// one method per edge, each emitting an audit event.
/// </summary>
public enum IncidentState
{
    Detected = 0,
    Triaging = 1,
    Suppressed = 2,
    Investigating = 3,
    AwaitingApproval = 4,
    Acting = 5,
    Verifying = 6,
    Resolved = 7,
    Escalated = 8,
    Expired = 9,
}

/// <summary>Why an incident was suppressed rather than investigated.</summary>
public enum SuppressionReason
{
    None = 0,
    DuplicateOfOpenIncident = 1,
    Flapping = 2,
    MaintenanceWindow = 3,
    SelfSignal = 4,
    OutOfScopeNamespace = 5,
    Quarantined = 6,
}

/// <summary>Why an incident was escalated to a human instead of resolved.</summary>
public enum EscalationReason
{
    None = 0,
    BudgetExhausted = 1,
    LowConfidence = 2,
    PolicyDenied = 3,
    NoPlanProduced = 4,
    Quarantined = 5,
    ApprovalTimedOut = 6,
    VerificationFailed = 7,
    RollbackPerformed = 8,
    ClusterWideEvent = 9,
    SelfSignal = 10,
    GroundingRejected = 11,
    InvestigationFailed = 12,
    StormCircuitBreaker = 13,
}

/// <summary>Why an investigation loop stopped.</summary>
public enum TerminationReason
{
    /// <summary>The model called the virtual <c>conclude</c> tool. The only clean exit.</summary>
    Concluded = 0,
    StepBudgetExhausted = 1,
    ToolCallBudgetExhausted = 2,
    WallClockExhausted = 3,
    TokenBudgetExhausted = 4,
    CostBudgetExhausted = 5,
    /// <summary>Two consecutive turns produced no tool call and no conclusion.</summary>
    Stalled = 6,
    Faulted = 7,
    Cancelled = 8,
}

/// <summary>
/// The closed vocabulary of things Hephaisto can do to the cluster. This is deliberately
/// an enum and not a set of callable functions: the planning LLM emits one of these by
/// name into a JSON schema, and pure C# turns it into an API call. A prompt injection can
/// therefore at most name an ActionType the policy engine then rejects - it can never
/// reach the Kubernetes API directly.
/// </summary>
public enum ActionType
{
    None = 0,
    RestartPod = 1,
    RolloutRestart = 2,
    RollbackDeployment = 3,
    ScaleWorkload = 4,
    DeleteStuckJob = 5,
    DeleteFailedJobPods = 6,
    SilenceAlert = 7,
    PatchResources = 8,
    CordonNode = 9,
    DrainNode = 10,

    // Permanently denied. Present so that a plan naming one is recorded and rejected
    // with a reason, rather than failing to deserialise into an unknown value.
    DeletePvc = 90,
    DeleteWorkload = 91,
}

public enum RiskTier
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public enum PolicyDecision
{
    /// <summary>Default. The policy engine is default-deny; Allow is always explicit.</summary>
    Deny = 0,
    RequireApproval = 1,
    Allow = 2,
}

public enum ActionState
{
    Proposed = 0,
    AwaitingApproval = 1,
    Approved = 2,
    Denied = 3,
    Executing = 4,
    Executed = 5,
    Failed = 6,
    Verifying = 7,
    Verified = 8,
    RolledBack = 9,
    Expired = 10,
}

/// <summary>
/// Who claimed responsibility for an action. Every action has one, including automatic
/// ones (<c>hephaisto/auto</c>), so "who did this" is answerable uniformly.
/// </summary>
public enum ApprovalSource
{
    /// <summary>
    /// Nobody approved this, and nobody was supposed to. The zero value, deliberately.
    /// </summary>
    /// <remarks>
    /// <c>Ui</c> used to be zero, and <c>ActionPlan.ApprovalSource</c> is never set on the
    /// denial path - so every action the policy engine refused was written to the database
    /// reading "a human typed a name into the console", for actions no human ever saw. The
    /// first eight-fixture e2e run produced two of them. <c>approved_by</c> was correctly
    /// null on the same rows, so nothing was unsafe; it was the audit trail saying something
    /// untrue, which is the one place where misleading is worse than absent.
    /// </remarks>
    NotApplicable = 0,

    /// <summary>Free-text name typed into the Blazor UI. Attribution, not authentication.</summary>
    Ui = 1,

    /// <summary>Free-text name posted to the HTTP API. Attribution, not authentication.</summary>
    Api = 2,

    /// <summary>Executed by policy under L3 autonomy. ApprovedBy is <c>hephaisto/auto</c>.</summary>
    Auto = 3,

    /// <summary>Reserved for the OIDC upgrade; populated from a verified claim.</summary>
    Oidc = 4,
}

/// <summary>
/// The kill switch. Set from three independent places (env var, ConfigMap, database row);
/// the most restrictive wins, and an unreadable source is read as <see cref="Observe"/>.
/// </summary>
public enum AgentMode
{
    /// <summary>Ingest nothing, investigate nothing. Full stop.</summary>
    Off = 0,

    /// <summary>Detect, investigate, diagnose, annotate. Never mutate. The MVP mode.</summary>
    Observe = 1,

    /// <summary>
    /// Run the whole flow including the executor, but every Kubernetes call carries
    /// dryRun=All so the API server validates it and changes nothing.
    /// </summary>
    DryRun = 2,

    /// <summary>L3: execute allowlisted low-risk actions without asking.</summary>
    Auto = 3,
}

public enum VerificationOutcome
{
    Pending = 0,
    Passed = 1,
    Failed = 2,
    Inconclusive = 3,
}

public enum StepKind
{
    LlmTurn = 0,
    ToolCall = 1,
}
