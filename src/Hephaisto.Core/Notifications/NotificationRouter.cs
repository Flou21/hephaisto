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
/// <summary>One channel a message goes out on, and to whom.</summary>
/// <param name="Recipients">The recipients the matching routes named, distinct.</param>
/// <param name="UsesChannelRecipients">
/// Whether a matching route named nobody, and so asked for the channel's own list as well.
/// </param>
/// <param name="Routes">The names of the routes that matched, for the delivery's record.</param>
public sealed record ChannelMatch(
    string Channel,
    IReadOnlyList<string> Recipients,
    bool UsesChannelRecipients,
    IReadOnlyList<string> Routes);

public readonly record struct RoutingResult(
    IReadOnlyList<ChannelMatch> Matches,
    bool SuppressedByUnknownNamespace)
{
    public IReadOnlyList<string> Channels => [.. Matches.Select(m => m.Channel)];

    public bool Any => Matches.Count > 0;
}

public static class NotificationRouter
{
    /// <summary>
    /// Which channels, and whom on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two questions, in order (#141). <b>Who owns the incident</b>: every route whose scope -
    /// namespaces, label matchers, clusters, kinds - holds, and an unscoped route owns everything.
    /// A fallback route owns an incident only when no scoped route does, which is what keeps a
    /// table scoped by label from telling nobody about an alert nobody labelled. <b>Then what they
    /// want</b>: the event and the severity.
    /// </para>
    /// <para>
    /// Additive: two routes on one channel are one delivery, to both routes' recipients.
    /// </para>
    /// </remarks>
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

        var owners = Owners(snapshot, routes, out var blockedByUnknownNamespace);

        var matches = owners
            .Where(r => r.Events.Contains(snapshot.Event) && snapshot.Severity >= r.MinSeverity)
            .GroupBy(r => r.Channel, StringComparer.Ordinal)
            .Select(g => new ChannelMatch(
                g.Key,
                [.. g.SelectMany(r => r.Recipients).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
                g.Any(r => r.Recipients.Count == 0),
                [.. g.Select(r => r.Name ?? r.Channel).Distinct(StringComparer.Ordinal)]))
            .ToList();

        // Only interesting when nothing matched. If some other route delivered the message
        // anyway, the empty namespace cost nothing and reporting it would be noise.
        return new RoutingResult(matches, blockedByUnknownNamespace && matches.Count == 0);
    }

    /// <summary>
    /// The routes that own the incident, whatever the event: the half of <see cref="Match"/> the
    /// escalation steps use (#142), because a step belongs to the route that owns the incident,
    /// not to the event that started its clock.
    /// </summary>
    public static IReadOnlyList<NotificationRoute> Owners(NotificationSnapshot snapshot, IReadOnlyList<NotificationRoute> routes) =>
        Owners(snapshot, routes, out _);

    private static List<NotificationRoute> Owners(
        NotificationSnapshot snapshot,
        IReadOnlyList<NotificationRoute> routes,
        out bool blockedByUnknownNamespace)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(routes);

        blockedByUnknownNamespace = false;
        var owners = new List<NotificationRoute>();
        var scopedOwner = false;

        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Channel) || route.Fallback)
            {
                continue;
            }

            if (!Owns(route, snapshot, ref blockedByUnknownNamespace))
            {
                continue;
            }

            owners.Add(route);
            scopedOwner |= route.IsScoped;
        }

        // An event about the agent has no incident to own; the fallback is for incidents.
        if (!scopedOwner && snapshot.IncidentId is not null)
        {
            owners.AddRange(routes.Where(r => r.Fallback && !string.IsNullOrWhiteSpace(r.Channel)));
        }

        return owners;
    }

    private static bool Owns(NotificationRoute route, NotificationSnapshot snapshot, ref bool blockedByUnknownNamespace)
    {
        if (route.Namespaces.Count > 0)
        {
            // A namespace-scoped route cannot carry ModeChanged or PolicyChanged: those are
            // about the agent, not a workload, and there is nothing to match them against.
            // That is a correct exclusion rather than a missing namespace, so it does not set
            // the flag.
            if (string.IsNullOrWhiteSpace(snapshot.Namespace))
            {
                if (snapshot.IncidentId is not null)
                {
                    blockedByUnknownNamespace = true;
                }

                return false;
            }

            if (!route.Namespaces.Contains(snapshot.Namespace, StringComparer.Ordinal))
            {
                return false;
            }
        }

        if (route.Clusters.Count > 0 && !route.Clusters.Contains(snapshot.Cluster, StringComparer.Ordinal))
        {
            return false;
        }

        if (route.Kinds.Count > 0 && (snapshot.IncidentId is null || !route.Kinds.Contains(snapshot.Kind)))
        {
            return false;
        }

        foreach (var matcher in route.Matchers)
        {
            if (!snapshot.Labels.TryGetValue(matcher.Label, out var value)
                || !matcher.Values.Contains(value, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
