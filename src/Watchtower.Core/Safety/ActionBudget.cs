using Watchtower.Core.Policy;

namespace Watchtower.Core.Safety;

/// <summary>Which cap the next action would break through, if any.</summary>
public enum BudgetWindow
{
    /// <summary>Nothing is exhausted; the next action is within every cap.</summary>
    None = 0,
    Incident = 1,
    WorkloadHour = 2,
    Hour = 3,
    Day = 4,
}

/// <summary>
/// <paramref name="Reason"/> is populated even when nothing is exceeded, so the UI can show
/// "3 of 10 actions used this hour" from the same call that policy makes.
/// </summary>
public readonly record struct BudgetStatus(BudgetWindow Exceeded, int Used, int Limit, string Reason)
{
    public bool IsExceeded => Exceeded is not BudgetWindow.None;
}

/// <summary>
/// The action budget, as a pure calculator over counts.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="PolicyEngine"/> so the Blazor UI can render "why is this button
/// asking me to approve a restart" using the identical arithmetic. A second implementation in
/// the UI would drift, and the drift would surface as a human being told the agent is within
/// budget while the engine has already downgraded the action.
/// </para>
/// <para>
/// Counts are "actions already taken", so the cap is reached at <c>used &gt;= limit</c>: the
/// action being judged is the one that would take the count past the limit.
/// </para>
/// </remarks>
public static class ActionBudget
{
    /// <summary>
    /// Windows are checked narrowest-first, because "you have already restarted this pod
    /// three times for this one incident" is a more useful thing to tell a human than
    /// "the cluster-wide daily cap is full".
    /// </summary>
    public static BudgetStatus Evaluate(
        int actionsOnIncident,
        int actionsOnWorkloadThisHour,
        int actionsClusterWideLastHour,
        int actionsClusterWideLastDay,
        PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (actionsOnIncident >= options.MaxActionsPerIncident)
        {
            return new BudgetStatus(
                BudgetWindow.Incident,
                actionsOnIncident,
                options.MaxActionsPerIncident,
                $"incident action budget exhausted ({actionsOnIncident}/{options.MaxActionsPerIncident})");
        }

        if (actionsOnWorkloadThisHour >= options.MaxActionsPerWorkloadPerHour)
        {
            return new BudgetStatus(
                BudgetWindow.WorkloadHour,
                actionsOnWorkloadThisHour,
                options.MaxActionsPerWorkloadPerHour,
                $"workload hourly action budget exhausted ({actionsOnWorkloadThisHour}/{options.MaxActionsPerWorkloadPerHour})");
        }

        if (actionsClusterWideLastHour >= options.MaxActionsPerHour)
        {
            return new BudgetStatus(
                BudgetWindow.Hour,
                actionsClusterWideLastHour,
                options.MaxActionsPerHour,
                $"cluster hourly action budget exhausted ({actionsClusterWideLastHour}/{options.MaxActionsPerHour})");
        }

        if (actionsClusterWideLastDay >= options.MaxActionsPerDay)
        {
            return new BudgetStatus(
                BudgetWindow.Day,
                actionsClusterWideLastDay,
                options.MaxActionsPerDay,
                $"cluster daily action budget exhausted ({actionsClusterWideLastDay}/{options.MaxActionsPerDay})");
        }

        return new BudgetStatus(
            BudgetWindow.None,
            actionsClusterWideLastHour,
            options.MaxActionsPerHour,
            $"within budget ({actionsClusterWideLastHour}/{options.MaxActionsPerHour} this hour)");
    }

    /// <summary>
    /// Convenience overload over the facts the policy engine already has.
    /// <see cref="ClusterFacts.RecentActionsOnWorkload"/> is the per-workload counter; it is
    /// gathered over the budget window, not the cooldown window, despite its name.
    /// </summary>
    public static BudgetStatus Evaluate(ClusterFacts facts, PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return Evaluate(
            facts.ActionsOnIncident,
            facts.RecentActionsOnWorkload,
            facts.ActionsClusterWideLastHour,
            facts.ActionsClusterWideLastDay,
            options);
    }
}
