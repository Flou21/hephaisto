using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Core.Safety;

namespace Hephaisto.Agent.Persistence.Repositories;

public sealed class ActionRepository(
    IKillSwitch killSwitch,
    HephaistoDbContext db,
    IAuditRepository audit,
    IClock clock,
    IOptionsMonitor<PersistenceOptions> options,
    ILogger<ActionRepository> logger) : IActionRepository
{
    /// <summary>
    /// States that mean "this action was admitted". Denied and Expired rows exist as a
    /// record of a refusal and must not count against a budget - refusing an action would
    /// otherwise consume the same budget as taking one.
    /// </summary>
    private static readonly ActionState[] AdmittedStates =
    [
        ActionState.Proposed,
        ActionState.AwaitingApproval,
        ActionState.Approved,
        ActionState.Executing,
        ActionState.Executed,
        ActionState.Failed,
        ActionState.Verifying,
        ActionState.Verified,
        ActionState.RolledBack,
    ];

    public Task<AgentAction?> GetAsync(Guid id, CancellationToken ct) =>
        db.AgentActions.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<AgentAction>> GetForIncidentAsync(Guid incidentId, CancellationToken ct) =>
        await db.AgentActions
            .Where(a => a.IncidentId == incidentId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>
    /// THE critical method in this layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget check, the cooldown check, the kill-switch check and the INSERT all happen
    /// inside ONE transaction at <see cref="IsolationLevel.Serializable"/>, additionally
    /// serialised per workload by taking a row lock on <c>workload_action_locks</c> first.
    /// Splitting them apart - checking the budget, then inserting - puts a TOCTOU race on
    /// the single code path where a race means an unintended <c>kubectl delete</c>. Two
    /// investigations concluding at the same moment about the same crash-looping Deployment
    /// would each read "0 actions this hour, cooldown clear", each decide it is within
    /// budget, and each act. The budget would say one; the cluster would get two. With a
    /// restart that is merely wasteful; with a drain or a scale-to-zero it is the outage.
    /// </para>
    /// <para>
    /// Both mechanisms are here on purpose. The lock row makes the common case - concurrent
    /// admissions for the SAME workload - block rather than abort, so it costs a wait
    /// instead of a retry. Serializable then covers what a per-workload lock cannot: the
    /// cluster-wide hourly and daily budgets, which are read across every workload and would
    /// otherwise still race between two admissions for two different workloads.
    /// </para>
    /// <para>
    /// Serialization failures are retried a bounded number of times and then REFUSED, never
    /// admitted. Under contention the safe answer to "may I change the cluster" is no.
    /// </para>
    /// <para>
    /// Rollbacks (<see cref="AgentAction.IsRollbackOf"/> set) bypass the budget and cooldown
    /// gates but not the kill switch: you must always be able to undo, even at the cap - a
    /// budget that blocks the fix for the action that exhausted it is worse than no budget.
    /// </para>
    /// </remarks>
    public async Task<ActionAdmission> TryAdmitActionAsync(
        AgentAction action,
        PolicyOptions policy,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, options.CurrentValue.MaxAdmissionRetries);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AdmitOnceAsync(action, policy, ct);
            }
            catch (PostgresException ex) when (
                ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                // The insert and its audit row may already be staged; drop both so the
                // retry does not attempt either of them twice.
                DetachStaged(action);

                if (attempt >= maxAttempts)
                {
                    logger.LogWarning(
                        ex,
                        "Refusing action {ActionId} on {Workload}: {Attempts} serialization failures",
                        action.Id,
                        action.Target.WorkloadKey,
                        attempt);

                    return ActionAdmission.Refuse(
                        AdmissionRefusal.Contention,
                        $"admission aborted by Postgres {attempt} times (SQLSTATE {ex.SqlState}); refusing rather than guessing");
                }

                // Jittered, because two admissions retrying in lockstep abort each other
                // again at the same instant.
                await Task.Delay(Random.Shared.Next(20, 120) * attempt, ct);
            }
        }
    }

    private async Task<ActionAdmission> AdmitOnceAsync(
        AgentAction action,
        PolicyOptions policy,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var workloadKey = action.Target.WorkloadKey;

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // Take the per-workload lock FIRST. ON CONFLICT DO UPDATE locks the row for the
        // rest of the transaction, so every other admission for this workload queues here
        // rather than racing the counts below.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO workload_action_locks (workload_key, updated_at)
             VALUES ({workloadKey}, {now})
             ON CONFLICT (workload_key) DO UPDATE SET updated_at = {now}
             """,
            ct);

        var modeRow = await db.AgentModeRows
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == AgentModeRow.SingletonId, ct);

        // A missing row is an unreadable kill switch, and an unreadable kill switch reads
        // as Observe. Failing the other way makes a truncated database an autonomy upgrade.
        //
        // A row that is present and unlatched is SILENT rather than declaring its mode column.
        // The mode is a Helm value; this arm exists to restrain, and its restraint is the
        // latch checked immediately below. Declaring the column here would clamp every
        // admission to Observe forever, because the migration seeds that column to Observe.
        var databaseArm = modeRow switch
        {
            null => ModeArm.Unreadable(KillSwitch.DatabaseArm, "the agent_mode row is missing"),
            { RunawayLatched: true } => ModeArm.Declaring(
                KillSwitch.DatabaseArm,
                AgentMode.Observe,
                $"runaway latch: {modeRow.LatchReason ?? "unknown reason"}"),
            _ => ModeArm.Silent(KillSwitch.DatabaseArm),
        };

        // The row is read inside the transaction because that is the only arm that can be
        // raced by a concurrent admission. The env and ConfigMap arms are in-memory and a
        // file read, so folding them in here costs the transaction nothing while making the
        // two arms an operator can actually reach at 3am bind the executor rather than only
        // the investigation loop.
        var resolution = ModeResolver.Resolve([.. killSwitch.ExternalArms, databaseArm]);
        var mode = resolution.Effective;

        if (modeRow?.RunawayLatched == true)
        {
            return await RefuseAsync(
                tx, action, AdmissionRefusal.KillSwitch, null, ct,
                $"runaway latch set: {modeRow.LatchReason ?? "unknown reason"}; a human must re-arm");
        }

        if (mode is AgentMode.Off or AgentMode.Observe)
        {
            return await RefuseAsync(
                tx, action, AdmissionRefusal.KillSwitch, null, ct,
                $"agent mode is {mode}, bound by {resolution.DecidedBy}; no action may execute "
                + $"[{string.Join("; ", resolution.Arms.Select(a => a.Describe()))}]");
        }

        var incident = await db.Incidents
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == action.IncidentId, ct);

        if (incident is null)
        {
            return await RefuseAsync(
                tx, action, AdmissionRefusal.UnknownIncident, null, ct,
                $"incident {action.IncidentId} does not exist; an action must belong to one");
        }

        if (incident.QuarantinedUntil is { } until && until > now)
        {
            return await RefuseAsync(
                tx, action, AdmissionRefusal.Quarantined, null, ct,
                $"workload quarantined until {until:O} by the oscillation detector");
        }

        var budget = await ReadBudgetAsync(action, mode, now, ct);
        var isRollback = action.IsRollbackOf is not null;

        if (!isRollback)
        {
            if (budget.ActionsOnIncident >= policy.MaxActionsPerIncident)
            {
                return await RefuseAsync(
                    tx, action, AdmissionRefusal.IncidentBudget, budget, ct,
                    $"incident already has {budget.ActionsOnIncident} actions (max {policy.MaxActionsPerIncident})");
            }

            if (budget.LastActionOnWorkloadAt is { } last && now - last < policy.WorkloadCooldown)
            {
                return await RefuseAsync(
                    tx, action, AdmissionRefusal.WorkloadCooldown, budget, ct,
                    $"workload {workloadKey} acted on {(now - last).TotalSeconds:F0}s ago; cooldown is {policy.WorkloadCooldown.TotalSeconds:F0}s");
            }

            if (budget.ActionsOnWorkloadLastHour >= policy.MaxActionsPerWorkloadPerHour)
            {
                return await RefuseAsync(
                    tx, action, AdmissionRefusal.WorkloadBudget, budget, ct,
                    $"workload {workloadKey} has {budget.ActionsOnWorkloadLastHour} actions in the last hour (max {policy.MaxActionsPerWorkloadPerHour})");
            }

            if (budget.ActionsClusterWideLastHour >= policy.MaxActionsPerHour)
            {
                return await RefuseAsync(
                    tx, action, AdmissionRefusal.HourlyBudget, budget, ct,
                    $"{budget.ActionsClusterWideLastHour} actions cluster-wide in the last hour (max {policy.MaxActionsPerHour})");
            }

            if (budget.ActionsClusterWideLastDay >= policy.MaxActionsPerDay)
            {
                return await RefuseAsync(
                    tx, action, AdmissionRefusal.DailyBudget, budget, ct,
                    $"{budget.ActionsClusterWideLastDay} actions cluster-wide in the last day (max {policy.MaxActionsPerDay})");
            }
        }

        // Stamped here when the caller left it unset, because every budget window above is
        // counted over approved_at. Counting only executed actions would let an unbounded
        // number of admitted-but-not-yet-executed actions through - the exact race this
        // transaction exists to close.
        action.ApprovedAt ??= now;
        action.ModeAtExecution = mode;
        action.DryRun = action.DryRun || mode == AgentMode.DryRun;

        db.AgentActions.Add(action);

        string[] reasons = isRollback
            ? ["rollback: budget and cooldown bypassed by design", $"mode {mode}"]
            :
            [
                $"mode {mode}",
                $"incident {budget.ActionsOnIncident}/{policy.MaxActionsPerIncident}",
                $"workload/hour {budget.ActionsOnWorkloadLastHour}/{policy.MaxActionsPerWorkloadPerHour}",
                $"cluster/hour {budget.ActionsClusterWideLastHour}/{policy.MaxActionsPerHour}",
                $"cluster/day {budget.ActionsClusterWideLastDay}/{policy.MaxActionsPerDay}",
            ];

        // Enlisted, not appended: the action row and the record of why it was allowed have
        // to reach the database in the same commit or neither does.
        audit.Enlist(BuildAuditEvent(action, "action.admitted", now, reasons, budget));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ActionAdmission.Admit(budget, reasons);
    }

    /// <inheritdoc />
    public Task<ActionBudgetSnapshot> ReadBudgetAsync(
        Guid incidentId, TargetRef target, AgentMode mode, CancellationToken ct) =>
        ReadBudgetAsync(incidentId, target, mode, clock.UtcNow, ct);

    private Task<ActionBudgetSnapshot> ReadBudgetAsync(
        AgentAction action,
        AgentMode mode,
        DateTimeOffset now,
        CancellationToken ct) =>
        ReadBudgetAsync(action.IncidentId, action.Target, mode, now, ct);

    /// <remarks>
    /// One query set, two callers: admission runs it inside the serializable transaction and
    /// the policy pre-check runs it outside. Keeping them on one method is the point - two
    /// copies of "what counts against the budget" would drift, and the copy that drifted would
    /// be the one nobody was watching.
    /// </remarks>
    private async Task<ActionBudgetSnapshot> ReadBudgetAsync(
        Guid incidentId,
        TargetRef target,
        AgentMode mode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);

        var admitted = db.AgentActions.Where(a => AdmittedStates.Contains(a.State));

        // Rollbacks are excluded from every count for the same reason they bypass the
        // gates: undoing damage must not be rationed by the damage.
        var counted = admitted.Where(a => a.IsRollbackOf == null);

        var workload = WorkloadQuery.ForWorkload(counted, target);

        return new ActionBudgetSnapshot
        {
            Mode = mode,
            ActionsOnIncident = await counted.CountAsync(a => a.IncidentId == incidentId, ct),
            ActionsOnWorkloadLastHour = await workload.CountAsync(a => a.ApprovedAt >= hourAgo, ct),
            ActionsClusterWideLastHour = await counted.CountAsync(a => a.ApprovedAt >= hourAgo, ct),
            ActionsClusterWideLastDay = await counted.CountAsync(a => a.ApprovedAt >= dayAgo, ct),
            LastActionOnWorkloadAt = await workload.MaxAsync(a => (DateTimeOffset?)a.ApprovedAt, ct),
        };
    }

    /// <summary>
    /// Rolls the admission transaction back, then records the refusal outside it. Outside,
    /// because the refusal must survive the rollback - an audit trail that only remembers
    /// the times the agent was allowed to act is not an audit trail.
    /// </summary>
    private async Task<ActionAdmission> RefuseAsync(
        IDbContextTransaction tx,
        AgentAction action,
        AdmissionRefusal refusal,
        ActionBudgetSnapshot? budget,
        CancellationToken ct,
        params string[] reasons)
    {
        DetachStaged(action);

        await tx.RollbackAsync(ct);

        var auditEvent = BuildAuditEvent(action, "action.refused", clock.UtcNow, reasons, budget);
        auditEvent.Summary = $"{action.Type} on {action.Target} refused: {refusal}";

        await audit.AppendAsync(auditEvent, ct);

        logger.LogInformation(
            "Refused {ActionType} on {Workload}: {Refusal} - {Reasons}",
            action.Type,
            action.Target.WorkloadKey,
            refusal,
            string.Join("; ", reasons));

        return ActionAdmission.Refuse(refusal, budget, reasons);
    }

    private AuditEvent BuildAuditEvent(
        AgentAction action,
        string type,
        DateTimeOffset at,
        IReadOnlyList<string> reasons,
        ActionBudgetSnapshot? budget) =>
        new()
        {
            At = at,
            Type = type,
            IncidentId = action.IncidentId,
            ActionId = action.Id,
            Actor = action.ApprovedBy ?? "hephaisto/auto",
            Summary = $"{action.Type} on {action.Target}",
            Detail = JsonSerializer.Serialize(new
            {
                action = action.Type.ToString(),
                target = action.Target.WorkloadKey,
                risk = action.Risk.ToString(),
                dryRun = action.DryRun,
                isRollback = action.IsRollbackOf is not null,
                reasons,
                budget,
            }),
        };

    /// <summary>
    /// Un-stages everything this admission attempt put in the change tracker. Both halves
    /// matter on a retry: the action would be inserted twice, and so would the audit row
    /// enlisted beside it - which would leave two records claiming the same action was
    /// admitted once.
    /// </summary>
    private void DetachStaged(AgentAction action)
    {
        var entry = db.Entry(action);

        if (entry.State != EntityState.Detached)
        {
            entry.State = EntityState.Detached;
        }

        foreach (var staged in db.ChangeTracker.Entries<AuditEvent>()
                     .Where(e => e.State == EntityState.Added && e.Entity.ActionId == action.Id)
                     .ToList())
        {
            staged.State = EntityState.Detached;
        }
    }
}
