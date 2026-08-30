using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Web;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Runs the scheduled checks on executed actions, and decides what the answer means.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism the planning prompt described for a whole release before it existed
/// (backlog #7). It closes the loop the architecture diagram has always drawn:
/// Acting -> Verifying -> Resolved, or -> rollback -> Escalated.
/// </para>
/// <para>
/// <b>Only the verifier may grant a Resolved.</b> The state machine refuses a model identity
/// as a granter by construction; this service passes <c>hephaisto/verifier</c> and reaches it
/// only after a deterministic predicate looked at the cluster. It is also, until an operator
/// closes something by hand, the only production path to Resolved at all - which is what
/// finally gives hephaisto.incident.duration something to measure that is not an escalation.
/// </para>
/// </remarks>
public sealed class VerificationScheduler(
    IServiceScopeFactory scopes,
    ILogger<VerificationScheduler> logger) : BackgroundService
{
    /// <summary>
    /// Well under the 60-second first check, so a due verification runs close to when it was
    /// due rather than up to a poll late. One indexed query over a table with a handful of
    /// pending rows; the index (DueAt, Outcome) exists for exactly this.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once before the first delay. Verifications that came due while the process was down
        // are the ones most worth running promptly - the same reason StrandedIncidentRequeue
        // runs at startup rather than waiting for its first tick.
        await PollAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;

            var db = sp.GetRequiredService<HephaistoDbContext>();
            var clock = sp.GetRequiredService<IClock>();
            var now = clock.UtcNow;

            var due = await db.Verifications
                .Include(v => v.Action)
                .Where(v => v.Outcome == VerificationOutcome.Pending && v.DueAt <= now)
                .OrderBy(v => v.DueAt)
                .Take(20)
                .ToListAsync(ct);

            foreach (var verification in due)
            {
                await RunOneAsync(sp, verification, now, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The loop must outlive any single bad tick. A scheduler that dies leaves executed
            // actions unverified forever, which is worse than a noisy log - and it fails
            // silently, because nothing else in the system notices that checks stopped.
            logger.LogError(ex, "A verification poll failed; the scheduler continues.");
        }
    }

    private async Task RunOneAsync(
        IServiceProvider sp, Verification verification, DateTimeOffset now, CancellationToken ct)
    {
        var action = verification.Action;

        if (action is null)
        {
            verification.Outcome = VerificationOutcome.Inconclusive;
            verification.RanAt = now;
            verification.Detail = "the action this verification belongs to is gone";

            await sp.GetRequiredService<HephaistoDbContext>().SaveChangesAsync(ct);

            return;
        }

        var checks = sp.GetRequiredService<VerificationChecks>();
        var metrics = sp.GetRequiredService<HephaistoMetrics>();

        using var activity = HephaistoMetrics.ActivitySource.StartActivity(HephaistoTelemetry.Spans.Verification);
        activity?.SetTag("action.id", action.Id);
        activity?.SetTag("action.type", action.Type.ToString());
        activity?.SetTag("verification.attempt", verification.Attempt);

        var result = await checks.RunAsync(action, ct).ConfigureAwait(false);

        verification.Outcome = result.Outcome;
        verification.RanAt = now;
        verification.Detail = result.Detail;
        verification.Checks = result.Checks is null ? null : JsonSerializer.Serialize(result.Checks, Json);

        activity?.SetTag("verification.result", result.Outcome.ToString());
        metrics.VerificationResult(result.Outcome, verification.Attempt);

        logger.LogInformation(
            "Verification {Attempt} of {Action} on {Workload}: {Outcome} - {Detail}",
            verification.Attempt, action.Type, action.Target.WorkloadKey, result.Outcome, result.Detail);

        switch (result.Outcome)
        {
            case VerificationOutcome.Passed:
                await PassAsync(sp, action, verification, result, ct).ConfigureAwait(false);
                break;

            case VerificationOutcome.Failed when verification.Attempt >= VerificationSchedule.FinalAttempt:
                await GiveUpAsync(sp, action, result, ct).ConfigureAwait(false);
                break;

            default:
                // Failed-but-not-final, or Inconclusive. Nothing is concluded from a check that
                // has not had its last word: a pod pulling an image at T+60s is not a failure,
                // and reverting on it would make the agent the cause of the next incident.
                await sp.GetRequiredService<HephaistoDbContext>().SaveChangesAsync(ct);
                break;
        }
    }

    /// <summary>The action worked. Cancel the later checks and close the incident.</summary>
    private async Task PassAsync(
        IServiceProvider sp, AgentAction action, Verification passed, CheckResult result, CancellationToken ct)
    {
        var db = sp.GetRequiredService<HephaistoDbContext>();

        action.State = ActionState.Verified;

        // The remaining attempts asked the same question and it has been answered. Leaving
        // them pending would re-open a decided matter fourteen minutes later, and a transient
        // blip at T+15m would then roll back an action that demonstrably worked.
        var later = await db.Verifications
            .Where(v => v.ActionId == action.Id
                        && v.Outcome == VerificationOutcome.Pending
                        && v.Attempt > passed.Attempt)
            .ToListAsync(ct);

        foreach (var v in later)
        {
            v.Outcome = VerificationOutcome.Inconclusive;
            v.RanAt = passed.RanAt;
            v.Detail = $"superseded: attempt {passed.Attempt} passed";
        }

        await ResolveIfSettledAsync(sp, db, action, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the incident once every action taken on it has been verified.
    /// </summary>
    /// <remarks>
    /// Every action, not this one. A plan may carry several, and closing on the first to pass
    /// would call an incident resolved while another action on it is still being judged.
    /// </remarks>
    private async Task ResolveIfSettledAsync(
        IServiceProvider sp, HephaistoDbContext db, AgentAction action, CheckResult result, CancellationToken ct)
    {
        var incident = await db.Incidents
            .Include(i => i.Events)
            .FirstOrDefaultAsync(i => i.Id == action.IncidentId, ct);

        if (incident is null || incident.State != IncidentState.Verifying)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var unsettled = await db.AgentActions
            .Where(a => a.IncidentId == incident.Id
                        && a.ExecutedAt != null
                        && a.State != ActionState.Verified
                        && a.Id != action.Id)
            .CountAsync(ct);

        if (unsettled > 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var eventsBefore = incident.Events.Count;
        var stateMachine = sp.GetRequiredService<IncidentStateMachine>();

        stateMachine.Resolve(incident, result.Detail, IncidentStateMachine.VerifierActor);

        db.TrackNewIncidentChildren(incident, eventsBefore);

        sp.GetRequiredService<IAuditRepository>().Enlist(new AuditEvent
        {
            At = incident.ResolvedAt ?? DateTimeOffset.UtcNow,
            Type = "incident.resolved",
            IncidentId = incident.Id,
            ActionId = action.Id,
            Actor = IncidentStateMachine.VerifierActor,
            Summary = "verification passed",
            Detail = result.Detail,
        });

        await db.SaveChangesAsync(ct);

        // AFTER the commit, for the same reason every other closure records here: the metric
        // reads Incident.State, and recording a closure that then failed to save would put a
        // number on the dashboard that the database disagrees with.
        sp.GetRequiredService<HephaistoMetrics>().IncidentClosed(
            incident.Kind, incident.Severity, incident.State,
            (incident.ResolvedAt ?? DateTimeOffset.UtcNow) - incident.OpenedAt);

        sp.GetRequiredService<IIncidentNotifier>().Publish(new IncidentLiveEvent
        {
            IncidentId = incident.Id,
            Kind = IncidentLiveEventKind.StateChanged,
            State = IncidentState.Resolved,
            Detail = result.Detail,
        });

        logger.LogInformation("Incident {IncidentId} resolved: {Detail}", incident.Id, result.Detail);
    }

    /// <summary>
    /// The last check failed. Revert if the action can be reverted, and tell a human either way.
    /// </summary>
    private async Task GiveUpAsync(
        IServiceProvider sp, AgentAction action, CheckResult result, CancellationToken ct)
    {
        var db = sp.GetRequiredService<HephaistoDbContext>();

        action.State = ActionState.Failed;
        action.Error = $"verification failed at attempt {VerificationSchedule.FinalAttempt}: {result.Detail}";

        // The moment the evidence arrives that the agent is not helping. Every other control
        // caps a rate - the cooldown spaces actions out, the budgets cap how many - and a
        // workload that fails every fifteen minutes stays inside all of them while achieving
        // nothing. This is the only check that notices that.
        var oscillation = await sp.GetRequiredService<OscillationGuard>()
            .EvaluateAsync(action.Target, ct)
            .ConfigureAwait(false);

        var rollback = await sp.GetRequiredService<ActionRollback>()
            .TryRevertAsync(action, ct)
            .ConfigureAwait(false);

        var incident = await db.Incidents
            .Include(i => i.Events)
            .FirstOrDefaultAsync(i => i.Id == action.IncidentId, ct);

        if (incident is null || !incident.IsOpen || incident.State is IncidentState.Escalated)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var eventsBefore = incident.Events.Count;

        var quarantined = oscillation is { Quarantine: true };

        sp.GetRequiredService<IncidentStateMachine>().Escalate(
            incident,
            quarantined ? EscalationReason.Quarantined
                : rollback.Reverted ? EscalationReason.RollbackPerformed
                : EscalationReason.VerificationFailed,
            quarantined
                ? $"{result.Detail}; {rollback.Detail}; {oscillation!.Reason}"
                : $"{result.Detail}; {rollback.Detail}");

        db.TrackNewIncidentChildren(incident, eventsBefore);

        await db.SaveChangesAsync(ct);

        sp.GetRequiredService<HephaistoMetrics>().IncidentClosed(
            incident.Kind, incident.Severity, incident.State, DateTimeOffset.UtcNow - incident.OpenedAt);

        sp.GetRequiredService<IIncidentNotifier>().Publish(new IncidentLiveEvent
        {
            IncidentId = incident.Id,
            Kind = IncidentLiveEventKind.StateChanged,
            State = incident.State,
            Detail = $"verification failed; {rollback.Detail}",
        });

        logger.LogWarning(
            "Verification of {Action} on {Workload} failed for the last time. {Rollback}",
            action.Type, action.Target.WorkloadKey, rollback.Detail);
    }
}
