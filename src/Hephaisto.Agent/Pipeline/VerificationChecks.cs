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
public sealed class VerificationChecks(KubernetesApi api, ILogger<VerificationChecks> logger)
{
    public async Task<CheckResult> RunAsync(AgentAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return action.Type switch
            {
                ActionType.DeleteStuckJob or ActionType.DeleteFailedJobPods =>
                    await JobIsNoLongerFailingAsync(action.Target, ct).ConfigureAwait(false),

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
            }
        }

        var settled = observed >= generation && updated == desired;
        var healthy = settled && ready == desired && desired > 0 && waiting.Count == 0;

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
            Detail = waiting.Count > 0
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
