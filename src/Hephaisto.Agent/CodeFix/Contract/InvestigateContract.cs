namespace Hephaisto.Agent.CodeFix.Contract;

/// <summary>
/// What Hephaisto hands an investigator Job (v0.12.0 F5), mounted like a coder's request at
/// <c>/work/in/request.json</c>. <c>investigate-request.schema.json</c> is the source of truth;
/// <c>CodeFixSchemaParityTests</c> hold this record to it.
/// </summary>
/// <remarks>
/// It carries a bearer token for the investigator endpoint. The token opens one investigation's
/// read-only tools until the run's deadline and nothing else, and the ConfigMap holding it is owned
/// by the Job, so garbage collection removes both together.
/// </remarks>
public sealed record InvestigateRequest
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public required Guid IncidentId { get; init; }

    public required Guid InvestigationId { get; init; }

    public string Phase { get; init; } = "investigate";

    public required InvestigateBudget Budget { get; init; }

    public required CodeFixContextRef Context { get; init; }

    public required InvestigateEndpoint Endpoint { get; init; }

    public required InvestigateIncident Incident { get; init; }

    /// <summary>What <c>PromptComposer</c> composed: the one source of the investigation prompt.</summary>
    public required string SystemPrompt { get; init; }

    public required string OpeningMessage { get; init; }

    /// <summary>The workload's repository at its running revision, read-only. Null: no checkout.</summary>
    public required InvestigateSource? Source { get; init; }
}

public sealed record InvestigateBudget(decimal MaxCostUsd, int DeadlineSeconds, int MaxTurns);

public sealed record InvestigateEndpoint(string Url, string Token);

public sealed record InvestigateIncident
{
    public required string Title { get; init; }

    public required string Kind { get; init; }

    public required string Severity { get; init; }

    public required CodeFixTarget Target { get; init; }
}

public sealed record InvestigateSource(string Url, string DefaultBranch, string Path, string? Ref, string? Image);

/// <summary>What an investigator Job answers, as the last framed block of its log.</summary>
/// <remarks>
/// Run metadata only. The findings arrived through the endpoint's <c>conclude</c> call and are a
/// recorded step; nothing here is evidence, and nothing here is grounded against.
/// </remarks>
public sealed record InvestigateResult
{
    public string ContractVersion { get; init; } = CodeFixContract.Version;

    public required Guid AttemptId { get; init; }

    public string Phase { get; init; } = "investigate";

    /// <summary>
    /// <c>concluded</c>, <c>no_conclusion</c>, <c>budget_exhausted</c>, <c>max_turns</c>,
    /// <c>rate_limited</c>, <c>no_credential</c> or <c>failed</c>.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>The SDK's figure. Notional when <see cref="Billing"/> is <c>subscription</c>.</summary>
    public required decimal CostUsd { get; init; }

    /// <summary><c>subscription</c>, <c>api</c> or <c>fake</c>.</summary>
    public required string Billing { get; init; }

    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public required int Turns { get; init; }

    public required string? Model { get; init; }

    public required string? SessionId { get; init; }

    public required string? ContextSha { get; init; }

    public required InvestigateSourceResult? Source { get; init; }

    /// <summary>The conclude call's code references the runner confirmed exist in the clone.</summary>
    public required IReadOnlyList<InvestigateCodeRef> CodeRefs { get; init; }

    public required string? Error { get; init; }

    public required IReadOnlyList<CodeFixDenial> DeniedToolCalls { get; init; }
}

public sealed record InvestigateSourceResult(bool Cloned, string? AnalysedRef, string? Error);

public sealed record InvestigateCodeRef(int Finding, string Path, int Line, int? EndLine, string? Note);
