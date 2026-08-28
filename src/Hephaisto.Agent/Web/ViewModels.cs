using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Web;

// One set of shapes for the JSON API and the Blazor pages, not two. The pages are the
// primary consumer and the API is the same data for a script or a curl; giving them
// separate models is how the two drift until the page shows a field the API forgot.
//
// None of these use `required`. They are built inside EF projections and inside mapping
// loops, and a projection is an expression tree the compiler cannot see through to prove a
// required member was set.

/// <summary>Flattened <see cref="TargetRef"/>. <see cref="TargetRef.WorkloadKey"/> is a
/// computed property with no column, so it is recomputed here rather than selected.</summary>
public sealed record TargetView
{
    public string Namespace { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? OwnerKind { get; init; }

    public string? OwnerName { get; init; }

    public string? NodeName { get; init; }

    public string WorkloadKey =>
        OwnerKind is { Length: > 0 } ok && OwnerName is { Length: > 0 } on
            ? $"{Namespace}/{ok}/{on}"
            : $"{Namespace}/{Kind}/{Name}";

    public override string ToString() => $"{Namespace}/{Kind}/{Name}";

    public static TargetView From(TargetRef t) => new()
    {
        Namespace = t.Namespace,
        Kind = t.Kind,
        Name = t.Name,
        OwnerKind = t.OwnerKind,
        OwnerName = t.OwnerName,
        NodeName = t.NodeName,
    };
}

/// <summary>One row of the incident list.</summary>
public sealed record IncidentListItem
{
    public Guid Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public SignalKind Kind { get; init; }

    public Severity Severity { get; init; }

    public IncidentState State { get; init; }

    public SuppressionReason SuppressionReason { get; init; }

    public EscalationReason EscalationReason { get; init; }

    public string Namespace { get; init; } = string.Empty;

    public string TargetKind { get; init; } = string.Empty;

    public string TargetName { get; init; } = string.Empty;

    public string? OwnerKind { get; init; }

    public string? OwnerName { get; init; }

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset LastSignalAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public int SignalCount { get; init; }

    public int InvestigationCount { get; init; }

    /// <summary>
    /// The column that matters most in observe mode. An incident with investigations but no
    /// findings is a different thing from one that was never investigated, and both are
    /// different from one that concluded - so this is "did it reach a conclusion", not
    /// "did it try".
    /// </summary>
    public bool HasDiagnosis { get; init; }

    /// <summary>
    /// Live progress, when a worker is running this incident right now. Null when it is not.
    /// </summary>
    /// <remarks>
    /// Not derived from <see cref="State"/>, because it cannot be.
    /// <c>IncidentState.Investigating</c> is written during triage, before the incident is
    /// even queued, and the investigation row is written only when the run finishes - so the
    /// stored state cannot distinguish "a worker has this" from "this is waiting behind two
    /// others". With worker concurrency of two, most incidents showing Investigating are
    /// waiting, and a console that draws them identically looks like an idle agent while it
    /// is busy and spending money.
    /// </remarks>
    public InvestigationProgressView? InProgress { get; init; }

    public string Workload =>
        OwnerKind is { Length: > 0 } ok && OwnerName is { Length: > 0 } on
            ? $"{ok}/{on}"
            : $"{TargetKind}/{TargetName}";

    /// <summary>Open incidents run to now; closed ones stop at the resolution.</summary>
    public TimeSpan DurationAt(DateTimeOffset now) => (ResolvedAt ?? now) - OpenedAt;
}

public sealed record SignalView
{
    public Guid Id { get; init; }

    public SignalSource Source { get; init; }

    public SignalKind Kind { get; init; }

    public Severity Severity { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; init; }

    public int Count { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    public TargetView Target { get; init; } = new();
}

public sealed record TransitionView
{
    public Guid Id { get; init; }

    public IncidentState? From { get; init; }

    public IncidentState To { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset At { get; init; }

    public string? TraceId { get; init; }
}

/// <summary>One row of the investigation trace. Every cost and truncation field is carried
/// through because hiding them is how a trace stops being auditable.</summary>
public sealed record StepView
{
    public Guid Id { get; init; }

    public int Ordinal { get; init; }

    public StepKind Kind { get; init; }

    public string? ToolName { get; init; }

    public string? ToolServer { get; init; }

    public string? Arguments { get; init; }

    /// <summary>What the model actually saw. The grounding check runs against this string,
    /// not against the raw blob, so this is the one a human must be able to read.</summary>
    public string? ResultDigest { get; init; }

    public Guid? RawBlobId { get; init; }

    public bool ResultTruncated { get; init; }

    public int ResultBytes { get; init; }

