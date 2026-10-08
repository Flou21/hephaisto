using Microsoft.Extensions.Options;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.CodeFix;

/// <summary>
/// Stages code-fix notifications into the outbox, and publishes the console's live event.
/// </summary>
/// <remarks>
/// Enlisted, never sent: the delivery rows join the caller's next <c>SaveChanges</c>, so a plan the
/// database never recorded cannot be announced, and one it did record cannot go unannounced. The
/// correlation key is the incident's with <c>/codefix</c> appended, so the escalation that preceded
/// the plan by a minute does not swallow it under the outbound cooldown.
/// </remarks>
public sealed class CodeFixNotifier(
    HephaistoDbContext db,
    IOptionsMonitor<NotificationOptions> options,
    IIncidentNotifier live,
    IClock clock,
    ILogger<CodeFixNotifier> logger)
{
    public void Enlist(NotificationEvent kind, CodeFixAttempt attempt, Incident incident, string? reason)
    {
        var snapshot = new NotificationSnapshot
        {
            Event = kind,
            IncidentId = incident.Id,
            CorrelationKey = incident.CorrelationKey + "/codefix",
            Title = incident.Title,
            Kind = incident.Kind,
            Severity = incident.Severity,
            State = incident.State,
            EscalationReason = incident.EscalationReason,
            Namespace = incident.Target.Namespace,
            Target = $"{incident.Target.Namespace}/{incident.Target.Kind}/{incident.Target.Name}",
            Summary = attempt.Summary,
            Reason = reason,
            ExternalUrl = attempt.PrUrl,
            Repository = attempt.RepositoryUrl,
            CodeFixAttemptId = attempt.Id,
            At = clock.UtcNow,
        };

        var (deliveries, _) = NotificationEnqueue.For(snapshot, options.CurrentValue, clock.UtcNow);

        if (deliveries.Count == 0)
        {
            logger.LogDebug("No route takes {Event}; the console is the only place it shows.", kind);
            return;
        }

        db.NotificationDeliveries.AddRange(deliveries);
    }

    /// <summary>Call after the save: a live event about an uncommitted change would be a lie.</summary>
    /// <remarks>
    /// An attempt for a work item has no incident, and the event is published all the same with
    /// no incident's id: the code-fixes page and the counts in the navigation refresh on the
    /// kind alone, and no incident page takes an id that is nobody's for its own.
    /// </remarks>
    public void Publish(CodeFixAttempt attempt, string? detail = null) =>
        live.Publish(new IncidentLiveEvent
        {
            IncidentId = attempt.IncidentId ?? Guid.Empty,
            Kind = IncidentLiveEventKind.CodeFixChanged,
            Detail = detail ?? attempt.State.ToString(),
        });
}
