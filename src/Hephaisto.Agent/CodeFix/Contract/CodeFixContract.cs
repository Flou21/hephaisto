using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hephaisto.Agent.CodeFix.Contract;

/// <summary>
/// The v1 contract with the coder runner, as C# records. The JSON Schemas vendored beside this file
/// are the source of truth; <c>CodeFixSchemaParityTests</c> hold these records to them.
/// </summary>
/// <remarks>
/// Deserialisation disallows unmapped members. A result carrying a field this version does not know
/// is a runner from a different contract, and the honest reading of that is a contract violation -
/// not a best-effort parse that silently drops whatever the new field meant.
/// </remarks>
public static class CodeFixContract
{
    public const string Version = "1";

    /// <summary>
    /// The version of a request for a work item (<see cref="CodeFixWorkItemRequest"/>). Only the
    /// request has a second version; both results are version 1 for either.
    /// </summary>
    public const string WorkItemVersion = "2";

    /// <summary>The plan result's caps for <c>questions</c>, as its schema states them. A test holds the two together.</summary>
    public const int MaxQuestions = 10;

    public const int MaxQuestionChars = 600;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false,
    };
}

public sealed record CodeFixRequest
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public required Guid IncidentId { get; init; }

    /// <summary><c>plan</c> or <c>implement</c>.</summary>
    public required string Phase { get; init; }

    public required CodeFixBudget Budget { get; init; }

    public required CodeFixRepository Repository { get; init; }

    public required CodeFixContextRef Context { get; init; }

    public required CodeFixIncident Incident { get; init; }

    public required IReadOnlyList<CodeFixFinding> Findings { get; init; }

    public required string? InvestigationSummary { get; init; }

    /// <summary>Null in the plan phase; the approved plan in the implement phase.</summary>
    public required CodeFixPlanResult? Plan { get; init; }
}

/// <summary>
/// The request for a piece of work somebody handed over (contract version 2,
/// <c>codefix-request-v2.schema.json</c>): <see cref="WorkItem"/> in place of the incident, the
/// findings and the investigation summary, which are absent rather than empty.
/// </summary>
/// <remarks>
/// A second record rather than nullable members on <see cref="CodeFixRequest"/>: that one
/// serialises exactly as it did before there were work items, which is what "version 1 is
/// unchanged" has to mean for a runner that refuses unknown members.
/// </remarks>
public sealed record CodeFixWorkItemRequest
{
    public string ContractVersion { get; init; } = CodeFixContract.WorkItemVersion;

    public required Guid AttemptId { get; init; }

    /// <summary><c>plan</c> or <c>implement</c>.</summary>
    public required string Phase { get; init; }

    public required CodeFixBudget Budget { get; init; }

    public required CodeFixRepository Repository { get; init; }

    public required CodeFixContextRef Context { get; init; }

    public required CodeFixWorkItem WorkItem { get; init; }

    /// <summary>
    /// What the earlier attempt for the same work item had planned and asked, when this one
    /// plans it AGAIN - an approver's <c>/replan</c>, or a fresh assignment after an attempt
    /// ended. Null, and then absent from the document, for the first plan of a hand-over: that
    /// request is byte for byte what it was before there was a second attempt.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodeFixPreviousPlan? Previous { get; init; }

    /// <summary>Null in the plan phase; the approved plan in the implement phase.</summary>
    public required CodeFixPlanResult? Plan { get; init; }
}

/// <summary>
/// The earlier plan, as far as a replanning Job needs it: what was planned, and what was asked -
/// the questions that the comments passed on with it answer. A model's text about a stranger's,
/// so the runner hands it over as data.
/// </summary>
public sealed record CodeFixPreviousPlan
{
    public required string Summary { get; init; }

    public required IReadOnlyList<string> Questions { get; init; }

    public required IReadOnlyList<string> Steps { get; init; }
}

/// <summary>The issue, as the coder is told about it. Title, author, body and comments are untrusted.</summary>
public sealed record CodeFixWorkItem
{
    public required string Source { get; init; }

    /// <summary><c>owner/repo</c>, from the install's list - not from the issue.</summary>
    public required string Repository { get; init; }

    public required int Number { get; init; }

    public required string Url { get; init; }

    public required string Title { get; init; }

    public required string? Type { get; init; }

    public required string Author { get; init; }

