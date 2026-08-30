using Hephaisto.Core.Policy;

namespace Hephaisto.Tests;

/// <summary>
/// The freeze window, which the policy engine has had a gate for since the MVP and no input to.
/// </summary>
/// <remarks>
/// Pure and exhaustively testable for the same reason the rest of the policy layer is: it is a
/// safety control, and a control that freezes the agent for 22 hours because someone read a
/// wrapping window backwards is worse than no control.
/// </remarks>
public sealed class MaintenanceWindowTests
{
    private static DateTimeOffset At(DayOfWeek day, int hour, int minute = 0)
    {
        // 2026-08-31 is a Monday, so this indexes cleanly off it.
        var monday = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var offset = ((int)day - (int)DayOfWeek.Monday + 7) % 7;

        return monday.AddDays(offset).AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void A_daily_window_applies_every_day()
    {
        var window = new MaintenanceWindow { Start = "01:00", End = "03:00" };

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            window.Contains(At(day, 2)).Should().BeTrue();
            window.Contains(At(day, 4)).Should().BeFalse();
        }
    }

    [Fact]
    public void The_start_is_inclusive_and_the_end_is_exclusive()
    {
        // So two adjacent windows do not overlap by a minute, and "01:00-03:00" does not
        // quietly mean 121 minutes.
        var window = new MaintenanceWindow { Start = "01:00", End = "03:00" };

        window.Contains(At(DayOfWeek.Monday, 1)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Monday, 2, 59)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Monday, 3)).Should().BeFalse();
        window.Contains(At(DayOfWeek.Monday, 0, 59)).Should().BeFalse();
    }

    [Fact]
    public void A_window_limited_to_days_applies_only_on_them()
    {
        var window = new MaintenanceWindow
        {
            Days = [DayOfWeek.Friday],
            Start = "14:00",
            End = "18:00",
        };

        window.Contains(At(DayOfWeek.Friday, 15)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Thursday, 15)).Should().BeFalse();
        window.Contains(At(DayOfWeek.Saturday, 15)).Should().BeFalse();
    }

    [Fact]
    public void A_window_that_wraps_midnight_is_short_rather_than_enormous()
    {
        // The one that matters. Read the other way, 22:00-02:00 is a 22-hour freeze - the
        // agent would be off for most of every day and it would look like a quiet cluster.
        var window = new MaintenanceWindow { Start = "22:00", End = "02:00" };

        window.Contains(At(DayOfWeek.Monday, 23)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Tuesday, 1)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Tuesday, 12)).Should().BeFalse();
        window.Contains(At(DayOfWeek.Monday, 12)).Should().BeFalse();
    }

    [Fact]
    public void A_wrapping_window_belongs_to_the_day_it_started_on()
    {
        // A Saturday 22:00-02:00 freeze still applies at 01:00 on Sunday. Matching the end
        // against Sunday instead would end the freeze at midnight, halfway through.
        var window = new MaintenanceWindow
        {
            Days = [DayOfWeek.Saturday],
            Start = "22:00",
            End = "02:00",
        };

        window.Contains(At(DayOfWeek.Saturday, 23)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Sunday, 1)).Should().BeTrue();
        window.Contains(At(DayOfWeek.Sunday, 23)).Should().BeFalse();
        window.Contains(At(DayOfWeek.Friday, 23)).Should().BeFalse();
    }

    [Fact]
    public void Times_are_UTC_whatever_offset_the_caller_carries()
    {
        // A window in local time moves twice a year, in opposite directions, and the first
        // anyone hears of it is an action taken during a change freeze.
        var window = new MaintenanceWindow { Start = "01:00", End = "03:00" };

        // 02:00 UTC, expressed as 04:00+02:00.
        window.Contains(new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.FromHours(2)))
            .Should().BeTrue();

        // 04:00 UTC, expressed as 02:00-02:00.
        window.Contains(new DateTimeOffset(2026, 8, 31, 2, 0, 0, TimeSpan.FromHours(-2)))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("", "03:00")]
    [InlineData("1:00", "03:00")]
    [InlineData("25:00", "03:00")]
    [InlineData("01:00", "nonsense")]
    public void An_unparseable_window_is_invalid_and_never_freezes_anything(string start, string end)
    {
        // Not a freeze: a typo that silently froze the agent would be indistinguishable from a
        // healthy quiet cluster. The gatherer logs it at Error instead.
        var window = new MaintenanceWindow { Start = start, End = end };

        window.IsValid.Should().BeFalse();
        window.Contains(At(DayOfWeek.Monday, 2)).Should().BeFalse();
    }

    [Fact]
    public void A_window_describes_itself_for_the_denial_reason()
    {
        new MaintenanceWindow { Start = "01:00", End = "03:00", Description = "nightly batch" }
            .Describe().Should().Be("nightly batch");

        new MaintenanceWindow { Days = [DayOfWeek.Friday], Start = "14:00", End = "18:00" }
            .Describe().Should().Contain("Friday").And.Contain("14:00-18:00");
    }
}
