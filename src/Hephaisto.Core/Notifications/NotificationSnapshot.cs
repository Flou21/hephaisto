using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Notifications;

/// <summary>
/// The facts as they were at the instant the event happened, frozen.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a snapshot and not a pointer, and that is the whole design.</b> The in-process
/// <c>IncidentLiveEvent</c> deliberately carries only an id, because a Blazor circuit re-reads
/// from Postgres and wants the newest truth. A delivery wants the opposite. An outbox row can
/// sit for twenty minutes behind a failing endpoint, and re-reading the incident at send time
/// would produce a card announcing an escalation that says <c>Resolved</c> - reporting the
/// present while claiming to report the past.
/// </para>
/// <para>
/// It also means a template fix applies to rows already queued, which rendering at enqueue time
/// would not.
/// </para>
/// <para>
/// Deep links are NOT here. They are derived at render from the configured base URL, so a
/// misconfigured URL can be corrected without re-queuing every pending delivery. The identity a
/// link is built from - the incident id - is a fact and is here.
/// </para>
/// </remarks>
public sealed record NotificationSnapshot
{
    public required NotificationEvent Event { get; init; }

    /// <summary>Null for the two events that are about the agent rather than an incident.</summary>
    public Guid? IncidentId { get; init; }

    /// <summary>
    /// Groups facets of one underlying problem, and is what the outbound cooldown is keyed on.
    /// Deliberately the coarse workload-shaped key rather than the signal fingerprint: a
    /// CrashLoopBackOff gets a new pod name every couple of minutes, and keying a cooldown on
    /// the pod would defeat it exactly when it is needed.
    /// </summary>
    public string CorrelationKey { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public SignalKind Kind { get; init; }

    public Severity Severity { get; init; }

    /// <summary>The state arrived at. Null for non-incident events.</summary>
    public IncidentState? State { get; init; }

    /// <summary>The state left. Null on the first transition, and for non-incident events.</summary>
    public IncidentState? PreviousState { get; init; }

    public EscalationReason EscalationReason { get; init; }

    /// <summary>
    /// Kept as its own field rather than parsed back out of <see cref="Target"/>, because it is
    /// what routing filters on and a routing decision must not depend on string splitting.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Human-readable <c>namespace/kind/name</c>, or empty when there is no target.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>The primary hypothesis, when the investigation produced one.</summary>
    public string? Summary { get; init; }

    /// <summary>The transition's own reason text, which is prose written for a person.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset At { get; init; }
}

/// <summary>
/// What a channel renders: the frozen facts, plus the things that are only known once the
/// dispatcher picks the row up.
/// </summary>
public sealed record NotificationMessage
{
    public required NotificationSnapshot Snapshot { get; init; }

    /// <summary>
    /// The outbox row's id, stable across every retry. It goes on the wire so a receiver can
    /// dedupe: at-least-once delivery means a duplicate is normal, not a bug, and a receiver
    /// with no key to dedupe on has no way to tell the difference.
    /// </summary>
    public required Guid DeliveryId { get; init; }

    /// <summary>Absolute link into this Hephaisto's own incident view. Null when unconfigured.</summary>
    public string? IncidentUrl { get; init; }

    /// <summary>Absolute link into Grafana, scoped to the incident's window. Null when unconfigured.</summary>
    public string? GrafanaUrl { get; init; }

    /// <summary>
    /// How many further deliveries the cooldown swallowed since the last one that went out. It
    /// rides on the next message that does go out, so a suppressed burst is visible where a
    /// human is already looking rather than only in a metric they would have to go and find.
    /// </summary>
    public int AlsoSuppressed { get; init; }
}
