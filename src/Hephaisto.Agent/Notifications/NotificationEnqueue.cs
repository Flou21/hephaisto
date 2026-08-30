using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// Turns one snapshot into the outbox rows the routing table says it deserves.
/// </summary>
/// <remarks>
/// Shared by the two ways an event gets here - the interceptor that watches state transitions,
/// and the explicit calls for the two events that are not transitions - so both produce
/// identical rows and neither can drift into its own idea of what routing means.
/// </remarks>
public static class NotificationEnqueue
{
    /// <returns>
    /// The rows to add, and whether routing was defeated by a missing namespace - which is
    /// worth a warning at the call site rather than a silent empty list.
    /// </returns>
    public static (List<NotificationDelivery> Deliveries, bool SuppressedByUnknownNamespace) For(
        NotificationSnapshot snapshot,
        NotificationOptions options,
        DateTimeOffset now)
    {
        var routing = NotificationRouter.Match(snapshot, options.Routes);
        var deliveries = new List<NotificationDelivery>(routing.Channels.Count);

        foreach (var channel in routing.Channels)
        {
            deliveries.Add(new NotificationDelivery
            {
                Event = snapshot.Event,
                IncidentId = snapshot.IncidentId,
                Channel = channel,
                CorrelationKey = snapshot.CorrelationKey,
                Status = DeliveryStatus.Pending,
                Snapshot = snapshot,
                CreatedAt = now,

                // Due immediately. "Queued" and "waiting out a backoff" are then one query
                // rather than two, which is what keeps the dispatcher's poll a single index
                // seek.
                NextAttemptAt = now,
            });
        }

        return (deliveries, routing.SuppressedByUnknownNamespace);
    }

    /// <summary>
    /// Which notification, if any, a transition into <paramref name="to"/> deserves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most transitions deserve none. Triaging, Investigating, Acting and Verifying are the
    /// agent working, and a channel that reported them would be one people mute - at which
    /// point the escalation gets muted with them.
    /// </para>
    /// <para>
    /// <b>VerificationFailed is not a state</b>, which is why the escalation reason is
    /// consulted. <c>VerificationScheduler.GiveUpAsync</c> ends at <c>Escalated</c> whether the
    /// action was rolled back, the workload was quarantined, or the check simply never passed -
    /// and "the agent tried and was wrong" is a different thing to learn than "the agent
    /// declined to try". Reading the state alone would collapse the two.
    /// </para>
    /// </remarks>
    public static NotificationEvent? Classify(IncidentState to, EscalationReason reason) => to switch
    {
        IncidentState.Escalated when reason
            is EscalationReason.VerificationFailed
            or EscalationReason.RollbackPerformed
            or EscalationReason.Quarantined => NotificationEvent.VerificationFailed,
        IncidentState.Escalated => NotificationEvent.IncidentEscalated,
        IncidentState.AwaitingApproval => NotificationEvent.ApprovalRequired,
        IncidentState.Resolved => NotificationEvent.IncidentResolved,
        _ => null,
    };

    /// <summary>
    /// Builds the frozen facts from the transition and the incident it belongs to.
    /// </summary>
    /// <param name="incident">
    /// Null when the incident is not in the same change graph. The message is then thinner - an
    /// id, a state and a link - which is deliberately still worth sending: a page that says
    /// "incident X escalated, look here" is not as good as a full one and is enormously better
    /// than silence, which is the thing being fixed.
    /// </param>
    public static NotificationSnapshot Snapshot(
        NotificationEvent kind,
        IncidentEvent transition,
        Incident? incident)
    {
        return new NotificationSnapshot
        {
            Event = kind,
            IncidentId = transition.IncidentId,
            CorrelationKey = incident?.CorrelationKey ?? string.Empty,
            Title = incident?.Title ?? string.Empty,
            Kind = incident?.Kind ?? SignalKind.Unknown,
            Severity = incident?.Severity ?? Severity.Warning,
            State = transition.To,
            PreviousState = transition.From,
            EscalationReason = incident?.EscalationReason ?? EscalationReason.None,
            Namespace = incident?.Target?.Namespace ?? string.Empty,
            Target = Describe(incident),
            Summary = incident?.Resolution,
            Reason = transition.Reason,
            At = transition.At,
        };
    }

    private static string Describe(Incident? incident) =>
        incident?.Target is null || string.IsNullOrWhiteSpace(incident.Target.Name)
            ? string.Empty
            : $"{incident.Target.Namespace}/{incident.Target.Kind}/{incident.Target.Name}";
}
