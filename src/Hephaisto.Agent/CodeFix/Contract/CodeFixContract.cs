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

    public required IReadOnlyList<string> Notes { get; init; }

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
