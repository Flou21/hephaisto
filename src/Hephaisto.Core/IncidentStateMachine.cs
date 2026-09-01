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
    /// Any open state -&gt; Expired. For incidents nobody ever answered: the signal stopped
    /// arriving and no human touched it. Distinct from Resolved on purpose - "it went away"
    /// is not "it was fixed", and conflating them inflates the agent's own success metric.
    /// </summary>
    public IncidentEvent Expire(Incident incident, string reason) =>
        Transition(incident, OpenStates, IncidentState.Expired, reason);

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
    /// Escalated | Expired -&gt; Investigating. A human asking for another attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="Reopen"/>, which is for an incident that was genuinely fixed
    /// and came back. This one is for an incident that was never diagnosed: the provider
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
            [IncidentState.Escalated, IncidentState.Expired],
            IncidentState.Investigating,
            $"{reason} (requested by {requestedBy})");

        incident.EscalationReason = EscalationReason.None;
        return evt;
    }

    /// <summary>
    /// Mirrors <see cref="Incident.IsOpen"/>. Kept as an explicit array rather than computed
    /// from the property so that the legal-predecessor set of every edge reads the same way.
    /// </summary>
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
