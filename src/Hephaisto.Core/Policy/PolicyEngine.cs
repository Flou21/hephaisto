using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Core.Policy;

/// <summary>
/// The last thing between a plan and the cluster.
/// </summary>
/// <remarks>
/// <para>
/// Pure, static and deterministic: same request plus same facts always yields the same verdict,
/// with no clock read, no cluster call and no model in the loop. That is what makes the safety
/// argument testable - every rule below is reachable from a unit test in microseconds, and the
/// test suite rather than a staging cluster is where the guarantees are demonstrated.
/// </para>
/// <para>
/// Default-deny throughout. <see cref="PolicyDecision.Deny"/> is the zero value of the enum, an
/// empty <see cref="PolicyOptions"/> permits nothing, and <see cref="PolicyDecision.Allow"/> is
/// only ever reached by falling off the end of every gate below. A rule added in the middle can
/// therefore make the engine stricter by accident but never looser.
/// </para>
/// <para>
/// Ordering is load-bearing. Hard denials come first and are accumulated rather than
/// short-circuited, because "why was this refused" is asked once, after the fact, and one
/// reason out of four is a misleading answer. Downgrades come last, after allow-eligibility
/// has been established, so a downgrade always has something to downgrade from.
/// </para>
/// </remarks>
public static class PolicyEngine
{
    /// <summary>
    /// Action types that never reach the cluster whatever the configuration says. They exist
    /// in <see cref="ActionType"/> only so a plan naming one deserialises, gets recorded and
    /// gets refused with a reason, instead of failing to parse and looking like a bug.
    /// </summary>
    private static readonly ActionType[] NeverApprovable = [ActionType.DeletePvc, ActionType.DeleteWorkload];

    /// <summary>Cheap, reversible, single-object actions. Everything else needs a human by default.</summary>
    /// <remarks>
    /// <b><see cref="ActionType.SilenceAlert"/> was here and has been removed.</b> It is cheap,
    /// reversible and single-object - it satisfies every word of the description - and it is
    /// still the wrong thing to let an agent do unattended, because its entire effect is to
    /// stop a human being told. Every other action on this list fails visibly when it is wrong;
    /// this one fails by making the cluster look quiet. It now has its own case below, which
    /// always requires approval.
    /// </remarks>
    private static readonly ActionType[] LowRisk =
    [
        ActionType.RestartPod,
        ActionType.DeleteStuckJob,
        ActionType.DeleteFailedJobPods,
    ];

    /// <summary>
    /// Action types with no inverse operation, because the controller already restores the
    /// state they disturb. Exempt from the rollback-spec requirement at gate 14.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deleting a pod cannot be undone; it is <i>reconciled</i>. The ReplicaSet notices the
    /// missing replica and creates one, which is the entire mechanism by which "restart a pod"
    /// works - there is no RESTART verb in the Kubernetes API. So there is no prior state to
    /// return to, and demanding a rollback spec here asks the model for a fiction.
    /// </para>
    /// <para>
    /// That matters because gate 14 would otherwise make <see cref="ActionType.RestartPod"/> -
    /// the most common and most often correct remediation there is - permanently
    /// <see cref="PolicyDecision.RequireApproval"/>, and whether autonomy worked at all would
    /// depend on how the model happened to word a JSON field. Either it invents a plausible
    /// rollback and satisfies a safety gate with nothing behind it, or it follows the prompt's
    /// own instruction to say plainly that an action cannot be undone and is downgraded
    /// forever. Both are worse than saying this out loud.
    /// </para>
    /// <para>
    /// <b>The recourse on a failed verification is escalation, not rollback.</b> Gate 14 exists
    /// so a failed check never leaves the cluster in a state nobody chose; for these types the
    /// state after a failure is the state the controller chose, which is the same state it
    /// would have converged on without us. Keep this set to actions where that is literally
    /// true - if an action leaves anything behind that a controller will not reconcile, it
    /// needs a rollback spec and does not belong here.
    /// </para>
    /// </remarks>
    private static readonly ActionType[] SelfHealing = [ActionType.RestartPod, ActionType.DeleteFailedJobPods];

