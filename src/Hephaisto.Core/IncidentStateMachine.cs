using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Core;

/// <summary>
/// Thrown when a caller attempts an edge that is not in the diagram. It is an exception and
/// not a bool return because there is no sensible way for a caller to carry on: a component
/// that thinks an incident is Acting when it is actually Resolved has a bug, and silently
/// swallowing that produces an audit trail that does not match reality.
/// </summary>
public sealed class InvalidStateTransitionException(IncidentState from, IncidentState to, string? detail = null)
    : InvalidOperationException(
        detail is null
            ? $"Illegal incident transition {from} -> {to}."
            : $"Illegal incident transition {from} -> {to}: {detail}")
{
    public IncidentState From { get; } = from;

    public IncidentState To { get; } = to;
}

/// <summary>
/// The only thing permitted to write <see cref="Incident.State"/>. One public method per
/// legal edge, so an illegal transition is not merely rejected at runtime - most of them
/// cannot be expressed at all, because no method spells them.
/// </summary>
/// <remarks>
/// Every method appends an <see cref="IncidentEvent"/> and returns it. The event log is not
/// a nicety: <see cref="Incident.State"/> is a single mutable column and therefore cannot
/// answer "how long did this sit awaiting approval", which is the number that decides whether
/// the approval flow is usable at all.
/// </remarks>
public sealed class IncidentStateMachine(IClock clock)
{
    /// <summary>
    /// Reserved actor names that may never grant a resolution. See <see cref="Resolve"/>.
    /// </summary>
    public const string ModelActor = "hephaisto/model";

    public const string VerifierActor = "hephaisto/verifier";

    public const string SystemActor = "hephaisto/system";

    /// <summary>
    /// The actor for an action the policy engine admitted under L3, with no human involved.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT in <see cref="ForbiddenGranters"/>. Policy admitting a low-risk action
    /// is the agent's own decision, made by rules rather than by the model, and it is exactly
    /// what L3 means; laundering a model's opinion into a human approval is the thing that list
    /// prevents, and this is not that.
    /// </remarks>
    public const string AutoActor = "hephaisto/auto";

    private static readonly string[] ForbiddenGranters = [ModelActor, "model", "llm", "gemini", "hephaisto/llm"];

    /// <summary>
    /// Whether an actor name is a model identity, and therefore may not grant anything.
    /// </summary>
    /// <remarks>
    /// Exposed because approving an action asks the same question one step earlier than
    /// resolving an incident does, and the two must not be able to disagree about who counts
    /// as a model. Free text either way - this is attribution, not authentication - but the
    /// obvious way to launder a model decision into a human one should not be the easy one.
    /// </remarks>
    public static bool IsForbiddenGranter(string? actor) =>
        actor is not null && ForbiddenGranters.Contains(actor.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Detected -&gt; Triaging. Dedup, suppression and scope checks happen inside triage.</summary>
    public IncidentEvent Triage(Incident incident, string reason) =>
        Transition(incident, [IncidentState.Detected], IncidentState.Triaging, reason);

    /// <summary>
    /// Triaging -&gt; Suppressed. A terminal state, and by far the most common outcome:
    /// most signals are duplicates of something already open.
    /// </summary>
    public IncidentEvent Suppress(Incident incident, SuppressionReason reason, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var evt = Transition(
            incident,
            [IncidentState.Triaging],
            IncidentState.Suppressed,
            detail is null ? reason.ToString() : $"{reason}: {detail}");

        incident.SuppressionReason = reason;
        return evt;
    }

    /// <summary>Triaging -&gt; Investigating. The only door into the expensive path.</summary>
    public IncidentEvent BeginInvestigation(Incident incident, string reason) =>
        Transition(incident, [IncidentState.Triaging], IncidentState.Investigating, reason);

    /// <summary>Investigating -&gt; AwaitingApproval, once a plan exists and policy asked for a human.</summary>
    public IncidentEvent AwaitApproval(Incident incident, string reason) =>
        Transition(incident, [IncidentState.Investigating], IncidentState.AwaitingApproval, reason);

    /// <summary>
    /// Investigating | AwaitingApproval -&gt; Acting. Reachable straight from Investigating
    /// only because policy returned Allow; the executor never chooses this for itself.
    /// </summary>
    public IncidentEvent BeginActing(Incident incident, string reason) =>
        Transition(
            incident,
            [IncidentState.Investigating, IncidentState.AwaitingApproval],
            IncidentState.Acting,
            reason);

    /// <summary>Acting -&gt; Verifying. Entered the moment the last action returns, not when it succeeds.</summary>
    public IncidentEvent BeginVerifying(Incident incident, string reason) =>
        Transition(incident, [IncidentState.Acting], IncidentState.Verifying, reason);

    /// <summary>
    /// Any open state -&gt; Resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant this method exists to enforce: <b>the LLM may propose Resolved, but only
    /// verification grants it.</b> A model asked "is this fixed?" will say yes - it has every
    /// incentive to and no way to check. So resolution is not a conclusion the planner can
    /// reach; it is a grant, and the grant needs a named granter that is deterministic C#
    /// (<see cref="VerifierActor"/>) or a human. Passing a model identity here throws rather
    /// than being quietly recorded, because an incident closed on the model's own say-so is
    /// indistinguishable in the database from one that was actually fixed.
    /// </para>
    /// </remarks>
    public IncidentEvent Resolve(Incident incident, string reason, string grantedBy)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantedBy);

