using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Core.Safety;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Builds the history <see cref="OscillationDetector"/> needs, and writes its verdict.
/// </summary>
/// <remarks>
/// <para>
/// The detector itself is pure, fully unit-tested, and until now had never been called from
/// anywhere. What was missing was not the logic but its input: it takes a list of
/// <see cref="ActionOutcome"/> - what was done, when, and whether the incident came back -
/// and nothing in the system assembled one.
/// </para>
/// <para>
/// It is the concrete answer to "it restarts a pod that crashes again forever". Every other
/// control caps a RATE: the cooldown spaces actions out, the budgets cap how many, and a
/// workload that fails every fifteen minutes stays comfortably inside all of them while
/// achieving nothing. Oscillation detection is the only one that notices the agent is not
/// helping.
/// </para>
/// </remarks>
public sealed class OscillationGuard(
    HephaistoDbContext db,
    IOptionsMonitor<PolicyOptions> policyOptions,
    IClock clock,
    ILogger<OscillationGuard> logger)
{
    /// <summary>
    /// Re-evaluates a workload after an action on it did not stick, and quarantines it if the
    /// agent is going in circles.
    /// </summary>
    public async Task<OscillationVerdict?> EvaluateAsync(TargetRef target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var now = clock.UtcNow;
        var history = await HistoryAsync(target, now, ct).ConfigureAwait(false);

        if (history.Count == 0)
        {
            return null;
        }

        var verdict = OscillationDetector.Evaluate(history, now, policyOptions.CurrentValue);

        if (!verdict.Quarantine || verdict.Until is not { } until)
        {
            return verdict;
        }

        var row = await db.WorkloadActionLocks
            .FirstOrDefaultAsync(w => w.WorkloadKey == target.WorkloadKey, ct);

        if (row is null)
        {
            row = new WorkloadActionLock { WorkloadKey = target.WorkloadKey, UpdatedAt = now };
            db.WorkloadActionLocks.Add(row);
        }

        // Never shorten an existing quarantine. Two detections in the same window should not
        // let the second one release the workload earlier than the first decided.
        if (row.QuarantinedUntil is null || row.QuarantinedUntil < until)
        {
            row.QuarantinedUntil = until;
            row.QuarantineReason = verdict.Reason;
        }

        row.UpdatedAt = now;

        logger.LogWarning(
            "Workload {Workload} quarantined until {Until}: {Reason}",
            target.WorkloadKey, until, verdict.Reason);

        return verdict;
    }

    /// <summary>
    /// What has been done to this workload, and whether the problem came back each time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "The incident reopened" is the load-bearing part, and it is not literally the Reopen
    /// edge - in production an incident that recurs is a NEW incident, because fingerprints
    /// are per-signal and dedup opens a fresh one once the old one has closed. So the question
    /// asked here is the one that matters operationally: after this action, did another
    /// incident open on the same workload? That is what "it did not hold" looks like in the
    /// data.
    /// </para>
    /// <para>
    /// An action whose own verification failed counts too, without waiting for a new incident.
    /// The evidence is already in: the agent looked, and the fix had not worked.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ActionOutcome>> HistoryAsync(
        TargetRef target, DateTimeOffset now, CancellationToken ct)
    {
        // The detector's own window is two hours; a day of history lets its shrinking-MTBF arm
        // see a pattern that a two-hour view would clip.
        var since = now.AddDays(-1);

        var actions = await WorkloadQuery
            .ForWorkload(db.AgentActions.AsNoTracking(), target)
            .Where(a => a.ExecutedAt != null && a.ExecutedAt >= since && a.IsRollbackOf == null)
            .OrderBy(a => a.ExecutedAt)
            .Select(a => new { a.ExecutedAt, a.Type, a.State })
            .ToListAsync(ct);

        if (actions.Count == 0)
        {
            return [];
        }

        var incidentOpenings = await WorkloadQuery
            .ForWorkload(db.Incidents.AsNoTracking(), target)
            .Where(i => i.OpenedAt >= since)
            .Select(i => i.OpenedAt)
            .ToListAsync(ct);

        return
        [
            .. actions.Select(a => new ActionOutcome(
                a.ExecutedAt!.Value,
                a.Type,
                IncidentReopened:
                    a.State is ActionState.Failed or ActionState.RolledBack ||
                    incidentOpenings.Any(o => o > a.ExecutedAt!.Value))),
        ];
    }
}