    /// <summary>A rollout restart above this many pods stops being a nudge and becomes a deploy.</summary>
    private const int MaxUnattendedRolloutRestartPods = 10;

    public static PolicyResult Evaluate(ActionRequest request, ClusterFacts facts, PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);

        var denials = new List<string>();

        // Carried beside each human sentence rather than parsed back out of it. See
        // PolicyReasonCode for why deriving them from the prose would be brittle in exactly the
        // wrong place.
        var codes = new List<PolicyReasonCode>();

        void Deny(PolicyReasonCode code, string reason)
        {
            codes.Add(code);
            denials.Add(reason);
        }

        var target = request.Target;
        var ns = target.Namespace;
        var workload = facts.Workload;

        // 1. Never approvable. Checked before anything else, including IsRollback: there is no
        //    signal, no evidence and no human who can make deleting a PVC the agent's business,
        //    and a protected namespace is where the agent and its own observability live - an
        //    action there can blind the agent to the outage it just caused.
        if (NeverApprovable.Contains(request.Type))
        {
            Deny(PolicyReasonCode.NeverApprovable,
                $"action type {request.Type} is permanently denied and can never be approved");
        }

        if (options.ProtectedNamespaces.Contains(ns))
        {
            Deny(PolicyReasonCode.ProtectedNamespace,
                $"namespace '{ns}' is protected; no action of any kind is permitted there");
        }

        // 2. Allowlist, not denylist: a denylist fails open for every namespace created after
        //    it was written, and namespaces get created without telling anyone.
        if (!options.AllowedNamespaces.Contains(ns))
        {
            Deny(PolicyReasonCode.NamespaceNotAllowed, $"namespace '{ns}' is not in the allowed namespaces");
        }

