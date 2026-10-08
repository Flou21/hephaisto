using Microsoft.Extensions.Options;
using Hephaisto.Agent.GitHub;
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
    IOptionsMonitor<GitHubOptions> github,
    IIncidentNotifier live,
    IClock clock,
    ILogger<CodeFixNotifier> logger)
{
    /// <summary>The correlation key of a work item's code-fix events: its own, per work item.</summary>
    public static string WorkItemKey(Guid workItemId) => $"workitem/{workItemId:N}/codefix";

    /// <summary>
    /// The same three moments for an attempt that is for a work item (v0.14.0): a plan waits, a
    /// pull request is open, or it ended without one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no incident, and the snapshot does not invent one: no incident id, no kind, no
    /// namespace, no cluster, no labels, and the severity left at its zero. So a route scoped by a
    /// namespace, a cluster, a kind or a label does not own it, a route with a minimum severity
    /// above Info does not want it, and a fallback route - which is for incidents nobody else
    /// owns - is not asked. What takes it is an unscoped route that lists the event.
    /// </para>
    /// <para>
    /// The title is the issue's, somebody else's words; <paramref name="reason"/> is Hephaisto's
    /// own sentence here, and it names the issue by its reference and says where a plan is
    /// answered. The key is the work item's own, so every work item is told about once per event
    /// whatever the cooldown.
    /// </para>
    /// </remarks>
    public void Enlist(NotificationEvent kind, CodeFixAttempt attempt, WorkItem item, string? reason)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(item);

        var issue = $"{item.Repository}#{item.Number}";

        var snapshot = new NotificationSnapshot
        {
            Event = kind,
            CorrelationKey = WorkItemKey(item.Id),
            Title = item.Title,
            Summary = attempt.Summary,
            Reason = WorkItemText(kind, issue, attempt, reason, github.CurrentValue.Approvers.Count > 0),
            ExternalUrl = attempt.PrUrl,
            Repository = attempt.RepositoryUrl,
            CodeFixAttemptId = attempt.Id,
            WorkItemId = item.Id,
            Issue = issue,
            IssueUrl = item.Url,
            At = clock.UtcNow,
        };

        var (deliveries, _) = NotificationEnqueue.For(snapshot, options.CurrentValue, clock.UtcNow);

        if (deliveries.Count == 0)
        {
            logger.LogDebug("No route takes {Event} for a work item; its issue and the console are where it shows.", kind);
            return;
        }

        db.NotificationDeliveries.AddRange(deliveries);
    }

    /// <summary>
    /// What a person is told about a work item's attempt. Hephaisto's words around a reference:
    /// nothing of the issue's text, and of the model's only what <paramref name="reason"/>
    /// carries for an attempt that failed.
    /// </summary>
    internal static string WorkItemText(NotificationEvent kind, string issue, CodeFixAttempt attempt, string? reason, bool commentsAreRead) =>
        kind switch
        {
            NotificationEvent.CodeFixPlanReady => commentsAreRead
                ? $"A plan for {issue} is waiting for an answer. An approver answers on the issue - /approve, or /reject and a reason, "
                    + "as the first line of a comment - or in the console."
                : $"A plan for {issue} is waiting for an answer. It is answered in the console: this install reads no comments, "
                    + "because GitHub:Approvers is empty.",
            NotificationEvent.CodeFixPrOpened =>
                $"A draft pull request for {issue} is open{(attempt.PrUrl is { Length: > 0 } url ? $": {url}" : string.Empty)}. "
                + "A person reviews and merges it; merging closes the issue.",
            _ => $"The code fix for {issue} ended without a pull request"
                + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"),
        };

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
