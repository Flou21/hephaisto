using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;

namespace Hephaisto.Core.Safety;

/// <summary>One past action on a workload, and whether the incident came back afterwards.</summary>
/// <remarks>
/// <paramref name="IncidentReopened"/> is the whole point. An action repeated three times is
/// unremarkable if each one held; the same action repeated three times while the incident keeps
/// reopening is the agent papering over a fault it has not diagnosed.
/// </remarks>
public readonly record struct ActionOutcome(DateTimeOffset At, ActionType Type, bool IncidentReopened);

/// <summary>
/// <paramref name="Backoff"/> and <paramref name="Quarantine"/> are separate severities:
/// backoff says "slow down", quarantine says "stop and fetch a human".
/// </summary>
public sealed record OscillationVerdict(bool Quarantine, DateTimeOffset? Until, TimeSpan? Backoff, string Reason);

/// <summary>
/// Catches the failure mode that makes an autonomous remediator dangerous rather than merely
/// useless: the fix that appears to work, so it gets applied again, and again.
/// </summary>
/// <remarks>
/// <para>
/// Restarting a pod that is out of memory always "works" - the pod comes back Ready and the
/// incident resolves. Then it fills up again. Each cycle looks like a success in the metrics,
/// the mean time between failures shrinks, and nobody raises the memory limit. Two independent
/// signals are checked here: a flat repeat count with reopenings, and a shrinking MTBF, because
/// the second catches the same pathology while it is still spread over days.
/// </para>
/// <para>
/// Pure, like everything else in Core: history and <c>now</c> come in, a verdict goes out.
/// </para>
/// </remarks>
public static class OscillationDetector
{
    /// <summary>Three strikes inside two hours. Two is a coincidence; three is a pattern.</summary>
    public const int RepeatThreshold = 3;

    public static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Long enough that a human working days sees the quarantine, rather than it lapsing
    /// overnight and the agent quietly resuming the same loop before anyone looked.
    /// </summary>
    public static readonly TimeSpan QuarantineDuration = TimeSpan.FromHours(24);

    /// <summary>Backoff past this is indistinguishable from quarantine, so it stops here.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(12);

    public static OscillationVerdict Evaluate(
        IReadOnlyList<ActionOutcome> history,
        DateTimeOffset now,
        PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);

        if (history.Count == 0)
        {
            return new OscillationVerdict(false, null, null, "no prior actions on this workload");
        }

        var recent = history.Where(a => a.At > now - RepeatWindow && a.At <= now).ToArray();

        foreach (var group in recent.GroupBy(a => a.Type))
        {
            var reopening = group.Count(a => a.IncidentReopened);
            if (reopening >= RepeatThreshold)
            {
                return new OscillationVerdict(
                    Quarantine: true,
                    Until: now + QuarantineDuration,
                    Backoff: null,
                    Reason: $"{group.Key} applied {reopening} times in " +
                            $"{RepeatWindow.TotalHours:0.#}h and the incident reopened every time; " +
                            "the action is treating a symptom, not the cause");
            }
        }

        // Shrinking MTBF. Measured over the full history rather than the two-hour window,
        // because the interesting version of this failure decays over days: 8h, then 4h,
        // then 90 minutes. By the time it fits in a two-hour window it is already the case
        // above, and a human should have been told long before then.
        foreach (var group in history.GroupBy(a => a.Type))
        {
            var occurrences = group.OrderBy(a => a.At).ToArray();
            if (occurrences.Length < RepeatThreshold)
            {
                continue;
            }

            var gaps = new TimeSpan[occurrences.Length - 1];
            for (var i = 1; i < occurrences.Length; i++)
            {
                gaps[i - 1] = occurrences[i].At - occurrences[i - 1].At;
            }

            var shrinking = true;
            for (var i = 1; i < gaps.Length; i++)
            {
                if (gaps[i] >= gaps[i - 1])
                {
                    shrinking = false;
                    break;
                }
            }

            if (!shrinking || !occurrences[^1].IncidentReopened)
            {
                continue;
            }

            // Double the ordinary cooldown once per repeat. The point is not the exact number
            // but that the interval outgrows the shrinking MTBF, so the loop stops being free.
            var multiplier = Math.Pow(2, occurrences.Length - 1);
            var backoff = TimeSpan.FromTicks((long)Math.Min(
                options.WorkloadCooldown.Ticks * multiplier,
                MaxBackoff.Ticks));

            return new OscillationVerdict(
                Quarantine: false,
                Until: null,
                Backoff: backoff,
                Reason: $"time between {group.Key} actions is shrinking " +
                        $"({string.Join(" -> ", gaps.Select(g => $"{g.TotalMinutes:0}m"))}); " +
                        $"the fix is holding for less time each round, backing off to {backoff.TotalMinutes:0}m");
        }

        return new OscillationVerdict(false, null, null, "no oscillation detected");
    }
}
