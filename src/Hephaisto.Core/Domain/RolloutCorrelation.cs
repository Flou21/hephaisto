using System.Globalization;

namespace Hephaisto.Core.Domain;

/// <summary>
/// A rollout that happened close enough to an incident to be worth stating as a fact.
/// </summary>
/// <remarks>
/// <para>
/// The roadmap calls this "change correlation" and phrases it as the sentence it is meant to
/// produce: <i>"this started 4 minutes after the rollout of x:sha"</i>. The narrow version is
/// the one worth building - surface the rollout as a <b>fact in the incident card</b> rather
/// than only as a tool the model may or may not think to call.
/// </para>
/// <para>
/// <b>Why a fact and not a tool.</b> <c>get_rollout_history</c> has existed since v0.2.0 and the
/// runbooks tell the model to reach for it. But backlog #74 established that step budget is the
/// binding constraint on accuracy, and #88 found nine runs that ended on a budget before the
/// planner ran at all. A fact given for free is a step not spent, and this is the fact that
/// separates a rollback that is a reasoned response from one that is a guess.
/// </para>
/// <para>
/// <b>Only when it is recent.</b> A fact present on every incident is noise, and noise in a
/// prompt is worse than absence because it is paid for on every turn. This is emitted only when
/// the rollout preceded the incident inside <see cref="RelevanceWindow"/>; an incident on a
/// Deployment that has been serving the same revision for a week gets nothing, which is correct
/// - "nothing changed recently" is not evidence the model needs handed to it, and the model can
/// still call the tool.
/// </para>
/// </remarks>
public sealed record RolloutCorrelation
{
    /// <summary>
    /// How long after a rollout an incident still counts as possibly caused by it.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than <c>PolicyOptions.RollbackFreshRevisionWindow</c>, and the
    /// difference is the point: this decides whether the model is TOLD about a rollout, and that
    /// gate decides whether it may act on one unattended. Telling it about a rollout it may not
    /// roll back is fine and often correct - the right answer may be to escalate naming the
    /// revision. Acting on one nobody mentioned is not.
    /// </remarks>
    public static readonly TimeSpan RelevanceWindow = TimeSpan.FromHours(2);

    public required long Revision { get; init; }

    public required TimeSpan IncidentOpenedAfter { get; init; }

    /// <summary>How long the revision before this one stayed live, when there was one.</summary>
    public TimeSpan? PreviousRevisionLastedFor { get; init; }

    /// <summary>The container images the current revision runs, for naming the change.</summary>
    public string? Images { get; init; }

    /// <summary>
    /// The sentence the incident card carries.
    /// </summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();

        sb.Append(CultureInfo.InvariantCulture,
            $"revision {Revision} rolled out {Humanise(IncidentOpenedAfter)} before this incident opened");

        if (Images is { Length: > 0 } images)
        {
            sb.Append(CultureInfo.InvariantCulture, $", running `{images}`");
        }

        sb.Append('.');

        if (PreviousRevisionLastedFor is { } previous)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" The previous revision had been live for {Humanise(previous)}.");
        }

        return sb.ToString();
    }

    private static string Humanise(TimeSpan span) => span switch
    {
        { TotalSeconds: < 90 } => $"{(int)span.TotalSeconds}s",
        { TotalMinutes: < 90 } => $"{(int)span.TotalMinutes}m",
        { TotalHours: < 48 } => $"{(int)span.TotalHours}h",
        _ => $"{(int)span.TotalDays}d",
    };
}
