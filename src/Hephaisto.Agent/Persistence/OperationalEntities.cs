using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Persistence;

// These five types are infrastructure, not domain. They live here rather than in
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

    /// <summary>
    /// Set when the spend was a coder Job's rather than an investigation's. Coder cost enters the
    /// same rolling windows as every other LLM call, so a runaway coder trips the global budget
    /// like a runaway investigation would.
    /// </summary>
    public Guid? CodeFixAttemptId { get; set; }

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

    /// <summary>
    /// While in the future, nothing may be done to this workload by anyone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the oscillation detector, and deliberately here rather than on the incident.
    /// The detector's finding is about a WORKLOAD - "restarting this has not helped three
    /// times" - and incidents are per-fingerprint and keep being opened afresh, so a
    /// quarantine recorded on one of them expires the moment the next one appears, which is
    /// exactly when the loop would otherwise continue.
    /// </para>
    /// <para>
    /// This row is already taken with a lock at the top of every admission transaction, so
    /// the check costs nothing extra and cannot be raced by a concurrent admission.
    /// </para>
    /// </remarks>
    public DateTimeOffset? QuarantinedUntil { get; set; }

    public string? QuarantineReason { get; set; }
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

/// <summary>
/// One outbound message, one channel, and everything needed to send it again later.
/// </summary>
/// <remarks>
/// <para>
/// <b>This table is the reason the milestone exists.</b> <c>IIncidentNotifier</c> is an
/// in-process fan-out that drops on overflow by design; it is a fine way to nudge a browser tab
/// and a catastrophic way to tell somebody the agent has given up. A row here is written in the
/// SAME transaction as the state change that caused it, so an incident cannot reach
/// <c>Escalated</c> without a delivery existing to carry that fact outward, and a pod restart
/// between the two is not a thing that can happen.
/// </para>
/// <para>
/// One row per (event, channel) rather than one per event: a Teams outage must not hold up the
/// webhook, and the attempt count and backoff are per-channel facts.
/// </para>
/// <para>
/// <see cref="Snapshot"/> is frozen at enqueue. Re-reading the incident at send time would make
/// a retry describe a LATER state than the event it reports - an escalation card that has
/// quietly become a resolution card - which is the one thing a delivery must never do.
/// </para>
/// </remarks>
public sealed class NotificationDelivery
{
    /// <summary>
    /// Stable across every retry, and put on the wire so a receiver can dedupe. At-least-once
    /// delivery makes a duplicate normal rather than a bug, and a receiver with no key to
    /// dedupe on cannot tell the difference.
    /// </summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public NotificationEvent Event { get; set; }

    /// <summary>Null for the two events that are about the agent rather than an incident.</summary>
    public Guid? IncidentId { get; set; }

    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Denormalised out of the snapshot because the outbound cooldown queries it, and a
    /// cooldown that had to deserialise every candidate row to find its key would be a
    /// sequential scan on the delivery path.
    /// </summary>
    public string CorrelationKey { get; set; } = string.Empty;

    public DeliveryStatus Status { get; set; }

    /// <summary>The facts as they were. See the remarks on this class.</summary>
    public NotificationSnapshot Snapshot { get; set; } = new() { Event = NotificationEvent.Unspecified };

    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the dispatcher should next pick this up. Set to the creation time on enqueue, so
    /// "due now" and "due after a backoff" are one query rather than two.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// Why the last attempt did not work, in the words the endpoint used. Truncated on the way
    /// in - a stack trace or an HTML error page in a column that a UI renders is how an
    /// operator ends up scrolling past the thing they needed to read.
    /// </summary>
    public string? LastError { get; set; }
}

public enum TeamsBotMessageKind
{
    /// <summary>Takes the zero value so a default-constructed row cannot claim to be a board.</summary>
    Unspecified = 0,

    /// <summary>The one message in the channel that lists every open incident.</summary>
    Board = 1,

    /// <summary>One incident, for one person, in their personal chat with the bot.</summary>
    Alert = 2,
}

public enum TeamsBotMessageState
{
    Unspecified = 0,

    /// <summary>Still compared with what it shows, and edited when that differs.</summary>
    Live = 1,

    /// <summary>A newer alert for the same incident was posted; this one is to be shrunk.</summary>
    Superseded = 2,

    /// <summary>Said its last word. Never edited again.</summary>
    Final = 3,
}

/// <summary>
/// A message the bot posted to Teams, and everything needed to edit it later.
/// </summary>
/// <remarks>
/// <para>
/// Editing is the whole point of the bot, and editing needs the id Teams gave the message when it
/// was posted. That id exists nowhere else, so losing it turns a living card into a permanent one
/// that nothing can correct - which is why it is a row and not a field in memory.
/// </para>
/// <para>
/// <b>There is no "deleted" state</b>, because nothing is ever deleted: Teams leaves a
/// "This message has been deleted." line behind. A message that is over is <see
/// cref="TeamsBotMessageState.Final"/>, still there, saying how it ended.
/// </para>
/// <para>
/// No foreign key to incidents, matching <see cref="NotificationDelivery"/>: this is a record of
/// what was said to somebody.
/// </para>
/// </remarks>
public sealed class TeamsBotMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public TeamsBotMessageKind Kind { get; set; }

    public TeamsBotMessageState State { get; set; }

    /// <summary>Null for the board, and for an alert about the agent rather than an incident.</summary>
    public Guid? IncidentId { get; set; }

    /// <summary>
    /// For an alert, the address it went to. For the board, the channel it is in - so pointing
    /// the configuration at another channel starts a new board rather than editing the old one.
    /// </summary>
    public string Recipient { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;

    public string ActivityId { get; set; } = string.Empty;

    /// <summary>What was last sent, so an unchanged card is not sent again.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// The outbox row that caused an alert. With <see cref="Recipient"/> it is what makes a
    /// retried delivery skip the people who were already told.
    /// </summary>
    public Guid? DeliveryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Why the last edit did not work, in the words Teams used. Truncated on the way in.</summary>
    public string? LastError { get; set; }
}
