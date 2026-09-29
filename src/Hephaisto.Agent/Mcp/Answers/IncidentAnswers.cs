using Hephaisto.Agent.Persistence;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Mcp.Answers;

// What the incident tools answer. Every string here is McpText - enveloped, or a name that looks
// like one - unless it is [ServerAuthored]: an id, a cursor, a hint Hephaisto wrote. McpAnswer's
// resolver refuses a type in this namespace that breaks the rule, so a new field somebody forgot
// to wrap fails its call instead of reaching a model as the server's own words.

/// <summary>One incident as a list shows it.</summary>
public sealed record IncidentRow
{
    public required Guid Id { get; init; }

    /// <summary>The first eight hex digits. Shared by every incident opened in the same ~65 seconds.</summary>
    [ServerAuthored]
    public required string ShortId { get; init; }

    public required McpText Title { get; init; }

    public required Severity Severity { get; init; }

    public required IncidentState State { get; init; }

    public required SignalKind Kind { get; init; }

    public McpText? AlertName { get; init; }

    public McpText? Cluster { get; init; }

    public required McpText Namespace { get; init; }

    public required McpText Workload { get; init; }

    public McpText? AssignedTo { get; init; }

    public McpText? AcknowledgedBy { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>When it resolved, closed or expired. Absent while it is open.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    [ServerAuthored]
    public string? Url { get; init; }
}

public sealed record IncidentPage
{
    public required IReadOnlyList<IncidentRow> Incidents { get; init; }

    /// <summary>Pass back as cursor for the next page; absent on the last one.</summary>
    [ServerAuthored]
    public string? NextCursor { get; init; }

    [ServerAuthored]
    public string? Note { get; init; }
}

public sealed record IncidentCount
{
    public required int Total { get; init; }

    [ServerAuthored]
    public required string GroupBy { get; init; }

    public required IReadOnlyList<IncidentCountGroup> Groups { get; init; }

    [ServerAuthored]
    public string? Note { get; init; }
}

public sealed record IncidentCountGroup
{
    public required McpText Key { get; init; }

    public required int Count { get; init; }

    public required int Open { get; init; }

    /// <summary>The newest incidents of the group.</summary>
    public required IReadOnlyList<IncidentRow> Examples { get; init; }
}

/// <summary>One incident, to read first.</summary>
public sealed record IncidentOverview
{
    public required IncidentDetail Incident { get; init; }

    /// <summary>The tools that go further, with the arguments for this incident.</summary>
    [ServerAuthored]
    public required IReadOnlyList<string> Next { get; init; }
}

public sealed record IncidentDetail
{
    public required Guid Id { get; init; }

    [ServerAuthored]
    public required string ShortId { get; init; }

    public required McpText Title { get; init; }

    public required Severity Severity { get; init; }

    public required IncidentState State { get; init; }

    public required SignalKind Kind { get; init; }

    public EscalationReason? EscalationReason { get; init; }

    public McpText? AlertName { get; init; }

    public McpText? Cluster { get; init; }

    public required McpText Namespace { get; init; }

    public required McpText Workload { get; init; }

    public McpText? WorkloadKind { get; init; }

    public McpText? Owner { get; init; }

    public required McpMap Labels { get; init; }

    /// <summary>The alert's own description or summary, or the signal's message.</summary>
    public McpText? Description { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public required DateTimeOffset LastSignalAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public DateTimeOffset? ReopenedAt { get; init; }

    public McpText? AcknowledgedBy { get; init; }

    public DateTimeOffset? AcknowledgedAt { get; init; }

    public McpText? AssignedTo { get; init; }

    public McpText? AssignedBy { get; init; }

    public McpText? ClosedBy { get; init; }

    public McpText? Resolution { get; init; }

    public required SignalSummary Signals { get; init; }

    public InvestigationSummary? Investigation { get; init; }

    public FindingSummary? PrimaryFinding { get; init; }

    public required IReadOnlyList<ActionSummary> Actions { get; init; }

    public required IReadOnlyList<CodeFixSummary> CodeFixes { get; init; }

    public required NotificationSummary Notifications { get; init; }

    [ServerAuthored]
    public string? Url { get; init; }
}

public sealed record SignalSummary
{
    public required int Count { get; init; }

    public required int Firing { get; init; }

    public required int Resolved { get; init; }
}

public sealed record InvestigationSummary
{
    public required Guid Id { get; init; }