        if (ForbiddenGranters.Contains(grantedBy.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{grantedBy}' may not grant a resolution: the model proposes, verification grants.",
                nameof(grantedBy));
        }

        var evt = Transition(incident, OpenStates, IncidentState.Resolved, $"{reason} (granted by {grantedBy})");

        incident.ResolvedAt = clock.UtcNow;
        incident.Resolution = reason;
        return evt;
    }

    /// <summary>Any open state -&gt; Escalated. The universal give-up edge; always available.</summary>
    public IncidentEvent Escalate(Incident incident, EscalationReason reason, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var evt = Transition(
            incident,
            OpenStates,
            IncidentState.Escalated,
            detail is null ? reason.ToString() : $"{reason}: {detail}");

        incident.EscalationReason = reason;
        return evt;
    }

    /// <summary>
    /// Any open state, including <see cref="IncidentState.Escalated"/> -&gt; Expired. For
    /// incidents nobody ever answered: the signal stopped arriving and no human touched it.
    /// Distinct from Resolved on purpose - "it went away" is not "it was fixed", and conflating
    /// them inflates the agent's own success metric.
    /// </summary>
    /// <remarks>
    /// <b>Escalated is a legal predecessor, and it has to be.</b> Escalated is precisely the
    /// state "nobody answered" describes: the agent gave up and asked for a human, and the
    /// question is whether one came. It is also the state an Observe install leaves every
    /// incident in, so a sweeper that could not expire it could not drain anything. Distinct from
    /// <see cref="Close"/>, which asserts a person DID deal with it - this edge asserts the
    /// opposite, and the two must not be the same row.
    /// </remarks>
    public IncidentEvent Expire(Incident incident, string reason) =>
        Transition(incident, [.. OpenStates, IncidentState.Escalated], IncidentState.Expired, reason);

    /// <summary>
    /// Resolved -&gt; Investigating. The signal came back, so the fix did not hold. Reopening
    /// rather than opening a fresh incident is what lets the oscillation detector see that
    /// the same action has now failed three times on the same workload.
    /// </summary>
    public IncidentEvent Reopen(Incident incident, string reason)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var evt = Transition(incident, [IncidentState.Resolved], IncidentState.Investigating, reason);

        incident.ResolvedAt = null;
        incident.Resolution = null;
        return evt;
    }

    /// <summary>
    /// Any open state, including <see cref="IncidentState.Escalated"/> -&gt;
    /// <see cref="IncidentState.Closed"/>. A human is done with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This edge is why the state exists.</b> Before it, the only terminal exits were
    /// <see cref="Resolve"/> - which only verification may grant, after an action the agent took
    /// worked - and <see cref="Expire"/>, which had no caller at all. On an Observe install the
    /// agent never acts, so no incident could reach Resolved, every incident escalated, and
    /// <see cref="Incident.IsOpen"/> stayed true for all of them permanently. The open count on
    /// the status page climbed for as long as the process ran and nothing could bring it down.
    /// </para>
    /// <para>
    /// <b>Escalated is deliberately a legal predecessor</b>, and it is not in
    /// <see cref="OpenStates"/>, so this edge lists its predecessors itself rather than reusing
    /// that array. Escalated is the state almost every closure will start from: the agent gave
    /// up, a person picked it up, and the person is now finished.
    /// </para>
    /// <para>
    /// <b>Not reachable from Resolved, Expired or Suppressed.</b> Those are already terminal and
    /// already mean something specific; letting a human close them would overwrite a fact the
    /// agent established with a weaker one, and re-closing a closed incident is a no-op worth
    /// rejecting loudly rather than recording twice.
    /// </para>
    /// <para>
    /// <b>Never the model</b>, for the same reason <see cref="Resolve"/> refuses it: an incident
    /// closed on the model's own say-so is indistinguishable in the database from one a person
    /// dealt with, and closing is now the cheapest way to make an incident disappear.
    /// </para>
    /// </remarks>
    public IncidentEvent Close(Incident incident, string reason, string closedBy)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(closedBy);

        if (ForbiddenGranters.Contains(closedBy.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{closedBy}' may not close an incident: closing is a human judgement that "
                + "this no longer needs attention.",
                nameof(closedBy));
        }

        var evt = Transition(
            incident,
            [.. OpenStates, IncidentState.Escalated],
            IncidentState.Closed,
            $"{reason} (closed by {closedBy})");

        incident.ClosedAt = clock.UtcNow;
        incident.ClosedBy = closedBy;
        return evt;
    }

    /// <summary>
    /// Records that a person has picked an incident up. <b>Not a transition</b> - it returns no
    /// <see cref="IncidentEvent"/> and leaves <see cref="Incident.State"/> alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acknowledging and closing are different acts and an on-call engineer needs the first one
    /// first: "I have seen this, stop paging the others" long before "this is dealt with".
    /// Modelling it as a state would force a choice between where the incident is in its
    /// lifecycle and whether somebody is on it, and lose whichever one lost.
    /// </para>
    /// <para>
    /// It lives here anyway, rather than being a bare property set by a controller, because this
    /// class is the only thing permitted to write an incident's lifecycle fields and the
    /// model-actor refusal has to apply identically. The audit trail for it is an
    /// <c>incident.acknowledged</c> audit event written by the caller - not an
    /// <see cref="IncidentEvent"/>, whose From/To shape would have to lie.
    /// </para>
    /// <para>
    /// Idempotent in effect but not silent: acknowledging an already-acknowledged incident
    /// overwrites the holder, which is what handover looks like.
    /// </para>
    /// </remarks>
    public void Acknowledge(Incident incident, string actor)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (ForbiddenGranters.Contains(actor.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{actor}' may not acknowledge an incident: the point of an acknowledgement is "
                + "that a person is looking at it.",
                nameof(actor));
        }

        if (!incident.IsOpen)
        {
            throw new InvalidOperationException(
                $"incident {incident.Id} is {incident.State} and needs nobody to pick it up.");
        }

        incident.AcknowledgedBy = actor;
        incident.AcknowledgedAt = clock.UtcNow;
    }

    /// <summary>
    /// Escalated | Expired | Closed -&gt; Investigating. A human asking for another attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the single human door back into the lifecycle</b>, which is why
    /// <see cref="IncidentState.Closed"/> is on it rather than on <see cref="Reopen"/>: a person
    /// who closed an incident and then wants another look is asking for exactly what this method
    /// already does - a named requester, the kill switch consulted, the work queued, the token
    /// spend attributed. A second door would duplicate all four and be the one that drifts.
    /// </para>
    /// <para>
    /// Distinct from <see cref="Reopen"/>, which is for an incident that was genuinely fixed
    /// and came back - a recurrence the agent notices, not a human changing their mind. That
    /// edge still has no producer; see backlog #109 for the dedup question behind it. This one is for an incident that was never diagnosed: the provider
    /// returned an overload, the step budget ran out mid-thought, the model stalled. The
    /// cluster problem is unchanged and untouched - only our attempt to explain it failed -
    /// so there is nothing to un-resolve and no oscillation to record. Collapsing the two
    /// would make "the fix did not hold" and "we never produced a fix" the same row.
    /// </para>
    /// <para>
    /// <b>A named requester, and never the model.</b> Each attempt spends real tokens on the
    /// most expensive path in the system, so an unattributed retry is an unattributed
    /// invoice. Refusing the model identities is what stops a future auto-retry from being
    /// wired straight to this edge and quietly looping an incident until a budget stops it -
    /// if retry ever becomes automatic it must arrive with its own explicit cap, under its
    /// own name, as a deliberate change rather than by reusing the human door.
    /// </para>
    /// <para>
    /// Clears <see cref="Incident.EscalationReason"/>, mirroring the way <see cref="Reopen"/>
    /// clears the resolution. A retried incident that still reads BudgetExhausted describes an
    /// attempt that is no longer the current one.
    /// </para>
    /// </remarks>
    public IncidentEvent Reinvestigate(Incident incident, string reason, string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

        if (ForbiddenGranters.Contains(requestedBy.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{requestedBy}' may not request a re-investigation: a retry spends real "
                + "tokens and needs a human to answer for it.",
                nameof(requestedBy));
        }

        var evt = Transition(
            incident,
            [IncidentState.Escalated, IncidentState.Expired, IncidentState.Closed],
            IncidentState.Investigating,
            $"{reason} (requested by {requestedBy})");

        incident.EscalationReason = EscalationReason.None;

        // A reopened incident is not closed any more, and leaving the closer's name on it would
        // credit them with a decision that has since been undone.
        incident.ClosedAt = null;
        incident.ClosedBy = null;
        return evt;
    }

    /// <summary>
    /// The legal-predecessor set for the edges that leave the live part of the lifecycle.
    /// </summary>
    /// <remarks>
    /// <b>It does not mirror <see cref="Incident.IsOpen"/>, and the comment here used to claim
    /// it did.</b> <c>IsOpen</c> counts <see cref="IncidentState.Escalated"/> as open - the
    /// agent gave up but the problem did not go away - whereas this array excludes it, because
    /// an escalated incident cannot escalate again, cannot be resolved by a verifier that never
    /// acted, and cannot expire from a state a human is already looking at. The edges that DO
    /// accept Escalated say so themselves: <see cref="Close"/> and <see cref="Reinvestigate"/>.
    /// <c>HephaistoDbContext.OpenStates</c> is a third list, matching <c>IsOpen</c> rather than
    /// this one, because a computed property cannot be translated into SQL.
    /// Three lists, two meanings, all three pinned by <c>IncidentStateDefinitionsAgreeTests</c>.
    /// </remarks>
    private static readonly IncidentState[] OpenStates =
    [
        IncidentState.Detected,
        IncidentState.Triaging,
        IncidentState.Investigating,
        IncidentState.AwaitingApproval,
        IncidentState.Acting,
        IncidentState.Verifying,
    ];

    private IncidentEvent Transition(
        Incident incident,
        IncidentState[] legalPredecessors,
        IncidentState to,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var from = incident.State;
        if (!legalPredecessors.Contains(from))
        {
            throw new InvalidStateTransitionException(
                from,
                to,
                $"legal predecessors are {string.Join(", ", legalPredecessors)}");
        }

        var evt = new IncidentEvent
        {
            IncidentId = incident.Id,
            From = from,
            To = to,
            Reason = reason,
            At = clock.UtcNow,
        };

        incident.State = to;
        incident.Events.Add(evt);
        return evt;
    }
}
