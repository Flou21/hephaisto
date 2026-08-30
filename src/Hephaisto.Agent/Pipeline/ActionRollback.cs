using System.Text.Json;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

public sealed record RollbackResult
{
    public required bool Reverted { get; init; }

    public required string Detail { get; init; }
}

/// <summary>
/// Undoes an action whose verification failed, where undoing is a thing that exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rollback spec is written by the model, and is never executed as written.</b> It is
/// free-form JSON on a row the planning phase produced, so treating it as instructions would
/// hand the model exactly the mutating handle the three-phase split exists to deny it - and
/// it would do so on the one code path nobody watches, minutes after the incident, with the
/// budget checks deliberately bypassed. Instead the spec is read for a small number of typed
/// values, and the revert is built as an ordinary <see cref="AgentAction"/> over the same
/// closed <see cref="ActionType"/> enum, admitted and executed like any other.
/// </para>
/// <para>
/// Most of what this agent can do has no inverse, and says so rather than inventing one. A
/// restarted pod cannot be un-restarted; a deleted Job cannot be brought back. For those the
/// honest answer on a failed verification is escalation, which is what the caller does with
/// <see cref="RollbackResult.Reverted"/> false - and it is the reason the policy engine
/// exempts self-healing types from needing a spec at all rather than accepting a fictional one.
/// </para>
/// </remarks>
public sealed class ActionRollback(
    IActionExecutor executor,
    IClock clock,
    ILogger<ActionRollback> logger)
{
    public async Task<RollbackResult> TryRevertAsync(AgentAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Build(action) is not { } revert)
        {
            return new RollbackResult
            {
                Reverted = false,
                Detail = $"{action.Type} has no inverse, so a human has it",
            };
        }

        var result = await executor.ExecuteAsync(revert, ct).ConfigureAwait(false);

        if (result.Outcome != ActionExecutionOutcome.Executed)
        {
            logger.LogError(
                "Rollback of {Action} on {Workload} did not execute: {Outcome} - {Detail}",
                action.Type, action.Target.WorkloadKey, result.Outcome, result.Detail);

            return new RollbackResult
            {
                Reverted = false,
                Detail = $"the rollback itself could not run ({result.Outcome}): {result.Detail}",
            };
        }

        action.State = ActionState.RolledBack;

        return new RollbackResult
        {
            Reverted = true,
            Detail = $"rolled back by {revert.Type}",
        };
    }

    /// <summary>
    /// The typed revert for an action, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Deliberately a short list. Adding to it means being able to state, in code rather than
    /// in a JSON blob a model wrote, what returning to the previous state consists of.
    /// </remarks>
    private AgentAction? Build(AgentAction action)
    {
        switch (action.Type)
        {
            case ActionType.ScaleWorkload when PreviousReplicas(action) is { } replicas:
            {
                // The only revert currently expressible: put the replica count back. Read from
                // PreState - what the cluster actually looked like - in preference to the
                // model's rollback spec, because one is an observation and the other is a
                // claim, and they are only ever consulted when something has already gone wrong.
                return new AgentAction
                {
                    IncidentId = action.IncidentId,
                    Type = ActionType.ScaleWorkload,
                    Target = action.Target.Clone(),
                    Risk = action.Risk,
                    State = ActionState.Approved,
                    Decision = Core.Domain.PolicyDecision.Allow,
                    DecisionReasons = ["rollback of a failed verification"],
                    Arguments = JsonSerializer.Serialize(new { replicas }),
                    IsRollbackOf = action.Id,
                    ApprovedBy = IncidentStateMachine.VerifierActor,
                    ApprovalSource = ApprovalSource.Auto,
                    ApprovedAt = clock.UtcNow,
                    PredictedEffect = $"replicas back to {replicas}",
                    EvidenceFindingIds = [.. action.EvidenceFindingIds],
                };
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The replica count to return to: the observed one, falling back to the model's spec.
    /// </summary>
    private static int? PreviousReplicas(AgentAction action)
    {
        if (Read(action.PreState, "replicas") is { } observed)
        {
            return observed;
        }

        return Read(action.RollbackSpec, "replicas");
    }

    private static int? Read(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            // Model-authored, so malformed is a real possibility and not an exceptional one.
            return null;
        }
    }
}
