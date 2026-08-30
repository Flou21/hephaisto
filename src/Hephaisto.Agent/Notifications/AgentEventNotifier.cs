using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// Enqueues the two notifications that are not incident state transitions.
/// </summary>
/// <remarks>
/// <para>
/// Everything about an incident is picked up by <see cref="NotificationEnqueueInterceptor"/>,
/// which cannot be forgotten because it watches the transition log rather than the call sites.
/// These two have no transition to watch: the agent's mode and its policy are properties of the
/// process, not of any incident.
/// </para>
/// <para>
/// They are also the two most security-relevant events the system produces. Autonomy coming back
/// after a runaway latch is the moment most worth being able to attribute, and a policy that
/// changed without anybody saying so is indistinguishable from an attack. Being told about them
/// is the point; auditing them was never enough, because an audit row is only read by somebody
/// who already suspects something.
/// </para>
/// </remarks>
public interface IAgentEventNotifier
{
    /// <summary>
    /// Stages the deliveries into the ambient DbContext <b>without saving</b>, so they commit
    /// with whatever the caller is already writing - the audit row, and the latch itself.
    /// </summary>
    void Enlist(NotificationEvent kind, Severity severity, string title, string? detail, DateTimeOffset at);
}

public sealed class AgentEventNotifier(
    INotificationOutbox outbox,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<AgentEventNotifier> logger) : IAgentEventNotifier
{
    public void Enlist(
        NotificationEvent kind,
        Severity severity,
        string title,
        string? detail,
        DateTimeOffset at)
    {
        var o = options.CurrentValue;

        if (o.Routes.Count == 0)
        {
            return;
        }

        var snapshot = new NotificationSnapshot
        {
            Event = kind,
            Title = title,
            Severity = severity,
            Reason = detail,
            At = at,

            // No incident, no workload, and therefore no correlation key - which is what makes
            // these skip the outbound cooldown. "The agent is autonomous again" must not be
            // held back because an unrelated incident happened to page the same channel a
            // minute earlier.
            CorrelationKey = string.Empty,
        };

        var (deliveries, _) = NotificationEnqueue.For(snapshot, o, at);

        foreach (var delivery in deliveries)
        {
            outbox.Enlist(delivery);
        }

        if (deliveries.Count == 0)
        {
            // Worth a line, because the two events here are exactly the ones somebody would
            // assume were covered by "notifications are on".
            logger.LogInformation(
                "{Event} matched no notification route, so nobody is being told about it.",
                kind);
        }
    }
}