        //    ...and the namespace must say so itself. Two authorities, deliberately: the
        //    allowlist above is the operator's, this label is the namespace owner's. Both
        //    have to agree. The manifests have called this "a second, independent
        //    confirmation" since before any code read it - it is read now.
        if (options.RequiredNamespaceLabel is { Length: > 0 } required &&
            !(facts.NamespaceLabels.TryGetValue(required, out var optIn) &&
              string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase)))
        {
            Deny(PolicyReasonCode.NamespaceLabelMissing, $"namespace '{ns}' does not carry {required}=true");
        }

        // 3. Per-object opt-out. A team that labels its workload has said no, and that beats
        //    a cluster-wide allowlist that a platform engineer set months ago.
        foreach (var (key, value) in options.ProtectedLabels)
        {
            if (facts.TargetLabels.TryGetValue(key, out var actual) &&
                string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
            {
                Deny(PolicyReasonCode.ProtectedLabel, $"target carries protected label {key}={value}");
            }
        }

        // 4. The kill switch. Observe is called out separately because it is the MVP's normal
        //    operating mode, and "denied because the agent is off" would read as a fault.
        switch (facts.Mode)
        {
            case AgentMode.Off:
                Deny(PolicyReasonCode.AgentOff, "agent is off");
                break;
            case AgentMode.Observe:
                Deny(PolicyReasonCode.ObserveMode, "agent is in observe mode");
                break;
        }

        // 5. Quarantine, set by the oscillation detector. The agent has already demonstrated
        //    on this workload that its fix does not hold; doing it a fourth time is not a plan.
        if (facts.QuarantinedUntil is { } quarantinedUntil && quarantinedUntil > facts.Now)
        {
            Deny(PolicyReasonCode.Quarantined, $"workload is quarantined until {quarantinedUntil:O}");
        }

        // 6. Grounding. An action with no surviving finding behind it is an action the model
        //    wanted for reasons that failed the substring check - i.e. reasons it invented.
        //    Rollbacks are exempt because undoing does not need a fresh diagnosis.
        if (request.GroundedFindingIds.Count == 0 && !request.IsRollback)
        {
            Deny(PolicyReasonCode.Ungrounded, "no grounded finding justifies this action");
        }

        // 7. Stability. Each of these is a case where the cluster is already changing and the
        //    agent's change would be attributed to, or would fight, someone else's.
        if (workload?.RolloutInFlight == true)
        {
            Deny(PolicyReasonCode.RolloutInFlight, "a rollout is in flight; do not fight a human deploy");
        }

        if (workload?.YoungestPodAge is { } youngest && youngest < options.MinPodAgeBeforeAction)
        {
            Deny(
                PolicyReasonCode.PodTooYoung,
                $"youngest pod is {youngest.TotalSeconds:0}s old, below the " +
                $"{options.MinPodAgeBeforeAction.TotalSeconds:0}s minimum; it has not had a fair chance to become healthy");
        }

        if (facts.InMaintenanceWindow)
        {
            Deny(PolicyReasonCode.MaintenanceWindow, "a maintenance window is in progress");
        }

        if (facts.ClusterUnhealthyFraction > options.ClusterUnhealthyCeiling)
        {
            Deny(
                PolicyReasonCode.ClusterWideEvent,
                $"{facts.ClusterUnhealthyFraction:P0} of the cluster is unhealthy: " +
                "cluster-wide event, not a pod-level problem");
        }

        // 8. Blast radius. The absolute cap bounds the damage of a mistake; the fractional cap
        //    stops the agent taking out a majority of a small workload in one move.
        if (request.AffectedPodCount > options.MaxPodsPerAction)
        {
            Deny(
                PolicyReasonCode.BlastRadiusPods,
                $"blast radius {request.AffectedPodCount} pods exceeds the maximum of {options.MaxPodsPerAction}");
        }

        // Skipped for a single-replica workload: 1-of-1 is always 100%, so the fraction gate
        // would swallow the last-replica rule below and its deliberate escape hatch with it.
        if (workload is { DesiredReplicas: > 1 } sized)
        {
            var fraction = (double)request.AffectedPodCount / sized.DesiredReplicas;
            if (fraction > options.MaxWorkloadFraction)
            {
                Deny(
                    PolicyReasonCode.BlastRadiusFraction,
                    $"blast radius {fraction:P0} of the workload exceeds the maximum of {options.MaxWorkloadFraction:P0}");
            }
        }

        // 9. Not the last one standing. Restarting the sole Ready replica converts a degraded
        //    service into a down one, which is strictly worse than the symptom being treated.
        //    The label is the opt-in for workloads whose owners have decided otherwise.
        //
        //    THERE HAS TO BE A READY REPLICA TO LOSE. This gate used to fire at zero ready as
        //    well, and the denial it produced said so in as many words - "this would restart
        //    the last Ready replica (0 ready of 3 desired)" - which is not a sentence that can
        //    be true. A workload with nothing Ready is already down: a restart cannot degrade
        //    it, and is the only thing that might help.
        //
        //    Not a corner case. A crash-looping pod is BY DEFINITION not Ready, so at zero the
        //    gate refused every restart the action exists for, and RestartPod - the one type
        //    v0.2.0 promotes to auto - could never fire on the fault it is meant for. Found by
        //    running the acceptance test: the agent proposed exactly the right action for
        //    c11-transient and was refused for protecting a replica that was not there.
        if (request.Type is ActionType.RestartPod &&
            workload is { ReadyReplicas: >= 1 } only &&
            (only.DesiredReplicas == 1 || only.ReadyReplicas <= 1) &&
            !HasSingleReplicaEscapeHatch(facts, options))
        {
            Deny(
                PolicyReasonCode.LastReadyReplica,
                $"this would restart the last Ready replica of {only.Kind} {only.Key} " +
                $"({only.ReadyReplicas} ready of {only.DesiredReplicas} desired); " +
                $"set label {options.AllowSingleReplicaRestartLabel}=true to opt in");
        }

        // 10. Cooldown. Independent of the budget: the budget bounds how much happens, the
        //     cooldown bounds how fast, so a workload always gets time to settle in between.
        if (facts.LastActionOnWorkloadAt is { } lastAction && facts.Now - lastAction < options.WorkloadCooldown)
        {
            var elapsed = facts.Now - lastAction;
            Deny(
                PolicyReasonCode.WorkloadCooldown,
                $"workload cooldown active: last action {elapsed.TotalMinutes:0.#} min ago, " +
                $"cooldown is {options.WorkloadCooldown.TotalMinutes:0.#} min");
        }

        if (denials.Count > 0)
        {
            return PolicyResult.Deny([.. denials]) with { Codes = [.. codes] };
        }

        // 11. Risk routing. Only from here on can the answer be anything other than Deny.
        var (allowEligible, routingReason) = Route(request, workload, options);
        if (routingReason is null)
        {
            return PolicyResult.Deny($"action type {request.Type} has no routing rule and is therefore denied")
                with { Codes = [PolicyReasonCode.NoRoutingRule] };
        }

        var reasons = new List<string> { routingReason };

        if (!allowEligible)
        {
            // Reached directly, not downgraded: DowngradedFrom stays null so the audit trail
            // distinguishes "this always needed a human" from "this would have been automatic".
            return PolicyResult.Approval([.. reasons])
                with { Codes = [PolicyReasonCode.NotAllowEligible] };
        }

        var downgrades = new List<string>();
        var downgradeCodes = new List<PolicyReasonCode>();

        void Downgrade(PolicyReasonCode code, string reason)
        {
            downgradeCodes.Add(code);
            downgrades.Add(reason);
        }

        // 12. Autonomy gate. Allow-eligible is a property of the action; Allow is a property of
        //     the operator's configuration. Auto-enabled types are promoted one at a time after
        //     watching that type require no human correction, so eligibility alone is not enough.
        if (facts.Mode is not AgentMode.Auto)
        {
            Downgrade(
                PolicyReasonCode.NotAutoMode,
                $"agent is in {facts.Mode} mode, so an allow-eligible action still needs a human");
        }
        else if (!options.AutoEnabledActionTypes.Contains(request.Type))
        {
            Downgrade(
                PolicyReasonCode.TypeNotAutoEnabled,
                $"action type {request.Type} is not in the auto-enabled set");
        }

        // 13. Budgets downgrade, never deny. An exhausted budget means the agent should stop
        //     acting on its own - it does not mean the on-call engineer looking at the incident
        //     should be blocked from clicking approve. Hard-denying here would turn a rate limit
        //     into an outage extension.
        //
        //     A rollback bypasses budgets entirely, and only budgets: you must always be able to
        //     undo. Being at the cap is exactly the situation in which the agent has done several
        //     things and one of them needs taking back.
        if (!request.IsRollback)
        {
            var budget = ActionBudget.Evaluate(facts, options);
            if (budget.IsExceeded)
            {
                Downgrade(PolicyReasonCode.BudgetExhausted, budget.Reason);
            }
        }
        else
        {
            reasons.Add("rollback: budget checks bypassed so an action can always be undone");
        }

        // 14. No rollback spec, no automatic execution. Verification runs minutes after the fact;
        //     a failed verification with no way back leaves the cluster in a state nobody chose.
        //
        //     Except where there is nothing to go back TO. A deleted pod is not undone, it is
        //     reconciled - the controller recreates it, which is the whole mechanism of a
        //     restart. See SelfHealing for why that exemption is named rather than left to
        //     whether the model wrote something in the rollback field.
        if (!request.HasRollbackSpec && !SelfHealing.Contains(request.Type))
        {
            Downgrade(
                PolicyReasonCode.NoRollbackSpec,
                "action has no rollback spec, so a failed verification would have no recourse");
        }

        if (downgrades.Count > 0)
        {
            reasons.AddRange(downgrades);
            return new PolicyResult
            {
                Decision = PolicyDecision.RequireApproval,
                Reasons = [.. reasons],
                Codes = [.. downgradeCodes],
                DowngradedFrom = PolicyDecision.Allow,
            };
        }

        return PolicyResult.Allow([.. reasons]);
    }

    /// <summary>
    /// Maps an action type to whether it could run unattended at all, given the facts. Returns
    /// a null reason for a type with no rule, which the caller turns into a denial - a new
    /// <see cref="ActionType"/> is thereby denied until someone writes its rule, rather than
    /// inheriting whatever the nearest switch arm happened to do.
    /// </summary>
    private static (bool AllowEligible, string? Reason) Route(
        ActionRequest request,
        WorkloadFacts? workload,
        PolicyOptions options)
    {
        if (LowRisk.Contains(request.Type))
        {
            return (true, $"{request.Type} is a low-risk, single-object action");
        }

        switch (request.Type)
        {
            case ActionType.RolloutRestart:
                return request.AffectedPodCount <= MaxUnattendedRolloutRestartPods
                    ? (true, $"rollout restart of {request.AffectedPodCount} pods is within the unattended limit")
                    : (false, $"rollout restart of {request.AffectedPodCount} pods exceeds " +
                              $"the unattended limit of {MaxUnattendedRolloutRestartPods}");

            case ActionType.RollbackDeployment:
                // A rollback is only obvious when the current revision is minutes old and the
                // previous one had a track record. Outside that window "roll back" is a guess
                // about which of many changes broke things, and guesses want a human.
                var fresh = workload?.CurrentRevisionAge is { } age && age < options.RollbackFreshRevisionWindow;
                var previousProven = workload?.PreviousRevisionHealthyFor is { } healthy &&
                                     healthy >= options.RollbackPreviousHealthyMinimum;

                if (fresh && previousProven)
                {
                    return (true, "rollback of a fresh revision to a previously healthy one");
                }

                return (false, fresh
                    ? "rollback target revision has no proven healthy history"
                    : "current revision is not a fresh deploy, so a rollback is a guess rather than an undo");

            case ActionType.ScaleWorkload:
                // Scaling is cheap to do and expensive to be wrong about - it moves cost and it
                // masks the actual fault. A human decides whether more replicas is the answer.
                return (false, "scaling changes capacity and cost, so it requires approval");

            case ActionType.PatchResources:
                return (false, "patching resources mutates the workload spec, so it requires approval");

            case ActionType.SilenceAlert:
                // Never allow-eligible, whatever AutoEnabledActionTypes says. Silencing is the
                // one action whose failure mode is that everything looks fine: a wrong restart
                // shows up as a pod still crash-looping, and a wrong silence shows up as
                // nothing at all, for as long as it lasts. The agent may propose one - it is
                // often the right call during a known-noisy deploy - and a person decides.
                return (false, "silencing an alert stops a human being told, so it always requires approval");

            case ActionType.CordonNode:
                return (false, "cordoning a node affects scheduling cluster-wide, so it requires approval");

            case ActionType.DrainNode:
                // A second, distinct approver is enforced by the approval layer above this one.
                // Policy cannot see who approved, so it cannot check that here; all it can do is
                // guarantee the action never reaches the executor without going through approval.
                return (false, "draining a node evicts every pod on it and requires approval (and a second approver)");

            default:
                return (false, null);
        }
    }

    private static bool HasSingleReplicaEscapeHatch(ClusterFacts facts, PolicyOptions options) =>
        facts.TargetLabels.TryGetValue(options.AllowSingleReplicaRestartLabel, out var optIn) &&
        string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase);
}
