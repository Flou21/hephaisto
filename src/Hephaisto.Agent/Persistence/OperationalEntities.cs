using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence;

// These four types are infrastructure, not domain. They live here rather than in
// Hephaisto.Core because nothing in Core reasons about them: they exist only because the
// safety properties Core describes as pure functions have to survive a process restart,
// and a row in Postgres is the only thing here that does.

/// <summary>
/// One LLM call's cost, as a row rather than a counter. An in-memory counter resets on
/// every crash, which is exactly the moment a runaway loop is most likely - the loop that
/// burned the budget is also the one that killed the pod.
/// </summary>
/// <remarks>
/// Written in the same transaction as the <see cref="InvestigationStep"/> that consumed the
/// tokens, so accounting can never drift from the step log it is supposed to describe.
/// </remarks>
public sealed class LlmUsageRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid IncidentId { get; set; }

    public Guid? InvestigationId { get; set; }

    public DateTimeOffset At { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }
}

/// <summary>
/// One row per hour in which a budget ceiling was hit. Deduplicated on
/// (<see cref="HourBucket"/>, <see cref="Kind"/>) by a unique index, because a runaway loop
/// hits the cap thousands of times an hour and three *hours* is the signal, not three calls.
/// </summary>
public sealed class LlmBudgetBreach
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset HourBucket { get; set; }

    public string Kind { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public string? Detail { get; set; }
}

/// <summary>
/// A row to take a lock on, one per workload. Holds no state worth reading: its entire
/// purpose is to give <c>ActionRepository.TryAdmitActionAsync</c> something to serialise
/// concurrent admissions for the same workload against.
/// </summary>
public sealed class WorkloadActionLock
{
    public string WorkloadKey { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The database arm of the kill switch, and the latch the runaway backstop trips.
/// </summary>
/// <remarks>
/// One row, id <c>singleton</c>. The mode also comes from an env var and a ConfigMap and the
/// most restrictive of the three wins; this arm exists because it is the only one a human
/// can flip without a deploy, and the only one that can be read inside the same transaction
/// that admits an action. Reading it there is the point - a kill switch consulted before the
/// transaction is a kill switch with a race in it.
/// </remarks>
public sealed class AgentModeRow
{
    public const string SingletonId = "singleton";

    public string Id { get; set; } = SingletonId;

    /// <summary>
    /// Vestigial. Nothing writes it and no arm reads it.
    /// </summary>
    /// <remarks>
    /// The mode is decided by the Helm values and arrives as the env var and the ConfigMap.
    /// This column outlived that decision: the migration seeds it to <c>Observe</c>, so while
    /// it WAS read as an arm every database in existence silently pinned the agent to Observe
    /// and no chart value could lift it. It is left in place only so dropping it can happen in
    /// one migration alongside the other v0.2.0 schema changes.
    /// </remarks>
    public AgentMode Mode { get; set; } = AgentMode.Observe;

    /// <summary>
    /// Set by the runaway backstop. While true the agent must run as
    /// <see cref="AgentMode.Observe"/> whatever <see cref="Mode"/> says, and only a human
    /// re-arm clears it: an automatic reset would let the same loop trip it again forever.
    /// </summary>
    public bool RunawayLatched { get; set; }

    public string? LatchReason { get; set; }

    public DateTimeOffset? LatchedAt { get; set; }

    public string? ChangedBy { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}
