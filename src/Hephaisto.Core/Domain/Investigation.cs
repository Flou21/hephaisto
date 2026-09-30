namespace Hephaisto.Core.Domain;

/// <summary>
/// One pass of the three-phase loop over one incident: investigate with read-only tools,
/// then plan with no tools at all, then hand a typed plan to pure C#.
/// </summary>
public sealed class Investigation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid IncidentId { get; set; }

    public Incident? Incident { get; set; }

    /// <summary>
    /// The W3C trace id of the <c>hephaisto.investigation</c> span. This is the join key
    /// between the database and Tempo: an investigation row links straight to its trace.
    /// </summary>
    public string? TraceId { get; set; }

    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Who ran the model loop (v0.12.0 F5): one of <see cref="Investigations.InvestigationExecutors"/>.
    /// Null on every row written before it existed, which reads as in-process - it was.
    /// </summary>
    public string? Executor { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public TerminationReason TerminationReason { get; set; }

    public int StepsUsed { get; set; }

    public int ToolCallsUsed { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }

    /// <summary>Model's own confidence in the winning hypothesis, 0..1. Advisory only.</summary>
    public double? Confidence { get; set; }

    /// <summary>Set when the loop threw. Recorded rather than retried blindly.</summary>
    public string? Error { get; set; }

    public List<InvestigationStep> Steps { get; set; } = [];

    public List<Finding> Findings { get; set; } = [];

    public ActionPlan? Plan { get; set; }
}

/// <summary>
/// One LLM turn or one tool call. The full ordered list is what the Blazor UI renders as
/// "here is exactly what it did", and what the eval harness replays.
/// </summary>
public sealed class InvestigationStep
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid InvestigationId { get; set; }

    public Investigation? Investigation { get; set; }

    public int Ordinal { get; set; }

    public StepKind Kind { get; set; }

    /// <summary>Tool name for <see cref="StepKind.ToolCall"/>, null for an LLM turn.</summary>
    public string? ToolName { get; set; }

    /// <summary>Which server answered: <c>kubernetes</c>, <c>grafana-mcp</c>, or <c>internal</c>.</summary>
    public string? ToolServer { get; set; }

    /// <summary>Arguments as JSON, after redaction.</summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// The digested result - what the model actually saw. Grounding checks
    /// <see cref="Evidence.Excerpt"/> against THIS, not against the raw blob, because
    /// the model cannot cite text it was never shown.
    /// </summary>
    public string? ResultDigest { get; set; }

    /// <summary>Pointer into <c>evidence_blobs</c> holding the untruncated result.</summary>
    public Guid? RawBlobId { get; set; }

    public bool ResultTruncated { get; set; }

    public int ResultBytes { get; set; }

    public long DurationMs { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }

    public bool Failed { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>A hypothesis about what is wrong, with the evidence that supports it.</summary>
public sealed class Finding
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid InvestigationId { get; set; }

    public Investigation? Investigation { get; set; }

    /// <summary>Coarse bucket, e.g. <c>resource-limit</c>, <c>dependency</c>, <c>config</c>, <c>image</c>.</summary>
    public string Category { get; set; } = string.Empty;

    public string Hypothesis { get; set; } = string.Empty;

    public double Confidence { get; set; }

    /// <summary>The one the plan is built on. Exactly zero or one per investigation.</summary>
    public bool IsPrimary { get; set; }

    public List<Evidence> Evidence { get; set; } = [];

    /// <summary>
    /// Where in the running revision's source this points (v0.12.0 F5, an investigator with source
    /// access). Never evidence: grounding reads <see cref="Evidence"/> only. Each reference was
    /// confirmed to exist in the checkout by the runner before it was reported.
    /// </summary>
    public List<CodeRef> CodeRefs { get; set; } = [];
}

/// <summary>A file and line in a repository at a commit. Pointing, not proving.</summary>
public sealed record CodeRef
{
    public string Repository { get; init; } = string.Empty;

    /// <summary>The commit the investigator read, or null when only a branch was.</summary>
    public string? Ref { get; init; }

    public string Path { get; init; } = string.Empty;

    public int Line { get; init; }

    public int? EndLine { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// A citation. The grounding invariant lives here: <see cref="Excerpt"/> must appear
/// verbatim (modulo whitespace) in the <see cref="InvestigationStep.ResultDigest"/> of
/// <see cref="StepId"/>, and that step must belong to the same investigation.
/// </summary>
/// <remarks>
/// This is checked at runtime by <c>GroundingVerifier</c>, not asked for in the prompt.
/// A model that invents a plausible-sounding log line it never actually saw produces
/// evidence that fails the substring check, and the finding is dropped rather than shown
/// to a human as fact.
/// </remarks>
public sealed class Evidence
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid FindingId { get; set; }

    public Finding? Finding { get; set; }

    /// <summary>The step whose result this quotes. Must be in the same investigation.</summary>
    public Guid StepId { get; set; }

    public string Excerpt { get; set; } = string.Empty;

    /// <summary>
    /// Clickable provenance: <c>evidence://step/{stepId}</c> for a stored blob, or a
    /// Grafana deeplink for a PromQL/LogQL result.
    /// </summary>
    public string? SourceUri { get; set; }
}

/// <summary>
/// The untruncated result of a tool call. Digest for the model, raw for the audit: these
/// are ~1 MB each and expire at 30 days, while incident digests are kept indefinitely.
/// </summary>
public sealed class EvidenceBlob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid InvestigationId { get; set; }

    public string ContentType { get; set; } = "text/plain";

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
