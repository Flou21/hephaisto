using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Mcp.Answers;

// What the tools that go deeper answer: the history of an alert, the finding and its evidence,
// the investigation step by step, a raw blob, the actions, the note people keep, code fixes.
// The rule of IncidentAnswers.cs applies: McpText, or [ServerAuthored].

public sealed record IncidentHistory
{
    /// <summary>What the history is of: an alert name or a workload.</summary>
    public required McpText Subject { get; init; }

    [ServerAuthored]
    public required string SubjectKind { get; init; }

    public required int WindowDays { get; init; }

    public required int Total { get; init; }

    public required int Open { get; init; }

    /// <summary>How many incidents are in each state now: how each one ended, or that it has not.</summary>
    public required IReadOnlyDictionary<IncidentState, int> Outcomes { get; init; }

    /// <summary>How they ended, in the words a person asks it: closed by somebody, or over by itself.</summary>
    public required HistoryEndings Endings { get; init; }

    public required IReadOnlyList<HistoryBucket> Buckets { get; init; }

    /// <summary>Median minutes from open to end, over the incidents that ended.</summary>
    public double? MedianMinutes { get; init; }

    public required int NoteEntries { get; init; }

    public required IReadOnlyList<HistoryIncident> Incidents { get; init; }

    [ServerAuthored]
    public string? Note { get; init; }
}

public sealed record HistoryEndings
{
    public required int Open { get; init; }

    public required int ClosedByPerson { get; init; }

    /// <summary>Closed because the alert resolved: nobody had to do anything, or somebody fixed it silently.</summary>
    public required int EndedByAlert { get; init; }

    public required int Resolved { get; init; }

    public required int Expired { get; init; }
}

public sealed record HistoryBucket
{
    [ServerAuthored]
    public required string Start { get; init; }

    public required int Count { get; init; }
}

public sealed record HistoryIncident
{
    public required Guid Id { get; init; }

    public required IncidentState State { get; init; }

    public required McpText Namespace { get; init; }

    public required McpText Workload { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public double? DurationMinutes { get; init; }

    public McpText? ClosedBy { get; init; }

    public McpText? Resolution { get; init; }

    public McpText? RootCause { get; init; }

    public required int Actions { get; init; }

    public required int CodeFixes { get; init; }
}

public sealed record FindingsView
{
    public required Guid IncidentId { get; init; }

    public Guid? InvestigationId { get; init; }

    public McpText? Summary { get; init; }

    public double? Confidence { get; init; }

    public required IReadOnlyList<FindingDetail> Findings { get; init; }

    [ServerAuthored]
    public string? Note { get; init; }
}

public sealed record FindingDetail
{
    public required Guid Id { get; init; }

    public required bool Primary { get; init; }

    public required McpText Category { get; init; }

    public required McpText Hypothesis { get; init; }

    public required double Confidence { get; init; }

    public required IReadOnlyList<EvidenceDetail> Evidence { get; init; }
}

public sealed record EvidenceDetail
{
    public required Guid StepId { get; init; }

    public int? StepOrdinal { get; init; }

    public McpText? Tool { get; init; }

    public required McpText Excerpt { get; init; }

    /// <summary>The raw result the excerpt came from, for fetch_evidence_blob. Absent when it expired.</summary>
    public Guid? BlobId { get; init; }
}

public sealed record InvestigationDetail
{
    public required Guid Id { get; init; }

    public required Guid IncidentId { get; init; }

    public required McpText Model { get; init; }

    /// <summary>Who ran the model loop: InProcess, Job, or JobFallback (v0.12.0 F5).</summary>
    public required McpText Executor { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required TerminationReason Termination { get; init; }

    public required int Steps { get; init; }

    public required int ToolCalls { get; init; }

    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public required decimal CostUsd { get; init; }

    public double? Confidence { get; init; }

    public McpText? Error { get; init; }

    public required int Investigations { get; init; }
}

public sealed record StepDetail
{
    public required Guid Id { get; init; }

    public required int Ordinal { get; init; }

    public required StepKind Kind { get; init; }

    public McpText? Tool { get; init; }

    public McpText? Server { get; init; }

    public McpText? Arguments { get; init; }

    /// <summary>What the model was shown - what grounding checks an excerpt against.</summary>
    public McpText? Digest { get; init; }

    public Guid? BlobId { get; init; }

    public required bool Truncated { get; init; }

    public required int ResultBytes { get; init; }

    public required long DurationMs { get; init; }

    public required bool Failed { get; init; }

    public McpText? Error { get; init; }

    public required DateTimeOffset At { get; init; }
}

public sealed record InvestigationPage
{
    public required InvestigationDetail Investigation { get; init; }

