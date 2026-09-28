using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Routes that read labels, name people, and a fallback for what nobody owns (#141, #123).
/// </summary>
public sealed class RoutingByLabelTests
{
    private static NotificationSnapshot Alert(Dictionary<string, string>? labels = null, string cluster = "eu", Severity severity = Severity.Warning) =>
        GivenNotifications.Escalation(severity: severity) with
        {
            Labels = labels ?? [],
            Cluster = cluster,
        };

    private static NotificationRoute Team(string team, params string[] recipients) => new()
    {
        Name = team,
        Channel = "teamsBot",
        Events = [NotificationEvent.IncidentEscalated],
        Matchers = [new LabelMatcher { Label = "team", Values = [team] }],
        Recipients = [.. recipients],
    };

    private static NotificationRoute Fallback(params string[] recipients) => new()
    {
        Name = "fallback",
        Channel = "teamsBot",
        Events = [NotificationEvent.IncidentEscalated],
        Fallback = true,
        Recipients = [.. recipients],
    };

    [Fact]
    public void A_matcher_routes_by_label_to_its_recipients()
    {
        var result = NotificationRouter.Match(
            Alert(new() { ["team"] = "payments" }),
            [Team("payments", "a@example.com"), Team("search", "b@example.com"), Fallback("c@example.com")]);

        result.Matches.Should().ContainSingle()
            .Which.Recipients.Should().Equal("a@example.com");
    }

    [Fact]
    public void An_incident_no_scoped_route_owns_goes_to_the_fallback()
    {
        var result = NotificationRouter.Match(
            Alert(new() { ["team"] = "nobody" }),
            [Team("payments", "a@example.com"), Fallback("c@example.com")]);

        result.Matches.Single().Recipients.Should().Equal("c@example.com");
        result.Matches.Single().Routes.Should().Equal("fallback");
    }

    [Fact]
    public void The_fallback_is_silent_when_a_scoped_route_owns_it()
    {
        NotificationRouter.Match(
                Alert(new() { ["team"] = "payments" }),
                [Team("payments", "a@example.com"), Fallback("c@example.com")])
            .Matches.Single().Recipients.Should().NotContain("c@example.com");
    }

    [Fact]
    public void An_unscoped_route_hears_everything_and_does_not_take_the_fallbacks_place()
    {
        var outbound = new NotificationRoute { Channel = "webhook", Events = [NotificationEvent.IncidentEscalated] };

        var result = NotificationRouter.Match(Alert(), [outbound, Fallback("c@example.com")]);

        result.Channels.Should().BeEquivalentTo(["webhook", "teamsBot"]);
    }

    [Fact]
    public void The_fallback_is_for_incidents_not_for_events_about_the_agent()
    {
        var modeRoute = Fallback();
        modeRoute.Events = [NotificationEvent.ModeChanged];

        NotificationRouter.Match(GivenNotifications.ModeChanged(), [modeRoute]).Any.Should().BeFalse();
    }

    [Fact]
    public void Two_routes_on_one_channel_are_one_delivery_to_both_lists()
    {
        var critical = Team("payments", "lead@example.com");
        critical.Name = "payments-critical";
        critical.MinSeverity = Severity.Critical;

        var result = NotificationRouter.Match(
            Alert(new() { ["team"] = "payments" }, severity: Severity.Critical),
            [Team("payments", "a@example.com"), critical]);

        result.Matches.Single().Recipients.Should().BeEquivalentTo(["a@example.com", "lead@example.com"]);
        result.Matches.Single().Routes.Should().BeEquivalentTo(["payments", "payments-critical"]);
    }

    [Fact]
    public void A_route_that_names_nobody_asks_for_the_channels_own_list()
    {
        var plain = new NotificationRoute { Channel = "teamsBot", Events = [NotificationEvent.IncidentEscalated] };

        NotificationRouter.Match(Alert(), [plain]).Matches.Single().UsesChannelRecipients.Should().BeTrue();
    }

    [Fact]
    public void Clusters_and_kinds_scope_a_route()
    {
        var eu = new NotificationRoute
        {
            Channel = "teamsBot",
            Events = [NotificationEvent.IncidentEscalated],
            Clusters = ["eu"],
            Kinds = [SignalKind.CrashLoopBackOff],
        };

        NotificationRouter.Match(Alert(cluster: "eu"), [eu]).Any.Should().BeTrue();
        NotificationRouter.Match(Alert(cluster: "us"), [eu]).Any.Should().BeFalse();
        NotificationRouter.Match(Alert(cluster: "eu") with { Kind = SignalKind.Pipeline }, [eu]).Any.Should().BeFalse();
    }
}