    public long DurationMs { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public decimal CostUsd { get; init; }

    public bool Failed { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset At { get; init; }

    public string Label => Kind == StepKind.ToolCall
        ? ToolName ?? "tool"
        : "llm turn";
}

public sealed record EvidenceView
{
    public Guid Id { get; init; }

    public Guid StepId { get; init; }

    /// <summary>
    /// Resolved from <see cref="StepId"/> at read time. Null means the cited step is not in
    /// this investigation's step list, which should be impossible - the grounding verifier
    /// rejects such evidence - so the UI renders it as a broken citation rather than hiding
    /// it. A citation that cannot be followed is the failure this whole invariant exists to
    /// make visible.
    /// </summary>
    public int? StepOrdinal { get; init; }

    public string Excerpt { get; init; } = string.Empty;

    public string? SourceUri { get; init; }
}

public sealed record FindingView
{
    public Guid Id { get; init; }

    public string Category { get; init; } = string.Empty;

    public string Hypothesis { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public bool IsPrimary { get; init; }

    public IReadOnlyList<EvidenceView> Evidence { get; init; } = [];
}

public sealed record ActionView
{
    public Guid Id { get; init; }

    public ActionType Type { get; init; }

    public TargetView Target { get; init; } = new();

    public string? Arguments { get; init; }

    public RiskTier Risk { get; init; }

    public ActionState State { get; init; }

    public string? PredictedEffect { get; init; }

    public string? RollbackSpec { get; init; }

    public PolicyDecision Decision { get; init; }

    public IReadOnlyList<string> DecisionReasons { get; init; } = [];

    public bool DryRun { get; init; }

    public AgentMode ModeAtExecution { get; init; }

    public string? ApprovedBy { get; init; }

    public ApprovalSource ApprovalSource { get; init; }

    public DateTimeOffset? ExecutedAt { get; init; }

    public string? Outcome { get; init; }

    public string? Error { get; init; }
}

public sealed record PlanView
{
    public Guid Id { get; init; }

    public string Summary { get; init; } = string.Empty;

    public bool NoActionRequired { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<ActionView> Actions { get; init; } = [];
}

public sealed record InvestigationView
{
    public Guid Id { get; init; }

    /// <summary>The join key into Tempo. Rendered as text, not a link: the Grafana base URL
    /// is deployment configuration this layer has no business inventing.</summary>
    public string? TraceId { get; init; }

    public string ModelId { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TerminationReason TerminationReason { get; init; }

    public int StepsUsed { get; init; }

    public int ToolCallsUsed { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public decimal CostUsd { get; init; }

    public double? Confidence { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<StepView> Steps { get; init; } = [];

    public IReadOnlyList<FindingView> Findings { get; init; } = [];

    public PlanView? Plan { get; init; }

    public FindingView? PrimaryFinding => Findings.FirstOrDefault(f => f.IsPrimary);
}

/// <summary>The untruncated result behind a step, when it still exists.</summary>
/// <remarks>
/// <see cref="InvestigationStep.RawBlobId"/> is deliberately not a foreign key: blobs expire
/// at 30 days while the step log is kept, so the pointer is allowed to dangle. A miss here
/// therefore means "expired", not "bug", and the UI has to say which - a raw expander that
/// silently shows nothing looks like the evidence was never captured.
/// </remarks>
public sealed record EvidenceBlobView
{
    public Guid Id { get; init; }

    public string ContentType { get; init; } = "text/plain";

    public string Content { get; init; } = string.Empty;

    public int TotalBytes { get; init; }

    /// <summary>True when <see cref="Content"/> is a prefix. Blobs run to about a megabyte
    /// and pushing one through a SignalR circuit to be laid out as DOM stalls the tab.</summary>
    public bool Clipped { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record FeedbackView
{
    public Guid Id { get; init; }

    public bool Helpful { get; init; }

    public bool? RootCauseCorrect { get; init; }

    public bool FalsePositive { get; init; }

    public string? Comment { get; init; }

    public string SubmittedBy { get; init; } = string.Empty;

    public DateTimeOffset At { get; init; }
}

public sealed record IncidentDetailView
{
    /// <summary>
    /// Live progress when a worker is investigating this incident right now, else null.
    /// </summary>
    /// <remarks>
    /// The detail page is where a reader waits while an investigation runs, and until this
    /// existed it was the least informative place to do that: the investigations list stays
    /// empty for the whole run, because the row is written only at the end.
    /// </remarks>
    public InvestigationProgressView? InProgress { get; init; }

    public Guid Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string CorrelationKey { get; init; } = string.Empty;

    public SignalKind Kind { get; init; }

    public Severity Severity { get; init; }

    public IncidentState State { get; init; }

    public SuppressionReason SuppressionReason { get; init; }

    public EscalationReason EscalationReason { get; init; }

    public TargetView Target { get; init; } = new();

    /// <summary>The mode at the moment the incident opened, not the mode now. In observe
    /// mode this is why the plan section shows an empty execution column.</summary>
    public AgentMode ModeAtOpen { get; init; }

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset LastSignalAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public DateTimeOffset? QuarantinedUntil { get; init; }

    public string? Resolution { get; init; }

    public IReadOnlyList<SignalView> Signals { get; init; } = [];

    public IReadOnlyList<TransitionView> Transitions { get; init; } = [];

    public IReadOnlyList<InvestigationView> Investigations { get; init; } = [];

    public IReadOnlyList<ActionView> Actions { get; init; } = [];

    public IReadOnlyList<FeedbackView> Feedback { get; init; } = [];

    public TimeSpan DurationAt(DateTimeOffset now) => (ResolvedAt ?? now) - OpenedAt;
}

/// <summary>What <c>/api/status</c> and the status page both render.</summary>
/// <summary>What a running investigation is doing, sampled at request time.</summary>
public sealed record InvestigationProgressView
{
    public string Model { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public int Steps { get; init; }

    public int ToolCalls { get; init; }

    public decimal CostUsd { get; init; }

    /// <summary>The last tool it called, which is the most legible "what is it doing".</summary>
    public string? Activity { get; init; }

    /// <summary>
    /// The steps taken so far, in order. Populated on the detail view only.
    /// </summary>
    /// <remarks>
    /// Left empty by the list projection on purpose. A digest runs to a few kilobytes, and
    /// attaching every step of every running investigation to a page that renders fifty rows
    /// would push a lot of text over the circuit for something no one is reading at that
    /// zoom level. The counters are what the list needs; the log is what the detail page is.
    /// </remarks>
    public IReadOnlyList<StepView> StepLog { get; init; } = [];
}

public sealed record AgentStatusView
{
    /// <summary>Investigations a worker is running right now, with their live progress.</summary>
    /// <remarks>
    /// The answer to "is it doing anything". Neither <c>openIncidents</c> nor the incident
    /// states can give it: both count things that have been queued, not things being worked
    /// on, and the difference is invisible in the database.
    /// </remarks>
    public IReadOnlyList<InvestigationProgressView> RunningInvestigations { get; init; } = [];

    /// <summary>Incidents waiting for a worker slot. Worker concurrency is 2.</summary>
    public int QueuedInvestigations { get; init; }

    /// <summary>The mode CONFIGURED in the database row - what a human last asked for.</summary>
    public AgentMode Mode { get; init; }

    /// <summary>
    /// The mode actually in force: the most restrictive of the env var, the switch ConfigMap
    /// and the database row.
    /// </summary>
    /// <remarks>
    /// Shown next to <see cref="Mode"/> rather than instead of it, because the gap between
    /// the two is the interesting part. "Configured Auto, running Observe" is the state an
    /// operator most needs to see and the one a single mode field hides - it is what you get
    /// after someone hits the ConfigMap and before anyone updates the row.
    /// </remarks>
    public AgentMode EffectiveMode { get; init; }

    /// <summary>Which arm bound <see cref="EffectiveMode"/> - the one a human has to change.</summary>
    public string ModeDecidedBy { get; init; } = "default";

    /// <summary>Every arm, rendered, so the status page can show why without a debugger.</summary>
    public IReadOnlyList<string> ModeArms { get; init; } = [];

    /// <summary>True when some arm is holding the agent below what another arm asked for.</summary>
    public bool ModeConstrained { get; init; }

    /// <summary>True means the mode column is a lie: the agent is running as
    /// <see cref="AgentMode.Observe"/> regardless, until a human re-arms it.</summary>
    public bool RunawayLatched { get; init; }

    public string? LatchReason { get; init; }

    public DateTimeOffset? LatchedAt { get; init; }

    public string? ModeChangedBy { get; init; }

    public DateTimeOffset ModeChangedAt { get; init; }

    public int OpenIncidents { get; init; }

    public int EscalatedIncidents { get; init; }

    // Deliberately not clamped to 1. A window sitting at 1.4 is a different fact from one
    // sitting at 1.0, and clamping it in the view is how that difference disappears.
    public double HourlyTokenUtilization { get; init; }

    public double HourlyCostUtilization { get; init; }

    public double DailyCostUtilization { get; init; }

    public double WarnAtUtilization { get; init; }

    public DateTimeOffset? WatchdogLastSeenAt { get; init; }

    public bool WatchdogStale { get; init; }

    public long WatchdogReceipts { get; init; }

    public DateTimeOffset Now { get; init; }

    /// <summary>The running build, so the console itself says which version drew it.</summary>
    /// <remarks>
    /// Duplicated from <c>/api/version</c> on purpose. Someone reading the status page during
    /// an incident should not have to open a second endpoint to answer "is this the build we
    /// rolled out?", and the two cannot disagree - both read the same assembly attribute.
    /// </remarks>
    public string Version { get; init; } = "unknown";

    public string Commit { get; init; } = "unknown";
}
