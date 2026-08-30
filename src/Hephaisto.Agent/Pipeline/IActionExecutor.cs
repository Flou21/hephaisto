using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Turns an approved <see cref="AgentAction"/> into an API call, or explains why it did not.
/// </summary>
public interface IActionExecutor
{
    /// <summary>
    /// Admits the action and, if admission allows, performs it. Sets the action's terminal
    /// state, PreState, PostState, Outcome and Error, and saves them.
    /// </summary>
    /// <remarks>
    /// Never throws for an ordinary refusal or a failed API call - both are outcomes, not
    /// exceptions, because both must be recorded rather than propagated into whatever was
    /// unlucky enough to call this.
    /// </remarks>
    Task<ActionExecutionResult> ExecuteAsync(AgentAction action, CancellationToken ct);
}

/// <summary>
/// The executor that cannot execute. Registered with <c>TryAdd</c>, so it is what a host gets
/// if the real one was never wired.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>EscalateOnlyInvestigator</c>, and for a sharper reason. Registration
/// order decides which implementation wins, and the failure mode of getting it wrong should
/// be an agent that does nothing, not an agent that does something nobody configured. A
/// missing registration must never be able to resolve to a working executor.
/// </para>
/// <para>
/// It refuses loudly rather than silently: an approved action that quietly evaporates is
/// indistinguishable, from the outside, from one that ran.
/// </para>
/// </remarks>
public sealed class RefusingActionExecutor(ILogger<RefusingActionExecutor> logger) : IActionExecutor
{
    public Task<ActionExecutionResult> ExecuteAsync(AgentAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        logger.LogWarning(
            "No action executor is registered, so {Action} on {Workload} was not performed. "
            + "This build cannot act; nothing has changed in the cluster.",
            action.Type, action.Target.WorkloadKey);

        return Task.FromResult(new ActionExecutionResult
        {
            Outcome = ActionExecutionOutcome.Unsupported,
            Detail = "no action executor is registered in this host",
        });
    }
}
