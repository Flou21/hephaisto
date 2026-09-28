using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Notifications;

/// <summary>The facts about an incident the steps are decided on.</summary>
/// <param name="Since">When it opened, or last reopened: the clock every step is measured on.</param>
/// <param name="Fired">
/// The steps already fired since then, as "route/index". A step fired if its delivery exists -
/// there is no other state to keep, and none to get out of step with the outbox.
/// </param>
public sealed record UnansweredFacts(
    DateTimeOffset Since,
    bool Open,
    bool Acknowledged,
    Severity Severity,
    string? AssignedTo,
    IReadOnlySet<string> Fired);

/// <summary>A step that is due now, and to whom.</summary>
public sealed record DueStep(
    string Route,
    int Index,
    string Channel,
    IReadOnlyList<string> Recipients,
    bool UsesChannelRecipients);

/// <summary>
/// Which escalation steps are due for an incident (#142). Pure: the caller gathers the facts and
/// writes the deliveries.
/// </summary>
/// <remarks>
/// Computed, not scheduled. Nothing stores "step 2 fires at 14:20": at each tick the steps a
/// route owns are compared with the clock, the acknowledgement and the deliveries that already
/// exist. A restart loses nothing, an acknowledgement stops the steps without anybody cancelling
/// a timer, and a reopen restarts the clock because <see cref="UnansweredFacts.Since"/> moves.
/// </remarks>
public static class NotificationSteps
{
    /// <summary>
    /// How long past its time a step may still fire. Longer than that - the agent was down, or the
    /// step was added to a route after the incident opened - and it is skipped rather than sent:
    /// "nobody answered for twenty minutes" is not news three days later, and without this an
    /// upgrade would fire every step of every old open incident at once.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    public static string Key(string route, int index) => $"{route}/{index}";

    /// <summary>The oldest clock start that can still have a step due now.</summary>
    public static DateTimeOffset OldestDue(IEnumerable<NotificationRoute> routes, DateTimeOffset now)
    {
        var longest = routes.SelectMany(r => r.Steps).Select(s => s.After).DefaultIfEmpty(TimeSpan.Zero).Max();
        return now - longest - Grace;
    }

    public static IReadOnlyList<DueStep> Due(
        UnansweredFacts facts,
        IEnumerable<NotificationRoute> owningRoutes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(owningRoutes);

        if (!facts.Open || facts.Acknowledged)
        {
            return [];
        }

        var due = new List<DueStep>();

        foreach (var route in owningRoutes)
        {
            var name = route.Name ?? route.Channel;

            for (var i = 0; i < route.Steps.Count; i++)
            {
                var step = route.Steps[i];

                var elapsed = now - facts.Since;

                if (elapsed < step.After
                    || elapsed >= step.After + Grace
                    || facts.Severity < step.MinSeverity
                    || facts.Fired.Contains(Key(name, i)))
                {
                    continue;
                }

                var recipients = new List<string>(step.Recipients);
                if (step.ToAssignee && !string.IsNullOrWhiteSpace(facts.AssignedTo))
                {
                    recipients.Add(facts.AssignedTo);
                }

                var usesRoute = step.Recipients.Count == 0 && !step.ToAssignee;
                if (usesRoute)
                {
                    recipients.AddRange(route.Recipients);
                }

                due.Add(new DueStep(
                    name,
                    i,
                    string.IsNullOrWhiteSpace(step.Channel) ? route.Channel : step.Channel,
                    [.. recipients.Distinct(StringComparer.OrdinalIgnoreCase)],
                    usesRoute && route.Recipients.Count == 0));
            }
        }

        return due;
    }
}
