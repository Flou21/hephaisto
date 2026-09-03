using System.Text.Json;
using k8s;
using k8s.Models;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>The answer to "did that help", and the evidence for it.</summary>
public sealed record CheckResult
{
    public required VerificationOutcome Outcome { get; init; }

    public required string Detail { get; init; }

    /// <summary>The observations behind the verdict, stored as jsonb on the verification row.</summary>
    public object? Checks { get; init; }
}

/// <summary>
/// Deterministic predicates, one per action type, run against the cluster after an action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never an LLM.</b> The entity's own doc comment has said so since the schema was written,
/// and the state machine enforces the other half by refusing to let a model identity grant a
/// Resolved. A model marking its own work complete is not evidence, and it is the one place
/// where a plausible-sounding answer costs the most: it would close incidents that are still
/// happening.
/// </para>
/// <para>
/// The predicates are deliberately about the WORKLOAD rather than the object the action named.
/// A restarted pod is gone by definition - its name is not a thing to check - and what the
/// action was for is that the workload stops crash-looping.
/// </para>
/// </remarks>
public sealed class VerificationChecks(
    KubernetesApi api,
    TimeProvider time,
    ILogger<VerificationChecks> logger)
{
    /// <summary>
    /// How long a restarted container must have been up before it counts as recovered.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than a crash loop's Running window, and comfortably shorter than the
    /// 60 seconds before the first check runs, so it never rejects a genuine recovery.
    /// </remarks>
    private static readonly TimeSpan MinimumStableUptime = TimeSpan.FromSeconds(30);

    public async Task<CheckResult> RunAsync(AgentAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return action.Type switch
            {
                ActionType.DeleteStuckJob or ActionType.DeleteFailedJobPods =>
                    await JobIsNoLongerFailingAsync(action.Target, ct).ConfigureAwait(false),

                ActionType.RollbackDeployment =>
                    await RollbackLandedAsync(action, ct).ConfigureAwait(false),

                _ => await WorkloadIsHealthyAsync(action.Target, ct).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Inconclusive, never Failed. A check that could not run has learned nothing, and
            // treating "the API server timed out" as "the fix did not work" would roll back a
            // healthy cluster on a network blip.
            logger.LogWarning(ex, "Verification of {Workload} could not run.", action.Target.WorkloadKey);

            return new CheckResult
            {
                Outcome = VerificationOutcome.Inconclusive,
                Detail = $"the check could not run: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// The revision we asked for is the one now serving, and the workload is healthy on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half of backlog #42 that moves with the rollback executor. Every other
    /// predicate here is workload-shaped, and for a restart that is right - the pod is gone by
    /// definition, so the workload is the only thing left to check. For a rollback it is
    /// <b>not enough</b>: the pods of the previous revision were Ready throughout, so
    /// "the workload is healthy" is true before the action, during it, and after a rollback
    /// that did nothing at all. A predicate that passes on a no-op is worse than none, because
    /// it closes the incident.
    /// </para>
    /// <para>
    /// <b>The trap: a rollback does not restore the old revision number.</b> Rolling back from
    /// revision 3 to revision 2 produces revision <b>4</b>, whose template happens to equal
    /// revision 2's. So asserting <c>current revision == the one we rolled back to</c> is not
    /// merely fragile, it fails every single time, and it fails on a rollback that worked
    /// perfectly - which would revert a correct fix at T+60s.
    /// </para>
    /// <para>
    /// What is stable is the <b>ReplicaSet</b>. When a template matches one that already
    /// exists, the controller scales that existing ReplicaSet back up rather than creating a
    /// new one, so the object named in <c>PostState</c> is exactly the one that must end up
    /// carrying the replicas. That is the assertion.
    /// </para>
    /// <para>
    /// A missing or unreadable <c>PostState</c> is Inconclusive rather than Failed, for the
    /// same reason the catch in <see cref="RunAsync"/> is: a check that could not run has
    /// learned nothing, and treating that as "the fix did not work" rolls forward onto the
    /// revision that caused the incident.
    /// </para>
    /// </remarks>
    private async Task<CheckResult> RollbackLandedAsync(AgentAction action, CancellationToken ct)
    {
        var target = action.Target;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        if (RolledBackToReplicaSet(action.PostState) is not { Length: > 0 } expected)
        {
            return new CheckResult
            {
                Outcome = VerificationOutcome.Inconclusive,
                Detail = "the action recorded no ReplicaSet to verify against",
            };
        }

        var deployment = await api.Apps
            .ReadNamespacedDeploymentAsync(name, target.Namespace, cancellationToken: ct)
            .ConfigureAwait(false);

        var desired = deployment.Spec?.Replicas ?? 0;

        var replicaSet = await api.Apps
            .ReadNamespacedReplicaSetAsync(expected, target.Namespace, cancellationToken: ct)
            .ConfigureAwait(false);

        var ready = replicaSet.Status?.ReadyReplicas ?? 0;
        var scheduled = replicaSet.Spec?.Replicas ?? 0;

        var checks = new
        {
            replicaSet = expected,
            desiredReplicas = desired,
            replicaSetScaledTo = scheduled,
            replicaSetReady = ready,
        };

        if (scheduled == 0)
        {
            // The rolled-back-to ReplicaSet is still at zero: the patch did not take, or
            // something rolled forward again on top of it.
            return new CheckResult
            {
                Outcome = VerificationOutcome.Failed,
                Detail =
                    $"ReplicaSet {expected} is still scaled to 0, so the rollback did not take effect",
                Checks = checks,
            };
        }

        if (ready < desired)
        {
            return new CheckResult
            {
                Outcome = VerificationOutcome.Failed,
                Detail =
                    $"ReplicaSet {expected} has {ready} of {desired} replicas ready, so the revision "
                    + "rolled back to is not healthy either",
                Checks = checks,
            };
        }

        // The revision landed. Now the ordinary question - is the workload actually well - and
        // it is asked second rather than instead, because on its own it cannot tell a rollback
        // from a no-op.
        var health = await WorkloadIsHealthyAsync(target, ct).ConfigureAwait(false);

        return health with
        {
            Detail = health.Outcome == VerificationOutcome.Passed
                ? $"rolled back onto {expected}, {ready} of {desired} replicas ready; {health.Detail}"
                : health.Detail,
        };
    }

    /// <summary>
    /// The ReplicaSet name the executor recorded, or null if it recorded nothing usable.
    /// </summary>
    internal static string? RolledBackToReplicaSet(string? postState)
    {
        if (string.IsNullOrWhiteSpace(postState))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(postState);

            // Both guards are load-bearing and were both found by a test rather than by
            // reading: TryGetProperty throws on a root that is not an object (PostState can be
            // an array or a bare string), and GetString throws when the property is present
            // with a non-string value. Either would surface as an unhandled exception inside a
            // verification, which is reported as Inconclusive and leaves the incident sitting
            // in Verifying.
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("replicaSet", out var rs)
                   && rs.ValueKind == JsonValueKind.String
                ? rs.GetString()
                : null;
        }
        catch (JsonException)
        {
            // PostState falls back to a snapshot of the target for action types that do not
            // record their own after-state, so a non-rollback shape here is a real possibility
            // rather than corruption.
            return null;
        }
    }

    /// <summary>
    /// Every replica ready, the controller settled, and nothing restarting.
    /// </summary>
    /// <remarks>
    /// The restart-count clause is what makes this more than a readiness probe. A pod that
    /// crash-loops with a back-off can be Ready at the moment it is looked at and still be
    /// failing - which is precisely the fault a restart is most often used against, so a check
    /// that missed it would pass on the case it exists for.
    /// </remarks>
    private async Task<CheckResult> WorkloadIsHealthyAsync(TargetRef target, CancellationToken ct)
    {
        var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        int desired, ready, updated;
        long generation, observed;
        V1LabelSelector? selector;

        switch (kind)
        {
            case "Deployment":
            {
                var d = await api.Apps.ReadNamespacedDeploymentAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false);
                (desired, ready, updated) = (d.Spec?.Replicas ?? 0, d.Status?.ReadyReplicas ?? 0, d.Status?.UpdatedReplicas ?? 0);
                (generation, observed) = (d.Metadata?.Generation ?? 0, d.Status?.ObservedGeneration ?? 0);
                selector = d.Spec?.Selector;
                break;
            }

            case "StatefulSet":
            {
                var s = await api.Apps.ReadNamespacedStatefulSetAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false);
                (desired, ready, updated) = (s.Spec?.Replicas ?? 0, s.Status?.ReadyReplicas ?? 0, s.Status?.UpdatedReplicas ?? 0);
                (generation, observed) = (s.Metadata?.Generation ?? 0, s.Status?.ObservedGeneration ?? 0);
                selector = s.Spec?.Selector;
                break;
            }

            case "DaemonSet":
            {
                var d = await api.Apps.ReadNamespacedDaemonSetAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false);
                (desired, ready, updated) = (d.Status?.DesiredNumberScheduled ?? 0, d.Status?.NumberReady ?? 0, d.Status?.UpdatedNumberScheduled ?? 0);
                (generation, observed) = (d.Metadata?.Generation ?? 0, d.Status?.ObservedGeneration ?? 0);
                selector = d.Spec?.Selector;
                break;
            }

            default:
                return new CheckResult
                {
                    Outcome = VerificationOutcome.Inconclusive,
                    Detail = $"no health predicate for a {kind}",
                };
        }

        var restarts = 0;
        var waiting = new List<string>();
        var flapping = false;

        if (ClusterFactsRules.LabelSelector(selector) is { } labelSelector)
        {
            var pods = await api.Core
                .ListNamespacedPodAsync(target.Namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);

            foreach (var status in pods.Items.SelectMany(p => p.Status?.ContainerStatuses ?? []))
            {
                restarts += status.RestartCount;

                if (status.State?.Waiting?.Reason is { Length: > 0 } reason)
                {
                    waiting.Add(reason);
                }

                // A container that has restarted and has only just come up again has not
                // recovered - it is between crashes, and this is the moment it looks healthiest.
                //
                // Without this the check has a hole a crash loop fits through exactly. A
                // container with no readiness probe is Ready the instant it is Running, so a pod
                // that runs for two seconds and exits is Ready for two seconds of every cycle,
                // the Deployment reports availableReplicas: 1, and nothing is Waiting. Sample
                // in that window and a workload that is still failing passes verification, the
                // incident is Resolved, and the agent reports success for a fault it did not fix.
                if (status.RestartCount > 0 &&
                    status.State?.Running?.StartedAt is { } startedAt &&
                    time.GetUtcNow() - startedAt < MinimumStableUptime)
                {
                    flapping = true;
                }
            }
        }

        var settled = observed >= generation && updated == desired;
        var healthy = settled && ready == desired && desired > 0 && waiting.Count == 0 && !flapping;

        var checks = new
        {
            kind,
            name,
            desired,
            ready,
            updated,
            generation,
            observedGeneration = observed,
            restarts,
            waiting = waiting.Distinct().ToArray(),
            flapping,
        };

        if (healthy)
        {
            return new CheckResult
            {
                Outcome = VerificationOutcome.Passed,
                Detail = $"{kind}/{name} is settled with {ready}/{desired} ready and no container waiting",
                Checks = checks,
            };
        }

        // A rollout still in flight is not a failure - it is the action working. Saying
        // Inconclusive lets the next attempt look again instead of reverting mid-convergence.
        if (!settled)
        {
            return new CheckResult
            {
                Outcome = VerificationOutcome.Inconclusive,
                Detail = $"{kind}/{name} is still converging ({updated}/{desired} updated, generation {observed}/{generation})",
                Checks = checks,
            };
        }

        return new CheckResult
        {
            Outcome = VerificationOutcome.Failed,
            Detail = flapping
                ? $"{kind}/{name} is still restarting: a container has {restarts} restart(s) and came "
                  + $"up less than {MinimumStableUptime.TotalSeconds:F0}s ago"
                : waiting.Count > 0
                    ? $"{kind}/{name} has containers waiting: {string.Join(", ", waiting.Distinct())}"
                    : $"{kind}/{name} has {ready}/{desired} ready",
            Checks = checks,
        };
    }

    /// <summary>The Job is gone, or is no longer in a failed condition.</summary>
    private async Task<CheckResult> JobIsNoLongerFailingAsync(TargetRef target, CancellationToken ct)
    {
        var name = target.OwnerName is { Length: > 0 } on ? on : target.Name;

        V1Job job;

        try
        {
            job = await api.Batch.ReadNamespacedJobAsync(name, target.Namespace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Deleting it was the action. Gone is the success condition.
            return new CheckResult
            {
                Outcome = VerificationOutcome.Passed,
                Detail = $"job {target.Namespace}/{name} no longer exists",
            };
        }

        var failed = job.Status?.Conditions?.Any(c =>
            string.Equals(c.Type, "Failed", StringComparison.Ordinal) &&
            string.Equals(c.Status, "True", StringComparison.Ordinal)) == true;

        var checks = new
        {
            name,
            active = job.Status?.Active,
            succeeded = job.Status?.Succeeded,
            failed = job.Status?.Failed,
            condition = failed ? "Failed" : null,
        };

        return failed
            ? new CheckResult
            {
                Outcome = VerificationOutcome.Failed,
                Detail = $"job {target.Namespace}/{name} is still in a Failed condition",
                Checks = checks,
            }
            : new CheckResult
            {
                Outcome = VerificationOutcome.Passed,
                Detail = $"job {target.Namespace}/{name} is not failing",
                Checks = checks,
            };
    }
}
