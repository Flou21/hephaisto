using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The notifier inherits none of ingest's dedup, flap suppression or storm breaker, so a storm
/// that opens forty incidents would otherwise produce forty pages - the notifier amplifying the
/// exact event it exists to report.
/// </summary>
public sealed class NotificationRateLimitTests
{
    private static readonly DateTimeOffset Now = GivenNotifications.Now;

    [Fact]
    public void NothingSentYet_GoesOut()
    {
        var status = NotificationRateLimit.Evaluate("ns/Deployment/api", null, 0, Now, GivenNotifications.Options());

        status.IsSuppressed.Should().BeFalse();
        status.Exceeded.Should().Be(NotificationLimit.None);
    }

    [Fact]
    public void TheFirstMessageForAKey_AlwaysGoesOut()
    {
        // A cooldown that could swallow the opening message would be a worse failure than the
        // storm it prevents: nobody would ever learn the problem existed.
        NotificationRateLimit.Evaluate("ns/Deployment/api", null, 9, Now, GivenNotifications.Options())
            .IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void ASecondMessageInsideTheCooldown_IsSuppressed()
    {
        var status = NotificationRateLimit.Evaluate(
            "ns/Deployment/api",
            Now.AddMinutes(-4),
            0,
            Now,
            GivenNotifications.Options());

        status.Exceeded.Should().Be(NotificationLimit.CorrelationCooldown);
        status.Reason.Should().Contain("ns/Deployment/api");
    }

    [Fact]
    public void OnceTheCooldownHasPassed_ItGoesOutAgain()
    {
        NotificationRateLimit.Evaluate(
                "ns/Deployment/api",
                Now.AddMinutes(-15),
                0,
                Now,
                GivenNotifications.Options())
            .IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void AnAgentEventSkipsTheCooldown()
    {
        // ModeChanged carries no correlation key. Autonomy coming back is not something to
        // rate-limit against an unrelated incident that happened to share a quiet minute.
        NotificationRateLimit.Evaluate(string.Empty, Now.AddSeconds(-1), 0, Now, GivenNotifications.Options())
            .IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void TheChannelCapIsReachedAtTheLimit_NotPastIt()
    {
        // The count is deliveries already made, so the one being judged is the one that would
        // take the total past the cap.
        NotificationRateLimit.Evaluate("k", null, 10, Now, GivenNotifications.Options())
            .Exceeded.Should().Be(NotificationLimit.ChannelHour);
        NotificationRateLimit.Evaluate("k", null, 9, Now, GivenNotifications.Options())
            .IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void TheNarrowerLimitIsReported()
    {
        // "You were told about this workload four minutes ago" is more useful than "the channel
        // is full" - the same ordering ActionBudget uses, for the same reason.
        NotificationRateLimit.Evaluate("k", Now.AddMinutes(-1), 99, Now, GivenNotifications.Options())
            .Exceeded.Should().Be(NotificationLimit.CorrelationCooldown);
    }

    [Fact]
    public void TheReasonIsPopulatedEvenWhenNothingIsExceeded()
    {
        // So a status page can render "4 of 10 sent this hour" from the same call the dispatcher
        // makes, rather than a second implementation that drifts.
        var status = NotificationRateLimit.Evaluate("k", null, 4, Now, GivenNotifications.Options());

        status.Reason.Should().Contain("4/10");
        status.Used.Should().Be(4);
        status.Limit.Should().Be(10);
    }
}
