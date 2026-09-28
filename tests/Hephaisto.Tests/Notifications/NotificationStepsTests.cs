using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>When nobody answers, somebody else is told - once per step, per outage (#142).</summary>
public sealed class NotificationStepsTests
{
    private static readonly DateTimeOffset Opened = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    private static NotificationRoute Route() => new()
    {
        Name = "payments",
        Channel = "teamsBot",
        Recipients = ["oncall@example.com"],
        Steps =
        [
            new NotificationStep { After = TimeSpan.FromMinutes(20), Recipients = ["lead@example.com"] },
            new NotificationStep { After = TimeSpan.FromMinutes(60), Recipients = ["head@example.com"], MinSeverity = Severity.Critical },
        ],
    };

    private static UnansweredFacts Facts(
        bool acknowledged = false,
        Severity severity = Severity.Critical,
        string[]? fired = null,
        bool open = true,
        string? assignee = null) =>
        new(Opened, open, acknowledged, severity, assignee, (fired ?? []).ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void Nothing_is_due_before_the_first_step()
    {
        NotificationSteps.Due(Facts(), [Route()], Opened.AddMinutes(19)).Should().BeEmpty();
    }

    [Fact]
    public void The_first_step_is_due_after_its_time_to_its_people()
    {
        var due = NotificationSteps.Due(Facts(), [Route()], Opened.AddMinutes(21));

        due.Should().ContainSingle().Which.Recipients.Should().Equal("lead@example.com");
        due[0].Index.Should().Be(0);
    }

    [Fact]
    public void A_step_that_fired_does_not_fire_again()
    {
        NotificationSteps.Due(Facts(fired: [NotificationSteps.Key("payments", 0)]), [Route()], Opened.AddMinutes(30))
            .Should().BeEmpty();
    }

    [Fact]
    public void An_acknowledgement_stops_every_step()
    {
        NotificationSteps.Due(Facts(acknowledged: true), [Route()], Opened.AddHours(5)).Should().BeEmpty();
    }

    [Fact]
    public void An_ended_incident_has_no_steps()
    {
        NotificationSteps.Due(Facts(open: false), [Route()], Opened.AddHours(5)).Should().BeEmpty();
    }

    [Fact]
    public void A_step_can_want_a_higher_severity()
    {
        NotificationSteps.Due(Facts(severity: Severity.Warning, fired: [NotificationSteps.Key("payments", 0)]), [Route()], Opened.AddMinutes(70))
            .Should().BeEmpty();
        NotificationSteps.Due(Facts(fired: [NotificationSteps.Key("payments", 0)]), [Route()], Opened.AddMinutes(70))
            .Should().ContainSingle().Which.Recipients.Should().Equal("head@example.com");
    }

    [Fact]
    public void A_step_long_past_its_time_is_skipped_not_sent()
    {
        // The agent was down, or the step was added to the route after the incident opened. Three
        // days late, "nobody answered for twenty minutes" is not news - and after an upgrade it
        // would be sent for every old open incident at once.
        NotificationSteps.Due(Facts(), [Route()], Opened.AddDays(3)).Should().BeEmpty();
    }

    [Fact]
    public void A_step_that_names_nobody_tells_the_route_and_one_for_the_assignee_tells_them()
    {
        var route = Route();
        route.Steps = [new NotificationStep { After = TimeSpan.FromMinutes(1) }, new NotificationStep { After = TimeSpan.FromMinutes(1), ToAssignee = true }];

        var due = NotificationSteps.Due(Facts(assignee: "flo@example.com"), [route], Opened.AddMinutes(2));

        due[0].Recipients.Should().Equal("oncall@example.com");
        due[1].Recipients.Should().Equal("flo@example.com");
    }

    [Theory]
    [InlineData(NotificationEvent.IncidentUnanswered)]
    [InlineData(NotificationEvent.SeverityRaised)]
    public void A_step_and_a_raise_are_not_held_back_by_the_cooldown(NotificationEvent evt)
    {
        NotificationRateLimit.Evaluate("k", Opened, 0, Opened.AddSeconds(5), new NotificationOptions(), evt)
            .IsSuppressed.Should().BeFalse();
        NotificationRateLimit.Evaluate("k", Opened, 0, Opened.AddSeconds(5), new NotificationOptions(), NotificationEvent.IncidentEscalated)
            .IsSuppressed.Should().BeTrue();
    }
}
