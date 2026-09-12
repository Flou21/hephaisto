using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Telemetry;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Gives <c>Expire()</c> and <c>ApprovalTimedOut</c> the producers they never had.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three of the ten <see cref="IncidentState"/> members had no producer</b> (backlog #109,
/// #44). <c>IncidentStateMachine.Expire()</c> had zero callers, so <see cref="IncidentState.Expired"/>
/// was unreachable. Nothing swept <see cref="IncidentState.AwaitingApproval"/>, so
/// <see cref="EscalationReason.ApprovalTimedOut"/> had no producer. And
/// <see cref="IncidentState.Escalated"/> counts as open, which on an Observe install is where
/// every incident ends up - so the open count climbed for as long as the process ran and nothing
/// could bring it down.
/// </para>
/// <para>
/// <b>This does not close anything.</b> Closing asserts a person dealt with it; that is
/// <c>Close()</c> and it needs a human. Expiring asserts the opposite - nobody answered and the
/// signal stopped - which is the true statement about an incident that has gone quiet and been
/// ignored. Writing the first when the second happened would put a fact that never occurred into
/// the table the audit story rests on, which is also why the migration that added
/// <see cref="IncidentState.Closed"/> backfills nothing.
/// </para>
/// <para>
/// <b>An acknowledged incident is never expired.</b> Somebody said they were on it, so removing
/// it from their list on a timer is the one behaviour that would make acknowledging worse than
/// useless.
/// </para>
/// <para>
/// Off by default. Turning it on changes what the console shows without anyone asking, and on the
/// install that motivated it, seeing the backlog is the point before draining it.
/// </para>
/// </remarks>
public sealed class IncidentSweeper(
    IServiceScopeFactory scopes,
    IOptionsMonitor<IncidentSweepOptions> options,
    HephaistoMetrics metrics,
    IClock clock,
    ILogger<IncidentSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            logger.LogInformation(
                "Incident sweeping is OFF. Unanswered incidents stay open forever and the open "
                + "count only rises; set {Section}:Enabled to change that.",
                IncidentSweepOptions.SectionName);
            return;
        }

        var o = options.CurrentValue;

        logger.LogInformation(
            "Incident sweeping is on: expiring unanswered incidents after {ExpireAfter} of "
            + "silence, timing out approvals after {ApprovalTimeout}, every {Interval}.",
            o.ExpireAfter,
            o.ApprovalTimeout,
            o.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that throws must not take the loop down with it: the next pass would
                // otherwise never run and the count would silently resume climbing.
                logger.LogError(ex, "Incident sweep failed; will try again next interval.");
            }

            try
            {
                await Task.Delay(options.CurrentValue.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = clock.UtcNow;

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<HephaistoDbContext>();
        var audit = sp.GetRequiredService<IAuditRepository>();
        var machine = sp.GetRequiredService<IncidentStateMachine>();

        // Approvals first. An incident whose approval has timed out escalates, and an escalated
        // incident is then a candidate for expiry in the same pass if it is also ancient - which
        // is the correct order: it timed out, then nobody came.
        var timedOut = await TimeOutApprovalsAsync(db, audit, machine, now.Subtract(o.ApprovalTimeout), o.MaxPerPass, now, ct);
        var expired = await ExpireUnansweredAsync(db, audit, machine, now.Subtract(o.ExpireAfter), o.MaxPerPass, now, ct);

        if (timedOut + expired > 0)
        {
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Incident sweep: {TimedOut} approval(s) timed out, {Expired} incident(s) expired.",
                timedOut,
                expired);
        }
    }

    /// <summary>
    /// <see cref="IncidentState.AwaitingApproval"/> past its window: escalate the incident and
    /// mark the proposal <see cref="ActionState.Expired"/>.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Escalating without expiring the action leaves a proposal that a human
    /// could still approve hours later, executing advice computed against a cluster that has
    /// since moved. Expiring the action without escalating leaves the incident awaiting an
    /// approval that can no longer be given.
    /// </remarks>
    private async Task<int> TimeOutApprovalsAsync(
        HephaistoDbContext db,
        IAuditRepository audit,
        IncidentStateMachine machine,
        DateTimeOffset cutoff,
        int max,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var stale = await ApprovalTimeoutCandidates(db, cutoff, max)
            .Include(i => i.Events)
            .Include(i => i.Actions)
            .ToListAsync(ct);

        foreach (var incident in stale)
        {
            var eventsBefore = incident.Events.Count;

            machine.Escalate(
                incident,
                EscalationReason.ApprovalTimedOut,
                $"no answer within {options.CurrentValue.ApprovalTimeout}");

            // Ordering, for the reason TrackNewIncidentChildren exists: the event carries a
            // client-assigned key, so EF would emit an UPDATE matching nothing.
            db.TrackNewIncidentChildren(incident, eventsBefore);

            foreach (var action in incident.Actions.Where(a => a.State == ActionState.AwaitingApproval))
            {
                action.State = ActionState.Expired;
                action.Error = "The approval window closed before anyone answered.";
            }

            audit.Enlist(new AuditEvent
            {
                At = now,
                Type = "incident.approval_timed_out",
                IncidentId = incident.Id,
                Actor = IncidentStateMachine.SystemActor,
                Summary = $"escalated after {options.CurrentValue.ApprovalTimeout} with no answer",
            });

            metrics.IncidentClosed(incident.Kind, incident.Severity, incident.State, now - incident.OpenedAt);
        }

        return stale.Count;
    }

    /// <summary>
    /// Open and quiet for long enough that nobody is coming: expire it.
    /// </summary>
    /// <remarks>
    /// <c>AwaitingApproval</c> is excluded - that one belongs to the approval window above, and
    /// expiring it here would skip the escalation that tells somebody it went unanswered.
    /// Acknowledged incidents are excluded because a person said they were on it.
    /// </remarks>
    private async Task<int> ExpireUnansweredAsync(
        HephaistoDbContext db,
        IAuditRepository audit,
        IncidentStateMachine machine,
        DateTimeOffset cutoff,
        int max,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var stale = await ExpiryCandidates(db, cutoff, max)
            .Include(i => i.Events)
            .ToListAsync(ct);

        foreach (var incident in stale)
        {
            var from = incident.State;
            var eventsBefore = incident.Events.Count;

            machine.Expire(incident, $"no signal and nobody answered for {options.CurrentValue.ExpireAfter}");
            db.TrackNewIncidentChildren(incident, eventsBefore);

            audit.Enlist(new AuditEvent
            {
                At = now,
                Type = "incident.expired",
                IncidentId = incident.Id,
                Actor = IncidentStateMachine.SystemActor,
                Summary = $"expired from {from} after {options.CurrentValue.ExpireAfter} of silence",
            });

            metrics.IncidentClosed(incident.Kind, incident.Severity, IncidentState.Expired, now - incident.OpenedAt);
        }

        return stale.Count;
    }

    // ------------------------------------------------------------------------------------
    // The two predicates, exposed so the tests exercise THESE rather than a copy of them.
    //
    // They were duplicated into the test file first, which was wrong: a duplicated predicate
    // verifies the author's understanding, and when the two drift the test keeps passing while
    // the sweeper picks the wrong rows. Every mistake available in this component is a mistake
    // about which rows it touches, so the rows have to be chosen by the code under test.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Open, quiet for long enough that nobody is coming, and nobody has claimed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>LastSignalAt</c>, never <c>OpenedAt</c>: a fault still firing keeps its incident alive
    /// however old the incident is. Reversing that would expire live problems, which is the worst
    /// thing this component could do.
    /// </para>
    /// <para>
    /// <c>AwaitingApproval</c> is excluded because it belongs to the approval window - expiring it
    /// here would skip the escalation that tells somebody the approval went unanswered, so the
    /// incident would vanish instead of asking louder. <c>AcknowledgedBy == null</c> because a
    /// person said they were on it.
    /// </para>
    /// </remarks>
    internal static IQueryable<Incident> ExpiryCandidates(
        HephaistoDbContext db,
        DateTimeOffset cutoff,
        int max) =>
        db.Incidents
            .Where(i => HephaistoDbContext.OpenStates.Contains(i.State)
                && i.State != IncidentState.AwaitingApproval
                && i.AcknowledgedBy == null
                && i.LastSignalAt <= cutoff)
            .OrderBy(i => i.LastSignalAt)
            .Take(max);

    /// <summary>Waiting on a human for longer than the approval window allows.</summary>
    internal static IQueryable<Incident> ApprovalTimeoutCandidates(
        HephaistoDbContext db,
        DateTimeOffset cutoff,
        int max) =>
        db.Incidents
            .Where(i => i.State == IncidentState.AwaitingApproval && i.LastSignalAt <= cutoff)
            .OrderBy(i => i.LastSignalAt)
            .Take(max);
}
