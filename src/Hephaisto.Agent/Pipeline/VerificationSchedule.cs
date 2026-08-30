using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// When an executed action gets checked, and the rows that say so.
/// </summary>
/// <remarks>
/// <para>
/// T+60s, T+5m, T+15m. The three are not retries of one question - they answer different
/// ones. At 60 seconds a pod has been recreated and scheduled but may not be Ready, so the
/// check is "did anything obviously break"; at 5 minutes the workload should have converged;
/// at 15 minutes a fault that only looks fixed - a crash loop with a back-off long enough to
/// hide inside the first two windows - has had time to come back.
/// </para>
/// <para>
/// Only the last one may conclude a failure. A check that has not passed yet is not a check
/// that failed, and rolling back at 60 seconds because a pod was still pulling its image
/// would make the agent the cause of the next incident.
/// </para>
/// </remarks>
public static class VerificationSchedule
{
    public static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    /// <summary>The attempt number after which a still-failing action is given up on.</summary>
    public static int FinalAttempt => Delays.Length;

    /// <summary>
    /// The three pending rows for an action that actually changed something.
    /// </summary>
    /// <remarks>
    /// Never called for a dry run. Nothing changed, so all three checks would fail and the
    /// third would trigger a rollback of an action that never happened.
    /// </remarks>
    public static IEnumerable<Verification> For(AgentAction action, DateTimeOffset executedAt)
    {
        ArgumentNullException.ThrowIfNull(action);

        return Delays.Select((delay, i) => new Verification
        {
            ActionId = action.Id,
            Attempt = i + 1,
            DueAt = executedAt + delay,
            Outcome = VerificationOutcome.Pending,
        });
    }
}
