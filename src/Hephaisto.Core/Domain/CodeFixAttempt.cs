using Hephaisto.Core.CodeFix;

namespace Hephaisto.Core.Domain;

/// <summary>
/// One try at a change in code: a read-only plan Job, a human decision, and - if approved - an
/// implement Job that opens a Draft PR. For an incident's cause, or for a piece of work somebody
/// handed over (v0.14.0).
/// </summary>
/// <remarks>
/// <para>
/// Its own entity with its own lifecycle (<see cref="CodeFixState"/>), attached to the incident but
/// never a state of it: an incident is routinely Closed while its PR waits for review.
/// </para>
/// <para>
/// <b>It has exactly one subject</b>: <see cref="IncidentId"/> or <see cref="WorkItemId"/>, never
/// both and never neither. Postgres holds that in a check constraint, and at most one open
/// attempt per subject in two partial unique indexes.
/// </para>
/// <para>
/// The three JSON columns hold the documents exactly as they crossed the process boundary - the
/// request as sent, each result as parsed. They are what an operator reads when asking "what did the
/// coder see and say", and the request is what a cassette is later cut from, so they are stored
/// verbatim rather than re-derived.
/// </para>
/// </remarks>
public sealed class CodeFixAttempt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The incident this is for; null when it is for a <see cref="WorkItem"/>.</summary>
    public Guid? IncidentId { get; set; }

    public Incident? Incident { get; set; }

    /// <summary>The work item this is for; null when it is for an <see cref="Incident"/>.</summary>
    public Guid? WorkItemId { get; set; }

    public WorkItem? WorkItem { get; set; }

    /// <summary>The investigation whose findings the request was built from.</summary>
    public Guid? InvestigationId { get; set; }

    public CodeFixState State { get; set; } = CodeFixState.Eligible;

    /// <summary>
    /// Workload key the repository was mapped from. Empty for a work item: an issue names a
    /// repository, not something that runs, and no "one per workload" rule counts an empty one.
    /// </summary>
    public string Workload { get; set; } = string.Empty;

    public string RepositoryUrl { get; set; } = string.Empty;

    public string DefaultBranch { get; set; } = "main";

    /// <summary>Assigned by Hephaisto; the runner may push nothing else. <c>hephaisto/codefix-&lt;id12&gt;</c>.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>Who or what started it: <c>hephaisto/system</c>, or the human who asked.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public string? RequestJson { get; set; }

    public string? PlanResultJson { get; set; }

    public string? ImplementResultJson { get; set; }

    // Denormalised from the plan result, for the list view and the notification card.
    public string? Summary { get; set; }

    public string? RootCause { get; set; }

    public double? Confidence { get; set; }

    /// <summary><c>tests</c>, <c>build-only</c>, <c>typecheck-only</c> or <c>none</c>.</summary>
    public string? VerificationLevel { get; set; }

    /// <summary>The fix needs a Cait change first. Plan only; never implemented in v0.9.0.</summary>
    public bool NeedsCait { get; set; }

    /// <summary>The commit the plan was made against - the running image's tag when it is a sha.</summary>
    public string? AnalysedRef { get; set; }

    public string? ContextSha { get; set; }

    public string? PlanJobName { get; set; }

    public string? ImplementJobName { get; set; }

    public string? PlanSessionId { get; set; }

    public string? ImplementSessionId { get; set; }

    public decimal PlanCostUsd { get; set; }

    public decimal ImplementCostUsd { get; set; }

    public decimal TotalCostUsd => PlanCostUsd + ImplementCostUsd;

    /// <summary>
    /// The comment on the issue that carries this attempt's plan. GitHub's id, beyond 32 bits.
    /// Null for an incident's attempt, and until the comment was written.
    /// </summary>
    public long? PlanCommentId { get; set; }

    public string? PrUrl { get; set; }

    public int? PrNumber { get; set; }

    public string? ApprovedBy { get; set; }

    public ApprovalSource ApprovalSource { get; set; }

    /// <summary>Why it failed, was denied, expired or was cancelled.</summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PlanStartedAt { get; set; }

    public DateTimeOffset? PlanReadyAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? ImplementStartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? TraceId { get; set; }
}
