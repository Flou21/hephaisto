using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;

namespace Hephaisto.Agent.Persistence.Repositories;

/// <summary>Why an action was not admitted. One value per independent gate.</summary>
public enum AdmissionRefusal
{
    None = 0,

    /// <summary>Mode is Off or Observe, or the runaway latch is set.</summary>
    KillSwitch = 1,

    Quarantined = 2,
    WorkloadCooldown = 3,
    IncidentBudget = 4,
    WorkloadBudget = 5,
    HourlyBudget = 6,
    DailyBudget = 7,

    /// <summary>The incident this action claims to belong to does not exist.</summary>
    UnknownIncident = 8,

    /// <summary>
    /// Serialization failures exhausted the retry budget. Fails closed: under contention
    /// the safe answer to "may I change the cluster" is no.
    /// </summary>
    Contention = 9,
}

/// <summary>
/// The result of the one call in this layer that can lead to a cluster mutation.
/// <see cref="Reasons"/> is populated on admission as well as refusal, for the same reason
/// <see cref="PolicyResult.Reasons"/> is: the question asked after an outage is always "why
/// did it think this was fine".
/// </summary>
public sealed record ActionAdmission
{
    public required bool Admitted { get; init; }

    public required AdmissionRefusal Refusal { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Counts as they were inside the admitting transaction. Attached so the
    /// caller can log or export them without a second, differently-timed read.</summary>
    public ActionBudgetSnapshot? Budget { get; init; }

    public static ActionAdmission Admit(ActionBudgetSnapshot budget, params string[] reasons) =>
        new() { Admitted = true, Refusal = AdmissionRefusal.None, Reasons = reasons, Budget = budget };

    public static ActionAdmission Refuse(AdmissionRefusal refusal, params string[] reasons) =>
        new() { Admitted = false, Refusal = refusal, Reasons = reasons };

    public static ActionAdmission Refuse(AdmissionRefusal refusal, ActionBudgetSnapshot? budget, params string[] reasons) =>
        new() { Admitted = false, Refusal = refusal, Reasons = reasons, Budget = budget };
}

/// <summary>What the budget looked like at the instant the decision was made.</summary>
public sealed record ActionBudgetSnapshot
{
    public required int ActionsOnIncident { get; init; }

    public required int ActionsOnWorkloadLastHour { get; init; }

    public required int ActionsClusterWideLastHour { get; init; }

    public required int ActionsClusterWideLastDay { get; init; }

    public DateTimeOffset? LastActionOnWorkloadAt { get; init; }

    public required AgentMode Mode { get; init; }
}

public interface IActionRepository
{
    Task<AgentAction?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<AgentAction>> GetForIncidentAsync(Guid incidentId, CancellationToken ct);

    /// <summary>
    /// Budget check, cooldown check, kill-switch check and the INSERT, in one serializable
    /// transaction. Read the implementation comment before changing anything here.
    /// </summary>
    Task<ActionAdmission> TryAdmitActionAsync(AgentAction action, PolicyOptions options, CancellationToken ct);

    /// <summary>
    /// The same counts <see cref="TryAdmitActionAsync"/> gates on, read outside a transaction
    /// so the policy engine can see them before an action is proposed.
    /// </summary>
    /// <remarks>
    /// Deliberately advisory. These counts feed the policy engine's <b>downgrade</b> decision -
    /// "you are at your hourly budget, so a human should confirm this" - and are read without a
    /// lock, so they can be stale by the time anything acts. That is fine, and it is why
    /// admission re-reads them inside a <c>Serializable</c> transaction rather than trusting
    /// what it is handed. Never treat this as the enforcement point; enforcement is the one
    /// place where losing a race means an unintended write.
    /// </remarks>
    Task<ActionBudgetSnapshot> ReadBudgetAsync(
        Guid incidentId, TargetRef target, AgentMode mode, CancellationToken ct);

    /// <summary>
    /// When the oscillation detector's quarantine on this workload expires, if there is one.
    /// </summary>
    /// <remarks>
    /// Advisory here, exactly like the budget counts: the authoritative check happens inside
    /// the admission transaction, against the lock row it has already taken.
    /// </remarks>
    Task<DateTimeOffset?> GetWorkloadQuarantineAsync(TargetRef target, CancellationToken ct);

    /// <summary>
    /// Stages the scheduled checks for an action that changed something. Committed by the
    /// caller's next save, so an executed action and its verifications land together.
    /// </summary>
    void AddVerifications(IEnumerable<Verification> verifications);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