    /// <summary>
    /// The snapshot: the issue's text when it was taken, or when it was last handed over or
    /// replanned on purpose. Never a silent second read of the issue.
    /// </summary>
    public required string Body { get; init; }

    /// <summary>
    /// Empty for the first plan of a hand-over. For a replan: what the issue's author and the
    /// approvers wrote since the issue was handed over, oldest first - nobody else's words, and
    /// never Hephaisto's own.
    /// </summary>
    public required IReadOnlyList<CodeFixWorkItemComment> Comments { get; init; }
}

public sealed record CodeFixWorkItemComment(string Author, string Body);

public sealed record CodeFixBudget(decimal MaxCostUsd, int DeadlineSeconds);

public sealed record CodeFixRepository(string Url, string DefaultBranch, string Path, string Branch);

public sealed record CodeFixContextRef(string RepositoryUrl, string Ref);

public sealed record CodeFixIncident
{
    public required string Title { get; init; }

    public required string Kind { get; init; }

    public required string Severity { get; init; }

    public required CodeFixTarget Target { get; init; }

    public required string? Image { get; init; }

    public required string? RolloutRevision { get; init; }

    public required string EscalationReason { get; init; }
}

public sealed record CodeFixTarget(string Namespace, string Kind, string Name, string Workload);

public sealed record CodeFixFinding
{
    public required Guid Id { get; init; }

    public required bool Primary { get; init; }

    public required string Category { get; init; }

    public required double Confidence { get; init; }

    public required string Hypothesis { get; init; }

    public required IReadOnlyList<CodeFixEvidence> Evidence { get; init; }
}

public sealed record CodeFixEvidence(Guid StepId, string Tool, string Excerpt);

public sealed record CodeFixVerification
{
    /// <summary><c>tests</c>, <c>build-only</c>, <c>typecheck-only</c> or <c>none</c>.</summary>
    public required string Level { get; init; }

    public required IReadOnlyList<string> NotVerifiable { get; init; }
}

public sealed record CodeFixDenial(string Tool, string Input, string Reason);

public sealed record CodeFixPlanResult
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "plan";

    /// <summary><c>planned</c>, <c>not_a_code_problem</c>, <c>insufficient_context</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }

    public required string Summary { get; init; }

    public required string RootCause { get; init; }

    public required double Confidence { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public required IReadOnlyList<string> Steps { get; init; }

    public required CodeFixVerification Verification { get; init; }

    public required bool NeedsCait { get; init; }

    /// <summary>What was deliberately left out and why, suspected prompt injection, observations.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// What only a person can decide, each with what the plan assumed meanwhile. Optional in
    /// the contract, and absent means none: a plan that asks nothing is the document it always
    /// was, which is what keeps every stored plan - and an incident's version 1 implement
    /// request, which embeds one - unchanged. At most <see cref="CodeFixContract.MaxQuestions"/>
    /// of at most <see cref="CodeFixContract.MaxQuestionChars"/> characters by the schema; this
    /// side does not validate against it, so whatever shows or forwards them caps them again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Questions { get; init; }

    public required string? AnalysedRef { get; init; }

    public required string? ContextSha { get; init; }

    public required decimal CostUsd { get; init; }

    public required string? SessionId { get; init; }

    public required string? Error { get; init; }

    public required IReadOnlyList<CodeFixDenial> DeniedToolCalls { get; init; }
}

public sealed record CodeFixImplementResult
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "implement";

    /// <summary>
    /// <c>pr_opened</c>, <c>already_exists</c>, <c>no_changes</c>, <c>build_failed</c>,
    /// <c>tests_failed</c>, <c>policy_diff</c> or <c>failed</c>.
    /// </summary>
    public required string Outcome { get; init; }

    public required string? Branch { get; init; }

    public required string? PrUrl { get; init; }

    public required int? PrNumber { get; init; }

    public required string? BaseCommit { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public required bool BuildPassed { get; init; }

    public required bool TestsPassed { get; init; }

    public required string LogTail { get; init; }

    public required IReadOnlyList<string> Deviations { get; init; }

    public required decimal CostUsd { get; init; }

    public required string? SessionId { get; init; }

    public required string? Error { get; init; }

    public required IReadOnlyList<CodeFixDenial> DeniedToolCalls { get; init; }
}
