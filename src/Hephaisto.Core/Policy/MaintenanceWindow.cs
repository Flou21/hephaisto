using System.Globalization;

namespace Hephaisto.Core.Policy;

/// <summary>
/// A recurring weekly window during which the agent must not act on its own.
/// </summary>
/// <remarks>
/// <para>
/// The policy engine has had a maintenance-window gate since the MVP and there was never
/// anything to fill it: no schedule, no chart value, no producer. A gate whose input is
/// hardcoded false is the mirror image of the defect this repo already wrote a rule against -
/// config that reads like configuration and behaves like a comment - and it became worth
/// fixing rather than deleting the moment fact-gathering went live, because now every OTHER
/// gate can fail and this one still cannot.
/// </para>
/// <para>
/// Weekly rather than cron. What an operator actually wants to express is "our change freeze
/// is Friday afternoon" or "the nightly batch runs 01:00-03:00", and a cron expression can say
/// far more than that while being much harder to read correctly in a values file - and this is
/// a safety control, so being obviously right matters more than being general.
/// </para>
/// <para>
/// <b>Times are UTC.</b> A window in local time would silently move twice a year, in opposite
/// directions, and the first anyone would know of it is an action taken during a freeze.
/// </para>
/// </remarks>
public sealed class MaintenanceWindow
{
    /// <summary>Days the window applies on. Empty means every day.</summary>
    public HashSet<DayOfWeek> Days { get; set; } = [];

    /// <summary>Inclusive start, UTC, as <c>HH:mm</c>.</summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>Exclusive end, UTC, as <c>HH:mm</c>. May be earlier than <see cref="Start"/>.</summary>
    public string End { get; set; } = string.Empty;

    /// <summary>Free text, so a denial can say which window stopped it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether <paramref name="at"/> falls inside this window.
    /// </summary>
    /// <remarks>
    /// A window whose end is before its start wraps midnight - "22:00-02:00" is four hours
    /// across two days, not a 22-hour window, and reading it the other way would freeze the
    /// agent for most of the day. When it wraps, <see cref="Days"/> is matched against the day
    /// the window STARTED on, so a Saturday 22:00-02:00 window still applies at 01:00 Sunday.
    /// </remarks>
    public bool Contains(DateTimeOffset at)
    {
        if (!TryParse(Start, out var start) || !TryParse(End, out var end))
        {
            // Unparseable is not "always on" and not "always off" - it is a configuration
            // error, and the caller reports it. Returning true here would let a typo freeze
            // the agent permanently; returning false silently is what this whole class exists
            // to stop. IsValid is checked at startup.
            return false;
        }

        var utc = at.ToUniversalTime();
        var time = utc.TimeOfDay;

        if (start <= end)
        {
            return Applies(utc.DayOfWeek) && time >= start && time < end;
        }

        // Wraps midnight. Either we are after the start on a matching day, or before the end
        // on the day AFTER a matching one.
        return (Applies(utc.DayOfWeek) && time >= start)
            || (Applies(Previous(utc.DayOfWeek)) && time < end);
    }

    public bool IsValid => TryParse(Start, out _) && TryParse(End, out _);

    public string Describe() =>
        Description is { Length: > 0 } d
            ? d
            : $"{(Days.Count == 0 ? "daily" : string.Join("/", Days.Order()))} {Start}-{End} UTC";

    private bool Applies(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);

    private static DayOfWeek Previous(DayOfWeek day) =>
        day == DayOfWeek.Sunday ? DayOfWeek.Saturday : day - 1;

    private static bool TryParse(string value, out TimeSpan time)
    {
        time = default;

        return !string.IsNullOrWhiteSpace(value)
            && TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time);
    }
}
