namespace Hephaisto.Core.Domain;

/// <summary>
/// A problem, as opposed to an observation of one. Many <see cref="Signal"/>s collapse into
/// one incident, which is the unit a human reads, an investigation runs against and an
/// action is attributed to.
/// </summary>
public sealed class Incident
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Groups incidents that are facets of one underlying problem. Distinct from
    /// <see cref="Signal.Fingerprint"/>: fingerprint dedups identical signals, the
    /// correlation key merges an OOMKill incident and a latency incident on one workload.
    /// </summary>
    public string CorrelationKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public SignalKind Kind { get; set; }

    public Severity Severity { get; set; }

    public IncidentState State { get; set; } = IncidentState.Detected;

    public SuppressionReason SuppressionReason { get; set; }

    public EscalationReason EscalationReason { get; set; }

    public TargetRef Target { get; set; } = new();

    /// <summary>The mode the agent was in when this incident opened. Recorded so the audit
    /// trail explains why nothing was done without needing to know the config at the time.</summary>
    public AgentMode Mode { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset LastSignalAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// Set by the oscillation detector. While in the future, no action may be taken on this
    /// workload and incidents escalate straight to a human.
    /// </summary>
    public DateTimeOffset? QuarantinedUntil { get; set; }

    /// <summary>Free-text resolution note, written by the verifier or a human.</summary>
    public string? Resolution { get; set; }

    /// <summary>Who closed it, and when. Set only on the way into <see cref="IncidentState.Closed"/>.</summary>
    /// <remarks>
    /// Separate from <see cref="Resolution"/> on purpose: that field describes a fix the agent
    /// verified, and a closure is frequently the opposite - "this was never our problem". The
    /// closer's REASON lives on the <see cref="IncidentEvent"/> for the transition, where every
    /// other transition reason already lives, rather than in a second free-text column here.
    /// </remarks>
    public string? ClosedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Who has picked this up, and when. Deliberately NOT a state.
    /// </summary>
    /// <remarks>
    /// Acknowledging is "I have seen this and I am on it", which is orthogonal to where the
    /// incident is in its lifecycle - an acknowledged incident is still Investigating, or still
    /// Escalated. Modelling it as a state would force a choice between the two facts and lose
    /// one. It is the first thing an on-call engineer needs and the cheapest thing to offer.
    /// </remarks>
    public string? AcknowledgedBy { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    public List<Signal> Signals { get; set; } = [];

    public List<Investigation> Investigations { get; set; } = [];

    public List<AgentAction> Actions { get; set; } = [];

    public List<IncidentEvent> Events { get; set; } = [];

    /// <summary>
    /// Is this still live? <see cref="IncidentState.Escalated"/> counts as open, deliberately:
    /// the agent has given up but the cluster problem has not gone away, so it still belongs on
    /// somebody's list.
    /// </summary>
    /// <remarks>
    /// That is exactly why <see cref="IncidentState.Closed"/> had to exist. Before it, an
    /// escalated incident had no terminal exit that meant "handled" - only
    /// <c>Reinvestigate</c>, which spends tokens on another attempt - so on an Observe install,
    /// where every incident escalates, this property was true forever and the open count only
    /// ever climbed.
    /// </remarks>
    public bool IsOpen => State is not (
        IncidentState.Resolved
        or IncidentState.Expired
        or IncidentState.Suppressed
        or IncidentState.Closed);
}

/// <summary>
/// One row per state transition. <see cref="Incident.State"/> is the column you query;
/// this is the log you audit. Both exist deliberately - a single mutable column cannot
/// answer "how long was this awaiting approval".
/// </summary>
public sealed class IncidentEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid IncidentId { get; set; }

    public Incident? Incident { get; set; }

    public IncidentState? From { get; set; }

    public IncidentState To { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public string? TraceId { get; set; }
}
