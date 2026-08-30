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
