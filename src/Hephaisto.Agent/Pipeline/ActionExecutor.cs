using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Telemetry;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Phase 3 of the loop: turning an admitted <see cref="AgentAction"/> into an API call.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model never reaches this code.</b> It emits a plan against a JSON schema with no
/// tools available; the plan is judged by <see cref="PolicyEngine"/>, admitted by
/// <see cref="IActionRepository.TryAdmitActionAsync"/>, and only then does a
/// <c>switch</c> over a closed <see cref="ActionType"/> enum choose which typed call to make.
/// A prompt injection in a log line can at its very best produce a plan that gets refused. It
/// cannot name a verb, a resource or a namespace that is not already in this file.
/// </para>
/// <para>
/// <b>Order is the safety property.</b> Snapshot, then admit (which commits, with its audit
/// row), then mutate, then record. "No audit, no action" only holds if the commit precedes
/// the side effect - so a crash between them leaves an admitted action that never ran, which
/// is visible and recoverable, rather than a mutation nobody recorded.
/// </para>
/// </remarks>
public sealed class ActionExecutor(
    KubernetesApi api,
    IActionRepository actions,
    ActionEventMirror events,
    IOptionsMonitor<PolicyOptions> policyOptions,
    HephaistoMetrics metrics,
    IClock clock,
    ILogger<ActionExecutor> logger) : IActionExecutor
{
    /// <summary>
    /// Outcome vocabulary. Closed on purpose: <c>AgentAction.Outcome</c> is a free string and
    /// it reaches a Prometheus label, so anything derived from an API message would put
    /// unbounded cardinality on a counter. The detail belongs on the span and in Error.
    /// </summary>
    public static class Outcomes
    {
        public const string Applied = "applied";
        public const string DryRun = "dry_run";
        public const string Failed = "failed";
        public const string Unsupported = "unsupported";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ActionExecutionResult> ExecuteAsync(AgentAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!CanPerform(action.Type))
        {
            // Fails closed and says which capability is missing, rather than reaching the API
            // and returning a 403 that reads like a misconfiguration. CordonNode and DrainNode
            // are the important ones: their ClusterRole ships deliberately UNBOUND, so binding
            // it is a separate human act and until then the honest answer is "cannot", not
            // "forbidden".
            return await UnsupportedAsync(action, ct).ConfigureAwait(false);
        }

        // 1. Snapshot BEFORE admission, so the row admission commits already carries it. A
        //    target that cannot be read is a target that must not be acted on: there would be
        //    nothing to verify against and nothing to describe afterwards.
        string preState;

        try
        {
            preState = await SnapshotAsync(action.Target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not snapshot {Workload} before acting.", action.Target.WorkloadKey);

            return await FailAsync(
                action, ActionExecutionOutcome.NoPreState,
                $"could not read the target before acting: {ex.Message}", ct).ConfigureAwait(false);
        }

        action.PreState = preState;

        // 2. Admission. One Serializable transaction: workload lock, kill switch re-resolved
        //    inside it, quarantine, five budget and cooldown gates, the row and its audit
        //    event committed together. Everything before this point is advisory.
        var admission = await actions
            .TryAdmitActionAsync(action, policyOptions.CurrentValue, ct)
            .ConfigureAwait(false);

        if (!admission.Admitted)
        {
            return await RefusedAsync(action, admission, ct).ConfigureAwait(false);
        }

        var mode = admission.Budget?.Mode ?? AgentMode.Observe;
        var dryRun = action.DryRun;

        using var activity = HephaistoMetrics.ActivitySource.StartActivity(
            HephaistoTelemetry.Spans.ActionExecute, ActivityKind.Client);

        activity?.SetTag("action.id", action.Id);
        activity?.SetTag("action.type", action.Type.ToString());
        activity?.SetTag("action.risk", action.Risk.ToString());
        activity?.SetTag("action.mode", mode.ToString());
        activity?.SetTag("action.dry_run", dryRun);
        activity?.SetTag("k8s.namespace.name", action.Target.Namespace);
        activity?.SetTag("k8s.workload", action.Target.WorkloadKey);

        // 3. Mark it in flight and commit that, so a process death mid-call is visible as
        //    Executing rather than as an approved action that mysteriously never ran.
        action.State = ActionState.Executing;
        await actions.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            await PerformAsync(action, dryRun, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            logger.LogError(
                ex, "{Action} on {Workload} failed.", action.Type, action.Target.WorkloadKey);

            metrics.ActionExecuted(action.Type, mode, Outcomes.Failed);

            return await FailAsync(action, ActionExecutionOutcome.Failed, ex.Message, ct).ConfigureAwait(false);
        }

        // 4. Record what the world looks like now. Best-effort: the action HAS happened, and
        //    losing the after-picture must not turn a successful action into a failed one.
        action.PostState = await TrySnapshotAsync(action.Target, ct).ConfigureAwait(false);
        action.ExecutedAt = clock.UtcNow;
        action.Outcome = dryRun ? Outcomes.DryRun : Outcomes.Applied;
        action.State = ActionState.Executed;

        // Scheduled only for a real change, and staged into the same save as the outcome, so
        // an action can never be recorded as executed without the checks that will judge it.
        // A dry run gets none: nothing is different, so all three would fail and the last one
        // would revert an action that never happened.
        if (!dryRun)
        {
            actions.AddVerifications(VerificationSchedule.For(action, action.ExecutedAt.Value));
        }

        await actions.SaveChangesAsync(ct).ConfigureAwait(false);

        metrics.ActionExecuted(action.Type, mode, action.Outcome);

        // Onto the object itself, so `kubectl describe` answers "why did this restart" where
        // an on-call engineer is already looking. Not for a dry run: nothing happened to the
        // object, and an event saying otherwise would be the most misleading line in the
        // output. Best effort, and after the row is committed - the durable record is the
        // audit trail, and failing to annotate must not fail the action.
        if (!dryRun)
        {
            await events.MirrorAsync(action, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "{Action} on {Workload} {Verb} (mode {Mode}).",
            action.Type, action.Target.WorkloadKey, dryRun ? "validated, dry run" : "applied", mode);

        return new ActionExecutionResult
        {
            Outcome = ActionExecutionOutcome.Executed,
            DryRun = dryRun,
            Mode = mode,
            Detail = dryRun
                ? "the API server validated the call and discarded it; nothing changed"
                : null,
        };
    }

    /// <summary>
    /// The closed vocabulary of things this executor can do, matched to the verbs the write
    /// Role actually grants.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="ActionType"/>. A type absent here is refused
    /// before any call is made, which is the difference between "Hephaisto does not do that"
    /// and a 403 during an incident. <c>SilenceAlert</c> needs an outbound HTTP client, which
    /// does not exist anywhere in <c>src/</c> yet; <c>CordonNode</c> and <c>DrainNode</c> have
    /// a ClusterRole that ships unbound on purpose.
    /// </remarks>
    private static bool CanPerform(ActionType type) => type is
        ActionType.RestartPod or
        ActionType.RolloutRestart or
        ActionType.ScaleWorkload or
        ActionType.DeleteStuckJob or
        ActionType.DeleteFailedJobPods;

    private async Task PerformAsync(AgentAction action, bool dryRun, CancellationToken ct)
    {
        // "All" is the only value the API server accepts, and passing null is what makes a
        // call real. Everything below routes its dry-run through this one variable so a new
        // action type cannot forget it.
        var dr = dryRun ? "All" : null;
        var target = action.Target;

        switch (action.Type)
        {
            case ActionType.RestartPod:
                // There is no RESTART verb. Deleting a managed pod IS the restart: the
                // controller observes the missing replica and creates one.
                await api.Core
                    .DeleteNamespacedPodAsync(target.Name, target.Namespace, dryRun: dr, cancellationToken: ct)
                    .ConfigureAwait(false);
                break;

            case ActionType.RolloutRestart:
                await RolloutRestartAsync(target, dr, ct).ConfigureAwait(false);
                break;

            case ActionType.ScaleWorkload:
                await ScaleAsync(action, dr, ct).ConfigureAwait(false);
                break;

            case ActionType.DeleteStuckJob:
                // Background propagation so the Job's pods go with it. Orphaning them would
                // leave exactly the mess the action exists to clear.
                await api.Batch
                    .DeleteNamespacedJobAsync(
                        target.Name, target.Namespace,
                        propagationPolicy: "Background", dryRun: dr, cancellationToken: ct)
                    .ConfigureAwait(false);
                break;

            case ActionType.DeleteFailedJobPods:
                await DeleteFailedJobPodsAsync(target, dr, ct).ConfigureAwait(false);
                break;

            default:
                // Unreachable: CanPerform gates this switch. Throwing rather than silently
                // doing nothing, because "reported success and did nothing" is the worst
                // outcome available to an executor.
                throw new InvalidOperationException(
                    $"{action.Type} passed CanPerform but has no implementation. This is a bug.");
        }
    }

    /// <summary>
    /// Stamps <c>kubectl.kubernetes.io/restartedAt</c> on the pod template, which is exactly
    /// what <c>kubectl rollout restart</c> does - a controlled, gradual replacement rather
    /// than the blunt pod delete.
    /// </summary>
    private async Task RolloutRestartAsync(TargetRef target, string? dryRun, CancellationToken ct)
    {
        var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        var patch = new V1Patch(
            JsonSerializer.Serialize(new
            {
                spec = new
                {
                    template = new
                    {
                        metadata = new
                        {
                            annotations = new Dictionary<string, string>
                            {
                                ["kubectl.kubernetes.io/restartedAt"] =
                                    clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                            },
                        },
                    },
                },
            }, Json),
            V1Patch.PatchType.MergePatch);

        switch (kind)
        {
            case "Deployment":
                await api.Apps.PatchNamespacedDeploymentAsync(
                    patch, name, target.Namespace, dryRun: dryRun, cancellationToken: ct).ConfigureAwait(false);
                break;

            case "StatefulSet":
                await api.Apps.PatchNamespacedStatefulSetAsync(
                    patch, name, target.Namespace, dryRun: dryRun, cancellationToken: ct).ConfigureAwait(false);
                break;

            case "DaemonSet":
                await api.Apps.PatchNamespacedDaemonSetAsync(
                    patch, name, target.Namespace, dryRun: dryRun, cancellationToken: ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException(
                    $"cannot rollout-restart a {kind}; only Deployment, StatefulSet and DaemonSet have pod templates");
        }
    }

    /// <summary>
    /// Patches the <c>scale</c> subresource, which is a separate RBAC resource from the
    /// workload itself and is granted separately.
    /// </summary>
    private async Task ScaleAsync(AgentAction action, string? dryRun, CancellationToken ct)
    {
        var replicas = Replicas(action)
            ?? throw new InvalidOperationException(
                "scale_workload needs a 'replicas' integer in its arguments; the plan supplied none");

        if (replicas < 0)
        {
            throw new InvalidOperationException($"replicas must not be negative, got {replicas}");
        }

        var target = action.Target;
        var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        var patch = new V1Patch(
            JsonSerializer.Serialize(new { spec = new { replicas } }, Json),
            V1Patch.PatchType.MergePatch);

        switch (kind)
        {
            case "Deployment":
                await api.Apps.PatchNamespacedDeploymentScaleAsync(
                    patch, name, target.Namespace, dryRun: dryRun, cancellationToken: ct).ConfigureAwait(false);
                break;

            case "StatefulSet":
                await api.Apps.PatchNamespacedStatefulSetScaleAsync(
                    patch, name, target.Namespace, dryRun: dryRun, cancellationToken: ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"cannot scale a {kind}");
        }
    }

    private static int? Replicas(AgentAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Arguments))
        {
            return null;
        }

        using var document = JsonDocument.Parse(action.Arguments);

        return document.RootElement.TryGetProperty("replicas", out var value) &&
               value.TryGetInt32(out var replicas)
            ? replicas
            : null;
    }

    /// <summary>
    /// Deletes the FAILED pods of a Job, leaving the Job and any running pod alone.
    /// </summary>
    /// <remarks>
    /// One delete per pod rather than a collection delete. <c>deletecollection</c> is
    /// deliberately absent from the write Role so that a single bad label selector cannot take
    /// out a namespace, and this is the action that would otherwise reach for it.
    /// </remarks>
    private async Task DeleteFailedJobPodsAsync(TargetRef target, string? dryRun, CancellationToken ct)
    {
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        var pods = await api.Core
            .ListNamespacedPodAsync(target.Namespace, labelSelector: $"job-name={name}", cancellationToken: ct)
            .ConfigureAwait(false);

        var failed = pods.Items
            .Where(p => string.Equals(p.Status?.Phase, "Failed", StringComparison.Ordinal))
            .ToList();

        foreach (var pod in failed)
        {
            await api.Core
                .DeleteNamespacedPodAsync(
                    pod.Metadata.Name, target.Namespace, dryRun: dryRun, cancellationToken: ct)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Deleted {Count} failed pod(s) of job {Namespace}/{Job}.", failed.Count, target.Namespace, name);
    }

    /// <summary>
    /// The target object as stored JSON, for the before/after pair the audit trail rests on.
    /// </summary>
    private async Task<string> SnapshotAsync(TargetRef target, CancellationToken ct)
    {
        var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        object o = kind switch
        {
            "Deployment" => await api.Apps.ReadNamespacedDeploymentAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false),
            "StatefulSet" => await api.Apps.ReadNamespacedStatefulSetAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false),
            "DaemonSet" => await api.Apps.ReadNamespacedDaemonSetAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false),
            "Job" => await api.Batch.ReadNamespacedJobAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false),
            _ => await api.Core.ReadNamespacedPodAsync(target.Name, target.Namespace, cancellationToken: ct).ConfigureAwait(false),
        };

        return JsonSerializer.Serialize(Summarise(o), Json);
    }

    private async Task<string?> TrySnapshotAsync(TargetRef target, CancellationToken ct)
    {
        try
        {
            return await SnapshotAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Expected on the happy path of a pod restart: the pod this action deleted is
            // gone, and its replacement has a different name. Not a failure.
            logger.LogDebug(ex, "No post-state for {Workload}.", target.WorkloadKey);

            return null;
        }
    }

    /// <summary>
    /// The fields worth keeping, rather than the whole object.
    /// </summary>
    /// <remarks>
    /// A serialised V1Deployment is tens of kilobytes of managedFields and defaulted spec, per
    /// action, in a jsonb column kept indefinitely. What a human reconstructing the decision
    /// needs is the handful of numbers the action was about.
    /// </remarks>
    private static object Summarise(object o) => o switch
    {
        V1Deployment d => new
        {
            kind = "Deployment",
            name = d.Metadata?.Name,
            generation = d.Metadata?.Generation,
            replicas = d.Spec?.Replicas,
            ready = d.Status?.ReadyReplicas,
            updated = d.Status?.UpdatedReplicas,
            available = d.Status?.AvailableReplicas,
            observedGeneration = d.Status?.ObservedGeneration,
            images = d.Spec?.Template?.Spec?.Containers?.Select(c => c.Image),
        },
        V1StatefulSet s => new
        {
            kind = "StatefulSet",
            name = s.Metadata?.Name,
            generation = s.Metadata?.Generation,
            replicas = s.Spec?.Replicas,
            ready = s.Status?.ReadyReplicas,
            updated = s.Status?.UpdatedReplicas,
            available = (int?)null,
            observedGeneration = s.Status?.ObservedGeneration,
            images = s.Spec?.Template?.Spec?.Containers?.Select(c => c.Image),
        },
        V1DaemonSet d => new
        {
            kind = "DaemonSet",
            name = d.Metadata?.Name,
            generation = d.Metadata?.Generation,
            replicas = d.Status?.DesiredNumberScheduled,
            ready = d.Status?.NumberReady,
            updated = d.Status?.UpdatedNumberScheduled,
            available = d.Status?.NumberAvailable,
            observedGeneration = d.Status?.ObservedGeneration,
            images = d.Spec?.Template?.Spec?.Containers?.Select(c => c.Image),
        },
        V1Job j => new
        {
            kind = "Job",
            name = j.Metadata?.Name,
            active = j.Status?.Active,
            succeeded = j.Status?.Succeeded,
            failed = j.Status?.Failed,
        },
        V1Pod p => new
        {
            kind = "Pod",
            name = p.Metadata?.Name,
            uid = p.Metadata?.Uid,
            phase = p.Status?.Phase,
            node = p.Spec?.NodeName,
            restarts = p.Status?.ContainerStatuses?.Sum(c => c.RestartCount),
            ready = p.Status?.ContainerStatuses?.All(c => c.Ready),
            images = p.Spec?.Containers?.Select(c => c.Image),
        },
        _ => new { kind = o.GetType().Name },
    };

    // ------------------------------------------------------------------
    // Terminal states
    // ------------------------------------------------------------------

    private async Task<ActionExecutionResult> RefusedAsync(
        AgentAction action, ActionAdmission admission, CancellationToken ct)
    {
        // Admission rolled its transaction back and detached the action, so the in-memory
        // object is no longer tracked. The row still exists - the investigation wrote it when
        // the plan was proposed - and it must now read Denied rather than Approved, or the UI
        // shows an action as approved that admission refused seconds later.
        action.State = ActionState.Denied;
        action.DecisionReasons = [.. action.DecisionReasons, .. admission.Reasons];

        await ReattachAndSaveAsync(action, ct).ConfigureAwait(false);

        logger.LogInformation(
            "{Action} on {Workload} refused at admission: {Refusal} - {Reasons}",
            action.Type, action.Target.WorkloadKey, admission.Refusal, string.Join("; ", admission.Reasons));

        return new ActionExecutionResult
        {
            Outcome = ActionExecutionOutcome.Refused,
            Refusal = admission.Refusal,
            Mode = admission.Budget?.Mode ?? AgentMode.Observe,
            Detail = string.Join("; ", admission.Reasons),
        };
    }

    private async Task<ActionExecutionResult> FailAsync(
        AgentAction action, ActionExecutionOutcome outcome, string detail, CancellationToken ct)
    {
        action.State = ActionState.Failed;
        action.Error = detail;
        action.Outcome = Outcomes.Failed;
        action.ExecutedAt ??= clock.UtcNow;

        await ReattachAndSaveAsync(action, ct).ConfigureAwait(false);

        return new ActionExecutionResult
        {
            Outcome = outcome,
            Detail = detail,
            DryRun = action.DryRun,
        };
    }

    private async Task<ActionExecutionResult> UnsupportedAsync(AgentAction action, CancellationToken ct)
    {
        var detail = $"{action.Type} is not implemented by this executor and nothing was attempted";

        action.State = ActionState.Failed;
        action.Error = detail;
        action.Outcome = Outcomes.Unsupported;

        await ReattachAndSaveAsync(action, ct).ConfigureAwait(false);

        logger.LogWarning("{Action} was approved but this build cannot perform it.", action.Type);

        return new ActionExecutionResult
        {
            Outcome = ActionExecutionOutcome.Unsupported,
            Detail = detail,
        };
    }

    /// <summary>
    /// Saves an action that may have been detached by a rolled-back admission.
    /// </summary>
    private async Task ReattachAndSaveAsync(AgentAction action, CancellationToken ct)
    {
        try
        {
            await actions.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording the outcome must never be the thing that throws out of an executor:
            // by this point the cluster may already have changed, and losing the row would
            // leave a mutation with no record, which is the one state this system may not be
            // in. Loudly, and carry on.
            logger.LogError(
                ex, "Could not record the outcome of {Action} on {Workload}.",
                action.Type, action.Target.WorkloadKey);
        }
    }
}
