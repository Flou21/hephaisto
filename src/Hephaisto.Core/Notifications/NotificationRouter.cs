namespace Hephaisto.Core.Notifications;

/// <summary>
/// Which channels an event goes to, and one thing worth knowing when the answer is "none".
/// </summary>
/// <param name="Channels">
/// Distinct channel names, in the order the routes declared them. Two routes naming one channel
/// produce one entry.
/// </param>
/// <param name="SuppressedByUnknownNamespace">
/// True when a route matched on event and severity and was rejected <b>only</b> because the
/// snapshot carries no namespace. This is not a theoretical case: metric-derived alerts arrive
/// with an empty namespace whenever the rule labels it something ingest does not read
/// (backlog #33), and the visible symptom would otherwise be an escalation that silently
/// reaches nobody while the routing table looks correct. Worth a loud log and a metric, which is
/// why it is returned rather than inferred.
/// </param>
public readonly record struct RoutingResult(
    IReadOnlyList<string> Channels,
    bool SuppressedByUnknownNamespace)
{
    public bool Any => Channels.Count > 0;
}

/// <summary>
/// The routing table, as a pure function over a snapshot and a set of rules.
/// </summary>
/// <remarks>
/// <para>
/// Pure for the same reason <c>ActionBudget</c> is: the UI has to be able to answer "who would
/// be told if this escalated" with the identical arithmetic the dispatcher uses. A second
/// implementation would drift, and the drift would surface as somebody being told they are on
/// the rota for a namespace they never receive anything from.
/// </para>
/// <para>
/// Evaluated at <b>enqueue</b>, inside the transaction that writes the state change. A later
/// edit to the routing table therefore does not retarget rows already queued - what was decided
/// is what was recorded, the same property the audit trail rests on.
/// </para>
/// </remarks>
public static class NotificationRouter
{
    public static RoutingResult Match(NotificationSnapshot snapshot, IReadOnlyList<NotificationRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(routes);

        // Never routed. It exists so a default-constructed row cannot claim to be an escalation,
        // and letting it match would defeat the point of giving it the zero value.
        if (snapshot.Event is NotificationEvent.Unspecified)
        {
            return new RoutingResult([], false);
        }

        var channels = new List<string>();
        var blockedByUnknownNamespace = false;

        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Channel))
            {
                continue;
            }

            if (!route.Events.Contains(snapshot.Event))
            {
                continue;
            }

            if (snapshot.Severity < route.MinSeverity)
            {
                continue;
            }

            if (route.Namespaces.Count > 0)
            {
                // A namespace-scoped route cannot carry ModeChanged or PolicyChanged: those are
                // about the agent, not a workload, and there is nothing to match them against.
                // That is a correct exclusion rather than a missing namespace, so it does not
                // set the flag.
                if (string.IsNullOrWhiteSpace(snapshot.Namespace))
                {
                    if (snapshot.IncidentId is not null)
                    {
                        blockedByUnknownNamespace = true;
                    }

                    continue;
                }

                if (!route.Namespaces.Contains(snapshot.Namespace, StringComparer.Ordinal))
                {
                    continue;
                }
            }

            if (!channels.Contains(route.Channel, StringComparer.Ordinal))
            {
                channels.Add(route.Channel);
            }
        }

        // Only interesting when nothing matched. If some other route delivered the message
        // anyway, the empty namespace cost nothing and reporting it would be noise.
        return new RoutingResult(channels, blockedByUnknownNamespace && channels.Count == 0);
    }
}
