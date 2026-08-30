using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// Writes the outbox row for a state transition, in the transaction that performs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so that enqueueing cannot be forgotten.</b> The obvious design is a call at
/// each of the ten or so places an incident commits a transition, and the obvious failure of
/// that design is already in this codebase: <c>IncidentTriage</c> reaches <c>Escalated</c> twice
/// - the self-signal arm and the storm circuit breaker - and publishes no live event at all, so
/// those two escalations are invisible to an open browser until a fallback poll. Nobody noticed,
/// because nothing asserted it. A future eleventh call site would go the same way.
/// </para>
/// <para>
/// <c>IncidentStateMachine.Transition</c> already appends an <see cref="IncidentEvent"/> on
/// EVERY edge, without exception - that is the log the audit trail is built from. So watching
/// for those rows gives the property directly: an incident cannot reach <c>Escalated</c> without
/// a delivery being written by the same <c>SaveChangesAsync</c>, and there is no ordering, no
/// second commit, and no window in which a pod can die between the two.
/// </para>
/// <para>
/// <b>Three rules, because this runs on every single save in the process.</b> It must be cheap -
/// a stock install has no routes and leaves after one field read. It must not query - everything
/// it needs comes out of the change graph already in memory. And it must not throw: a
/// notification defect that could roll back an incident write would be a far worse bug than the
/// silence it was built to fix, so the whole body is wrapped and a failure costs a log line.
/// </para>
/// </remarks>
public sealed class NotificationEnqueueInterceptor(
    IOptionsMonitor<NotificationOptions> options,
    IClock clock,
    ILogger<NotificationEnqueueInterceptor> logger) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Enqueue(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Enqueue(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Enqueue(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            var o = options.CurrentValue;

            // The shipped default, and the reason this is affordable on every save: no routes
            // means nowhere to send anything, so there is nothing to compute.
            if (o.Routes.Count == 0)
            {
                return;
            }

            // Materialised before anything is added, because adding to the change tracker
            // while enumerating it is how you get an InvalidOperationException on the commit
            // path of an incident.
            var transitions = context.ChangeTracker
                .Entries<IncidentEvent>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();

            if (transitions.Count == 0)
            {
                return;
            }

            var incidents = context.ChangeTracker
                .Entries<Incident>()
                .Select(e => e.Entity)
                .ToList();

            var now = clock.UtcNow;
            var queued = new List<NotificationDelivery>();

            foreach (var transition in transitions)
            {
                var incident = transition.Incident
                    ?? incidents.Find(i => i.Id == transition.IncidentId);

                var kind = NotificationEnqueue.Classify(
                    transition.To,
                    incident?.EscalationReason ?? EscalationReason.None);

                if (kind is null)
                {
                    continue;
                }

                var snapshot = NotificationEnqueue.Snapshot(kind.Value, transition, incident);
                var (deliveries, unknownNamespace) = NotificationEnqueue.For(snapshot, o, now);

                if (unknownNamespace)
                {
                    // backlog #33 reaching the surface. Every namespace-scoped route rejected
                    // this because ingest did not recover the namespace from the alert labels,
                    // so the escalation is about to reach nobody while the routing table looks
                    // entirely correct. Loud, by name, with the incident id.
                    logger.LogWarning(
                        "Incident {IncidentId} reached {State} and matched no notification route "
                            + "because it carries no namespace. Every namespace-scoped route "
                            + "rejected it, so nobody is being told.",
                        transition.IncidentId,
                        transition.To);
                }

                queued.AddRange(deliveries);
            }

            if (queued.Count > 0)
            {
                context.AddRange(queued);
            }
        }
        catch (Exception ex)
        {
            // Deliberately swallowed. An incident that is written without its notification is a
            // bad day; an incident that fails to be written BECAUSE of its notification is a
            // worse one, and this code runs inside every save in the process.
            logger.LogError(
                ex,
                "Could not enqueue notifications for this transaction; the write itself is unaffected.");
        }
    }
}
