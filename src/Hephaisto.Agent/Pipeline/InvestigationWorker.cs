using Hephaisto.Agent.Safety;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Drains <see cref="InvestigationQueue"/> and runs investigations with bounded concurrency.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency is 2, and the number matters. Each investigation holds an LLM conversation
/// open for up to four minutes and costs real money, so unbounded parallelism turns a cluster
/// event into a simultaneous spend spike and a rate-limit wall. Two is enough that one slow
/// investigation does not block an unrelated urgent one, and small enough that a storm queues
/// visibly rather than executing invisibly.
/// </para>
/// </remarks>
public sealed class InvestigationWorker(
    InvestigationQueue queue,
    IServiceScopeFactory scopeFactory,
    IKillSwitch killSwitch,
    ILogger<InvestigationWorker> logger) : BackgroundService
{
    private const int MaxConcurrency = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var running = new List<Task>();

        await foreach (var incidentId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            // The second half of "ingest nothing, investigate nothing". The ingest gate stops
            // new work becoming queued; this stops work that was ALREADY queued when the switch
            // flipped, which is the window an operator cares about most - they hit the switch
            // precisely because something is already in flight.
            //
            // Checked before taking a concurrency slot: there is no reason to hold one to
            // decide not to run.
            var mode = await killSwitch.ResolveAsync(stoppingToken).ConfigureAwait(false);

            if (mode.Effective == AgentMode.Off)
            {
                // The incident itself is untouched and stays open and visible; only the
                // investigation is declined. It is picked up again by StrandedIncidentRequeue
                // on the next start, or by a human retry, once the agent is switched back on.
                logger.LogInformation(
                    "Agent is Off ({DecidedBy}); not investigating incident {IncidentId}. It stays open.",
                    mode.DecidedBy, incidentId);

                continue;
            }

            await slots.WaitAsync(stoppingToken).ConfigureAwait(false);

            running.Add(RunAsync(incidentId, slots, stoppingToken));
            running.RemoveAll(t => t.IsCompleted);
        }

        // Let in-flight investigations finish on shutdown. Killing one mid-loop burns the
        // tokens already spent and produces nothing - the same reasoning as the budget
        // service's "an in-flight investigation is allowed to finish".
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task RunAsync(Guid incidentId, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var investigator = scope.ServiceProvider.GetRequiredService<IIncidentInvestigator>();

            await investigator.InvestigateAsync(incidentId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            // A failed investigation is an escalation, never a crash. The incident is real
            // whether or not the model managed to say anything useful about it.
            // Name the offending entities. A bare DbUpdateConcurrencyException says only
            // "expected to affect 1 row(s), but actually affected 0" and gives no clue which
            // of the dozen entity types in an investigation save it means - which turns a
            // ten-minute fix into an afternoon of guessing.
            if (ex is Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                foreach (var entry in dbEx.Entries)
                {
                    logger.LogError(
                        "  offending entity: {Entity} state={State} key={Key}",
                        entry.Entity.GetType().Name,
                        entry.State,
                        string.Join(",", entry.Properties.Where(pr => pr.Metadata.IsPrimaryKey()).Select(pr => pr.CurrentValue)));
                }
            }

            logger.LogError(ex, "Investigation of incident {IncidentId} failed.", incidentId);
        }
        finally
        {
            slots.Release();
        }
    }
}
