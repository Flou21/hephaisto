using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The outbox is the only retry authority - ServiceDefaults applies the standard resilience
/// handler to every factory-built client, so a channel that did not opt out would retry inside
/// the handler and again out here, multiplying the attempts.
/// </summary>
public sealed class NotificationBackoffTests
{
    [Fact]
    public void TheFirstRetryWaitsTheConfiguredBase()
    {
        NotificationBackoff.Delay(1, GivenNotifications.Options(), jitter: 1.0)
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void ItDoubles(int attempt, int expectedSeconds)
    {
        NotificationBackoff.Delay(attempt, GivenNotifications.Options(), jitter: 1.0)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ItStopsDoublingAtTheCeiling()
    {
        // A long outage should keep retrying steadily rather than drifting out to never.
        NotificationBackoff.Delay(12, GivenNotifications.Options(), jitter: 1.0)
            .Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void AVeryLateAttemptDoesNotOverflow()
    {
        NotificationBackoff.Delay(int.MaxValue, GivenNotifications.Options(), jitter: 1.0)
            .Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void JitterOnlyEverShortensIntoTheTopHalf()
    {
        // Spreading retries across a shared outage is worth doing; letting one arrive sooner
        // than the schedule says is not.
        var options = GivenNotifications.Options();

        NotificationBackoff.Delay(1, options, jitter: 0).Should().Be(TimeSpan.FromSeconds(15));
        NotificationBackoff.Delay(1, options, jitter: 0.5).Should().Be(TimeSpan.FromSeconds(22.5));
        NotificationBackoff.Delay(1, options, jitter: 1).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(7.0)]
    [InlineData(double.NaN)]
    public void JitterOutOfRangeIsClamped_NotRejected(double jitter)
    {
        // A caller feeding this from a random source must not be able to fail a delivery with a
        // rounding error.
        var delay = NotificationBackoff.Delay(1, GivenNotifications.Options(), jitter);

        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(15));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AttemptsRunOutAtTheConfiguredMaximum()
    {
        var options = GivenNotifications.Options();

        NotificationBackoff.HasAttemptsLeft(7, options).Should().BeTrue();
        NotificationBackoff.HasAttemptsLeft(8, options).Should().BeFalse();
    }
}
