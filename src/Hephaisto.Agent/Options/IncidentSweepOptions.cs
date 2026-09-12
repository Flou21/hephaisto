namespace Hephaisto.Agent.Options;

/// <summary>
/// How long an incident may sit unanswered before the agent stops counting it as live.
/// </summary>
/// <remarks>
/// <para>
/// Both windows exist because three of the ten <c>IncidentState</c> members had no producer
/// (backlog #109, #44): nothing ever called <c>Expire()</c>, so <c>Expired</c> was unreachable,
/// and nothing swept <c>AwaitingApproval</c>, so <c>ApprovalTimedOut</c> had no producer either.
/// The open-incident count could therefore only ever go up.
/// </para>
/// <para>
/// Deliberately generous defaults. An incident that vanishes from the console because a timer
/// fired is worse than one that lingers: the second is untidy, the first loses a real cluster
/// problem nobody dealt with. These are a backstop against unbounded growth, not a workflow.
/// </para>
/// </remarks>
public sealed class IncidentSweepOptions
{
    public const string SectionName = "IncidentSweep";

    /// <summary>
    /// How often to look. Cheap - two indexed queries - so this is about responsiveness rather
    /// than cost.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long since the last SIGNAL before an unanswered incident expires.
    /// </summary>
    /// <remarks>
    /// Measured from <c>LastSignalAt</c> rather than <c>OpenedAt</c>, which matters: a fault
    /// still firing every thirty seconds keeps its incident alive however old the incident is,
    /// and that is the correct behaviour. Only silence expires one.
    /// </remarks>
    public TimeSpan ExpireAfter { get; set; } = TimeSpan.FromDays(3);

    /// <summary>
    /// How long an action may wait for a human before the incident escalates with
    /// <c>ApprovalTimedOut</c> and the action row is marked Expired.
    /// </summary>
    /// <remarks>
    /// Shorter than <see cref="ExpireAfter"/> on purpose. A proposal that has been sitting for a
    /// day is stale advice: the cluster has moved, and executing it then would act on facts that
    /// are no longer true. Escalating says "nobody answered in time" while leaving the incident
    /// open for a person, which is different from expiring it.
    /// </remarks>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Ceiling per pass, so a first run against a long-neglected install does one bounded batch
    /// rather than a single transaction over thousands of rows.
    /// </summary>
    public int MaxPerPass { get; set; } = 200;

    /// <summary>
    /// Off by default. Turning it on changes what the console shows without anyone asking, so it
    /// is an explicit choice - and on the install that motivated it, seeing the backlog is the
    /// point before draining it.
    /// </summary>
    public bool Enabled { get; set; }
}
