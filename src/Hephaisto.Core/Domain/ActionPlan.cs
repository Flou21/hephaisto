namespace Hephaisto.Core.Domain;

/// <summary>
/// What the planning phase produced. Note what is absent: any way to execute anything.
/// A plan is inert data that the policy engine then judges.
/// </summary>
public sealed class ActionPlan
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid InvestigationId { get; set; }

    public Investigation? Investigation { get; set; }

    /// <summary>One-paragraph statement of what the model believes and intends.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// True when the model concluded no action is warranted. A perfectly good outcome -
    /// most incidents want a diagnosis, not a change.
    /// </summary>
    public bool NoActionRequired { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<AgentAction> Actions { get; set; } = [];
}

/// <summary>
/// A single proposed or executed change. Named <c>AgentAction</c> rather than <c>Action</c>
/// to stay out of <see cref="System.Action"/>'s way.
/// </summary>
public sealed class AgentAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid IncidentId { get; set; }

    public Incident? Incident { get; set; }

    public Guid? ActionPlanId { get; set; }

    public ActionPlan? ActionPlan { get; set; }

    public ActionType Type { get; set; }

    public TargetRef Target { get; set; } = new();

    /// <summary>Typed arguments as JSON, e.g. <c>{"replicas":3}</c>. Never a shell string.</summary>
    public string? Arguments { get; set; }

    public RiskTier Risk { get; set; }

    public ActionState State { get; set; } = ActionState.Proposed;

    /// <summary>What the model expects to happen. Compared against reality by the verifier.</summary>
    public string? PredictedEffect { get; set; }

    /// <summary>
    /// How to undo this, as JSON. Absence is meaningful: an action with no rollback spec
    /// can never be auto-executed, because a failed verification would have no recourse.
    /// </summary>
    public string? RollbackSpec { get; set; }

    /// <summary>Findings this action is justified by. A plan citing a dropped finding is rejected.</summary>
    public List<Guid> EvidenceFindingIds { get; set; } = [];

    // --- decision ---

    public PolicyDecision Decision { get; set; }

    /// <summary>Every reason the policy engine gave, allow or deny. The audit trail's core value.</summary>
    public List<string> DecisionReasons { get; set; } = [];

    /// <summary>
    /// Always populated, including for automatic actions (<c>hephaisto/auto</c>), so
    /// "who did this" is answerable uniformly. Free text in MVP: attribution, not
    /// authentication - see the OIDC note in CLAUDE.md.
    /// </summary>
    public string? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public string? ApprovalReason { get; set; }

    public ApprovalSource ApprovalSource { get; set; }

    // --- execution ---

    /// <summary>True when executed with <c>dryRun=All</c>: the API server validated it and changed nothing.</summary>
    public bool DryRun { get; set; }

    public AgentMode ModeAtExecution { get; set; }

    /// <summary>State of the target before the change, as JSON. The rollback reads from this.</summary>
    public string? PreState { get; set; }

    public string? PostState { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    public string? Outcome { get; set; }

    public string? Error { get; set; }

    /// <summary>Set when this action undoes another. Rollbacks bypass the action budget:
    /// you must always be able to undo, even at the cap.</summary>
    public Guid? IsRollbackOf { get; set; }

    public List<Verification> Verifications { get; set; } = [];
}

/// <summary>
/// A scheduled check that the action actually helped, at T+60s, T+5m and T+15m.
/// Deterministic predicates per action type - never an LLM judging its own work.
/// </summary>
public sealed class Verification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ActionId { get; set; }

    public AgentAction? Action { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset? RanAt { get; set; }

    public VerificationOutcome Outcome { get; set; }

    /// <summary>Each predicate and whether it held, as JSON.</summary>
    public string? Checks { get; set; }

    public string? Detail { get; set; }
}