    public required IReadOnlyList<StepDetail> Steps { get; init; }

    /// <summary>Pass as afterStep for the next page; absent on the last one.</summary>
    public int? NextAfterStep { get; init; }
}

public sealed record EvidenceBlobPage
{
    public required Guid BlobId { get; init; }

    [ServerAuthored]
    public required string ContentType { get; init; }

    public required int TotalChars { get; init; }

    public required int Offset { get; init; }

    public int? NextOffset { get; init; }

    /// <summary>Only the lines containing the filter, when one was given.</summary>
    public int? MatchingLines { get; init; }

    public required McpText Text { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record ActionsView
{
    public required Guid IncidentId { get; init; }

    public required IReadOnlyList<ActionDetail> Actions { get; init; }

    [ServerAuthored]
    public required string Note { get; init; }
}

public sealed record ActionDetail
{
    public required Guid Id { get; init; }

    public required ActionType Type { get; init; }

    public required McpText Target { get; init; }

    public McpText? Arguments { get; init; }

    public required RiskTier Risk { get; init; }

    public required ActionState State { get; init; }

    public required PolicyDecision Decision { get; init; }

    public required IReadOnlyList<McpText> DecisionReasons { get; init; }

    public McpText? PredictedEffect { get; init; }

    public required bool DryRun { get; init; }

    public McpText? ApprovedBy { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public ApprovalSource? ApprovalSource { get; init; }

    public McpText? ApprovalReason { get; init; }

    public DateTimeOffset? ExecutedAt { get; init; }

    public McpText? Outcome { get; init; }

    public McpText? Error { get; init; }

    public required IReadOnlyList<VerificationDetail> Verifications { get; init; }
}

public sealed record VerificationDetail
{
    public required int Attempt { get; init; }

    public required DateTimeOffset DueAt { get; init; }

    public DateTimeOffset? RanAt { get; init; }

    public required VerificationOutcome Outcome { get; init; }

    public McpText? Checks { get; init; }

    public McpText? Detail { get; init; }
}

public sealed record NoteDetail
{
    public required McpText AlertName { get; init; }

    public required bool Exists { get; init; }

    public McpText? Body { get; init; }

    public McpText? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public required int EntryCount { get; init; }

    public required IReadOnlyList<NoteEntryDetail> Entries { get; init; }

    [ServerAuthored]
    public string? NextCursor { get; init; }

    [ServerAuthored]
    public string? Url { get; init; }
}

public sealed record NoteEntryDetail
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required McpText Author { get; init; }

    public required McpText Text { get; init; }

    public Guid? IncidentId { get; init; }
}

public sealed record CodeFixRow
{
    public required Guid Id { get; init; }

    public required Guid IncidentId { get; init; }

    public required CodeFixState State { get; init; }

    public required McpText Repository { get; init; }

    public required McpText Workload { get; init; }

    public McpText? Branch { get; init; }

    public McpText? Summary { get; init; }

    public McpText? PullRequestUrl { get; init; }

    public int? PullRequestNumber { get; init; }

    public required decimal CostUsd { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public McpText? FailureReason { get; init; }
}

public sealed record CodeFixPage
{
    public required IReadOnlyList<CodeFixRow> CodeFixes { get; init; }

    [ServerAuthored]
    public string? NextCursor { get; init; }
}

public sealed record CodeFixDetail
{
    public required CodeFixRow Attempt { get; init; }

    public McpText? RootCause { get; init; }

    public double? Confidence { get; init; }

    public McpText? VerificationLevel { get; init; }

    public required IReadOnlyList<McpText> Files { get; init; }

    public required IReadOnlyList<McpText> Steps { get; init; }

    public required IReadOnlyList<McpText> Notes { get; init; }

    public required IReadOnlyList<McpText> NotVerifiable { get; init; }

    public bool? BuildPassed { get; init; }

    public bool? TestsPassed { get; init; }

    public required IReadOnlyList<McpText> Deviations { get; init; }

    public required decimal PlanCostUsd { get; init; }

    public required decimal ImplementCostUsd { get; init; }

    public McpText? RequestedBy { get; init; }

    public McpText? DecidedBy { get; init; }

    public DateTimeOffset? PlanReadyAt { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    [ServerAuthored]
    public required string Note { get; init; }
}

public sealed record IncidentCodeFixes
{
    public required Guid IncidentId { get; init; }

    public required IReadOnlyList<CodeFixRow> Attempts { get; init; }

    public CodeFixDetail? Latest { get; init; }

    /// <summary>Why no attempt was started, when none was: the latest evaluation, or the mode.</summary>
    public McpText? Why { get; init; }

    public required CodeFixMode Mode { get; init; }
}
