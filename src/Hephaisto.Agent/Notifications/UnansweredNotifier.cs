using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// When nobody answers, tells somebody else (#142).
/// </summary>
/// <remarks>
/// <para>
/// Until v0.10.0 nothing sent a second message. The only thing that looked at an unanswered
/// incident was the sweeper, which expires it after three days of silence - the opposite of
/// escalating. As the only incident system, an alert that the first person does not see has to
/// reach a second one.
/// </para>
/// <para>
/// Every tick it reads the open, unacknowledged incidents, asks the router which routes own each,
/// and asks <see cref="NotificationSteps.Due"/> which of their steps are due. A step's delivery is
/// written with an audit row in one save; the dispatcher sends it like any other. Nothing is
/// scheduled and nothing needs cancelling: an acknowledgement stops the steps because the next
/// tick reads it, and a reopen restarts them because the clock is <c>ReopenedAt</c>.
/// </para>
/// </remarks>
public sealed class UnansweredNotifier(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<UnansweredNotifier> logger) : BackgroundService
{
    /// <summary>Per tick. A storm of unanswered incidents is a storm the hourly cap already guards.</summary>
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.CurrentValue.UnansweredInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad tick must not end the steps for every incident after it.
                logger.LogError(ex, "The escalation-step check failed; it will try again.");
            }
        }
    }

    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;

        if (!o.Routes.Any(r => r.Steps.Count > 0))
        {
            return 0;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

        var now = clock.UtcNow;

        var incidents = await db.Incidents
            .Where(i => HephaistoDbContext.OpenStates.Contains(i.State) && i.AcknowledgedAt == null)
            .OrderBy(i => i.OpenedAt)
            .Take(Batch)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var enlisted = 0;

        foreach (var incident in incidents)
        {
            var since = incident.ReopenedAt ?? incident.OpenedAt;
            var transition = new IncidentEvent
            {
                IncidentId = incident.Id,
                From = incident.State,
                To = incident.State,
                Reason = "nobody has acknowledged it",
                At = now,
            };

            var snapshot = NotificationEnqueue.Snapshot(NotificationEvent.IncidentUnanswered, transition, incident);
            var owners = NotificationRouter.Owners(snapshot, o.Routes).Where(r => r.Steps.Count > 0).ToList();

            if (owners.Count == 0)
            {
                continue;
            }

            var fired = (await db.NotificationDeliveries
                    .AsNoTracking()
                    .Where(d => d.IncidentId == incident.Id
                        && d.Event == NotificationEvent.IncidentUnanswered
                        && d.Step != null
                        && d.CreatedAt >= since)
                    .Select(d => new { d.Routes, d.Step })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .Where(d => d.Routes.Count > 0)
                .Select(d => NotificationSteps.Key(d.Routes[0], d.Step!.Value))
                .ToHashSet(StringComparer.Ordinal);

            var due = NotificationSteps.Due(
                new UnansweredFacts(since, incident.IsOpen, incident.AcknowledgedAt is not null, incident.Severity, incident.AssignedTo, fired),
                owners,
                now);

            foreach (var step in due)
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Event = NotificationEvent.IncidentUnanswered,
                    IncidentId = incident.Id,
                    Channel = step.Channel,
                    Recipients = [.. step.Recipients],
                    UsesChannelRecipients = step.UsesChannelRecipients,
                    Routes = [step.Route],
                    Step = step.Index,
                    CorrelationKey = snapshot.CorrelationKey,
                    Status = DeliveryStatus.Pending,
                    Snapshot = snapshot,
                    CreatedAt = now,
                    NextAttemptAt = now,
                });

                audit.Enlist(new AuditEvent
                {
                    At = now,
                    Type = "incident.unanswered",
                    IncidentId = incident.Id,
                    Actor = IncidentStateMachine.SystemActor,
                    Summary = $"step {step.Index + 1} of route {step.Route}: unacknowledged for "
                        + $"{(int)(now - since).TotalMinutes} min, telling {(step.Recipients.Count > 0 ? string.Join(", ", step.Recipients) : "the channel's recipients")}",
                });

                enlisted++;
            }
        }

        if (enlisted > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation("Enlisted {Count} escalation step(s) for unacknowledged incidents.", enlisted);
        }

        return enlisted;
    }
}