    public required int Investigations { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public required int Steps { get; init; }

    public required decimal CostUsd { get; init; }

    public McpText? Summary { get; init; }

    public bool Running { get; init; }
}

public sealed record FindingSummary
{
    public required McpText Hypothesis { get; init; }

    public required McpText Category { get; init; }

    public required double Confidence { get; init; }

    public required int Evidence { get; init; }
}

public sealed record ActionSummary
{
    public required Guid Id { get; init; }

    public required ActionType Type { get; init; }

    public required McpText Target { get; init; }

    public required ActionState State { get; init; }

    public required PolicyDecision Decision { get; init; }

    public required RiskTier Risk { get; init; }
}

public sealed record CodeFixSummary
{
    public required Guid Id { get; init; }

    public required CodeFixState State { get; init; }

    public McpText? PullRequestUrl { get; init; }
}

public sealed record NotificationSummary
{
    public required int Delivered { get; init; }

    public required int Pending { get; init; }

    public required int Failed { get; init; }

    /// <summary>Held back on purpose: a cooldown, a cap, a route that does not want this event.</summary>
    public required int Suppressed { get; init; }
}

public sealed record SignalRow
{
    public required Guid Id { get; init; }

    public required McpText Name { get; init; }

    public required SignalSource Source { get; init; }

    public required SignalStatus Status { get; init; }

    public required Severity Severity { get; init; }

    public required DateTimeOffset FirstSeen { get; init; }

    public required DateTimeOffset LastSeen { get; init; }

    public required int Count { get; init; }

    public McpText? Message { get; init; }

    public required McpMap Labels { get; init; }

    public required McpMap Annotations { get; init; }
}

public sealed record SignalPage
{
    public required IReadOnlyList<SignalRow> Signals { get; init; }

    [ServerAuthored]
    public string? NextCursor { get; init; }
}

public sealed record TimelineEntry
{
    public required DateTimeOffset At { get; init; }

    /// <summary><c>transition</c> or <c>audit</c>.</summary>
    [ServerAuthored]
    public required string Kind { get; init; }

    public IncidentState? From { get; init; }

    public IncidentState? To { get; init; }

    /// <summary>For an audit entry, what was done: <c>incident.acknowledged</c>, <c>action.approved</c>.</summary>
    [ServerAuthored]
    public string? Action { get; init; }

    public McpText? Actor { get; init; }

    public McpText? Reason { get; init; }

    public AuditOriginView? Origin { get; init; }
}

/// <summary>Where a change came from, when not the console: an agent through MCP.</summary>
public sealed record AuditOriginView
{
    [ServerAuthored]
    public required string Source { get; init; }

    public McpText? Token { get; init; }

    public McpText? TokenKind { get; init; }

    public McpText? Role { get; init; }

    public McpText? Client { get; init; }

    public McpText? ClaimedBy { get; init; }

    public bool ClaimVerified { get; init; }
}

public sealed record TimelinePage
{
    public required IReadOnlyList<TimelineEntry> Entries { get; init; }

    [ServerAuthored]
    public string? NextCursor { get; init; }
}

public sealed record NotificationRow
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required NotificationEvent Event { get; init; }

    public required McpText Channel { get; init; }

    public required IReadOnlyList<McpText> Recipients { get; init; }

    public required IReadOnlyList<McpText> Routes { get; init; }

    public int? Step { get; init; }

    public required DeliveryStatus Status { get; init; }

    public required int Attempts { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public McpText? Error { get; init; }
}

public sealed record NotificationPage
{
    public required IReadOnlyList<NotificationRow> Notifications { get; init; }

    [ServerAuthored]
    public string? NextCursor { get; init; }
}

public sealed record FilterValue
{
    public required McpText Value { get; init; }

    public required int Open { get; init; }

    public required int Total { get; init; }
}

public sealed record FilterValues
{
    public IReadOnlyList<FilterValue>? Namespaces { get; init; }

    public IReadOnlyList<FilterValue>? AlertNames { get; init; }

    public IReadOnlyList<FilterValue>? Clusters { get; init; }

    public IReadOnlyList<FilterValue>? Workloads { get; init; }

    public IReadOnlyList<FilterValue>? Assignees { get; init; }

    public IReadOnlyList<FilterValue>? Kinds { get; init; }

    public IReadOnlyList<FilterValue>? Repositories { get; init; }
}
