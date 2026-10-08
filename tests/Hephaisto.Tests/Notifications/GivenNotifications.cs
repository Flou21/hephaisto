using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Permissive baselines, so each test reads as the single thing it changes.
/// </summary>
internal static class GivenNotifications
{
    /// <summary>Fixed, so a test never depends on the wall clock.</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public static NotificationOptions Options() => new()
    {
        BaseUrl = "https://hephaisto.example",
        MaxPerChannelPerHour = 10,
        CorrelationCooldown = TimeSpan.FromMinutes(15),
        MaxAttempts = 8,
        FirstRetryDelay = TimeSpan.FromSeconds(30),
        MaxRetryDelay = TimeSpan.FromMinutes(30),
    };

    public static NotificationSnapshot Escalation(
        string ns = "hephaisto-chaos",
        Severity severity = Severity.Critical,
        NotificationEvent @event = NotificationEvent.IncidentEscalated) => new()
        {
            Event = @event,
            IncidentId = Guid.CreateVersion7(),
            CorrelationKey = $"{ns}/Deployment/api",
            Title = "api is crash looping",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = severity,
            State = IncidentState.Escalated,
            PreviousState = IncidentState.Investigating,
            EscalationReason = EscalationReason.NoPlanProduced,
            Namespace = ns,
            Target = $"{ns}/Deployment/api",
            At = Now,
        };

    /// <summary>An event about the agent rather than a workload: no incident, no namespace.</summary>
    public static NotificationSnapshot ModeChanged() => new()
    {
        Event = NotificationEvent.ModeChanged,
        Title = "runaway latch cleared",
        Severity = Severity.Critical,
        At = Now,
    };

    /// <summary>
    /// A code-fix event about a work item (v0.14.0): a GitHub issue handed to Hephaisto. No
    /// incident, and so no kind, no namespace, no labels, and the severity at its zero.
    /// </summary>
    public static NotificationSnapshot WorkItemPlan(NotificationEvent @event = NotificationEvent.CodeFixPlanReady) => new()
    {
        Event = @event,
        CorrelationKey = "workitem/0199a1b2c3d47e5f8a9b0c1d2e3f4a5b/codefix",
        Title = "The order total is null for an empty cart",
        Summary = "Guard the null total; see [the docs](https://evil.example) and @octocat",
        Reason = "A plan for octo/shop#12 is waiting for an answer.",
        Repository = "https://github.com/octo/shop",
        CodeFixAttemptId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"),
        WorkItemId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"),
        Issue = "octo/shop#12",
        IssueUrl = "https://github.com/octo/shop/issues/12",
        At = Now,
    };

    public static NotificationRoute Route(
        string channel = "teams",
        Severity minSeverity = Severity.Info,
        NotificationEvent[]? events = null,
        string[]? namespaces = null) => new()
        {
            Channel = channel,
            MinSeverity = minSeverity,
            Events = [.. events ?? [NotificationEvent.IncidentEscalated]],
            Namespaces = [.. namespaces ?? []],
        };
}
