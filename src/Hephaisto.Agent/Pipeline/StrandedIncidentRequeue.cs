using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Re-queues incidents left in <see cref="IncidentState.Investigating"/> by a previous
/// process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this an incident can be abandoned silently and permanently.</b> Triage sets
/// Investigating and pushes onto an in-memory queue; the queue and the running loop die with
/// the process. Anything queued or in flight at that moment keeps its Investigating state in
/// Postgres forever, and nothing ever looks at it again - no retry, no timeout, no alert. The
/// incident is real, the cluster problem is still there, and the console shows a row that
/// looks like it is being worked on.
/// </para>
/// <para>
/// A restart is not exceptional here. Every deploy is one, and this agent is a singleton with
/// <c>strategy: Recreate</c>, so there is no second replica still holding the work.
/// </para>
/// <para>
/// Bounded by <see cref="MaxRequeue"/>. Each re-queued incident costs a real LLM
/// investigation, so a process that comes up after a long outage must not spend its way
/// through a thousand-item backlog before it can look at anything current. The oldest are
/// taken first - they have been waiting longest - and whatever is left over is reported
/// rather than dropped quietly.
/// </para>
/// </remarks>
public sealed class StrandedIncidentRequeue(
    IServiceScopeFactory scopes,
    InvestigationQueue queue,
    IKillSwitch killSwitch,
    ILogger<StrandedIncidentRequeue> logger) : BackgroundService
{
    /// <summary>Half the queue's capacity, leaving room for incidents arriving now.</summary>
    public const int MaxRequeue = 16;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The queue is drained by InvestigationWorker, another hosted service. Yielding lets
        // it get to its first read before anything is pushed, so nothing blocks on a full
        // channel during startup.
        await Task.Yield();

        try
        {
            // An operator who switched the agent Off and then restarted it - which is the
            // normal way to apply a config change - must not have this sweep start sixteen
            // LLM investigations for them on the way up. Without this check, Off survives the
            // restart but the backlog it was meant to stop does not.
            var mode = await killSwitch.ResolveAsync(stoppingToken).ConfigureAwait(false);

            if (mode.Effective == AgentMode.Off)
            {
                logger.LogInformation(
                    "Agent is Off ({DecidedBy}); not re-queueing stranded incidents. They stay "
                    + "in Investigating and are swept up by a later start once it is switched on.",
                    mode.DecidedBy);

                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            var stranded = await db.Incidents
                .AsNoTracking()
                .Where(i => i.State == IncidentState.Investigating)
                .OrderBy(i => i.OpenedAt)
                .Select(i => i.Id)
                .Take(MaxRequeue + 1)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (stranded.Count == 0)
            {
                return;
            }

            var overflow = stranded.Count > MaxRequeue;
            var take = overflow ? MaxRequeue : stranded.Count;
            var queued = 0;

            for (var i = 0; i < take; i++)
            {
                if (queue.TryEnqueue(stranded[i]))
                {
                    queued++;
                }
            }

            logger.LogWarning(
                "Re-queued {Queued} incident(s) left in Investigating by a previous process. "
                + "They were abandoned mid-investigation and nothing else would ever pick "
                + "them up.",
                queued);

            if (overflow)
            {
                logger.LogWarning(
                    "More than {Max} stranded incidents exist; the rest stay in Investigating "
                    + "and will be picked up by a later restart. A backlog this size usually "
                    + "means investigations are failing rather than finishing.",
                    MaxRequeue);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Never fatal. Losing the sweep leaves incidents exactly as stranded as they
            // already were; taking the process down with it would be strictly worse.
            logger.LogError(ex, "Could not re-queue stranded incidents.");
        }
    }
}
