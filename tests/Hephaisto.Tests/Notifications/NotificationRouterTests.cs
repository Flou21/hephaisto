using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Routing decides who gets told the agent needs help, so its failure mode is silence. Every
/// test here is really the same question asked from a different side: can a rule that looks
/// correct deliver nothing?
/// </summary>
public sealed class NotificationRouterTests
{
    [Fact]
    public void NoRoutes_DeliverNowhere()
    {
        // The shipped default. A stock install notifies nobody, in the same direction as an
        // empty AllowedNamespaces and mode: Observe.
        var result = NotificationRouter.Match(GivenNotifications.Escalation(), []);

        result.Any.Should().BeFalse();
        result.SuppressedByUnknownNamespace.Should().BeFalse();
    }

    [Fact]
    public void Unspecified_IsNeverRouted()
    {
        // It exists to take the zero value so a default-constructed row cannot claim to be an
        // escalation. Routing it would defeat the point of having it.
        var snapshot = GivenNotifications.Escalation() with { Event = NotificationEvent.Unspecified };
        var route = GivenNotifications.Route(events: [NotificationEvent.Unspecified]);

        NotificationRouter.Match(snapshot, [route]).Any.Should().BeFalse();
    }

    [Fact]
    public void AMatchingRule_NamesItsChannel()
    {
        NotificationRouter.Match(GivenNotifications.Escalation(), [GivenNotifications.Route()])
            .Channels.Should().Equal("teams");
    }

    [Fact]
    public void AnEventTheRuleDoesNotCarry_DoesNotMatch()
    {
        var route = GivenNotifications.Route(events: [NotificationEvent.IncidentResolved]);

        NotificationRouter.Match(GivenNotifications.Escalation(), [route]).Any.Should().BeFalse();
    }

    [Fact]
    public void AnEmptyEventList_CarriesNothing()
    {
        // Empty means none, not all. Every default in this project points that way.
        var route = GivenNotifications.Route(events: []);

        NotificationRouter.Match(GivenNotifications.Escalation(), [route]).Any.Should().BeFalse();
    }

    [Theory]
    [InlineData(Severity.Info, false)]
    [InlineData(Severity.Warning, false)]
    [InlineData(Severity.Critical, true)]
    public void MinSeverityIsInclusive(Severity incident, bool expected)
    {
        var snapshot = GivenNotifications.Escalation(severity: incident);
        var route = GivenNotifications.Route(minSeverity: Severity.Critical);

        NotificationRouter.Match(snapshot, [route]).Any.Should().Be(expected);
    }

    [Fact]
    public void AnUnscopedRule_CarriesEveryNamespace()
    {
        var snapshot = GivenNotifications.Escalation(ns: "somewhere-else");

        NotificationRouter.Match(snapshot, [GivenNotifications.Route()]).Any.Should().BeTrue();
    }

    [Fact]
    public void AScopedRule_CarriesOnlyItsNamespaces()
    {
        var route = GivenNotifications.Route(namespaces: ["hephaisto-chaos"]);

        NotificationRouter.Match(GivenNotifications.Escalation(ns: "hephaisto-chaos"), [route])
            .Any.Should().BeTrue();
        NotificationRouter.Match(GivenNotifications.Escalation(ns: "production"), [route])
            .Any.Should().BeFalse();
    }

    [Fact]
    public void TwoRulesNamingOneChannel_ProduceOneDelivery()
    {
        // Otherwise a broadening rule added beside a specific one silently doubles every page.
        var routes = new[]
        {
            GivenNotifications.Route(namespaces: ["hephaisto-chaos"]),
            GivenNotifications.Route(),
        };

        NotificationRouter.Match(GivenNotifications.Escalation(), routes)
            .Channels.Should().Equal("teams");
    }

    [Fact]
    public void ChannelsKeepTheOrderTheRulesDeclaredThem()
    {
        var routes = new[]
        {
            GivenNotifications.Route(channel: "webhook"),
            GivenNotifications.Route(channel: "teams"),
        };

        NotificationRouter.Match(GivenNotifications.Escalation(), routes)
            .Channels.Should().Equal("webhook", "teams");
    }

    [Fact]
    public void ARuleWithNoChannelName_IsSkippedRatherThanMatched()
    {
        NotificationRouter.Match(GivenNotifications.Escalation(), [GivenNotifications.Route(channel: "  ")])
            .Any.Should().BeFalse();
    }

    [Fact]
    public void AnIncidentWithNoNamespace_CannotMatchAScopedRule_AndSaysSo()
    {
        // backlog #33: a metric-derived alert whose rule labels the namespace something ingest
        // does not read arrives with an empty one. Without the flag this is an escalation that
        // reaches nobody while the routing table looks correct.
        var snapshot = GivenNotifications.Escalation(ns: string.Empty);
        var route = GivenNotifications.Route(namespaces: ["hephaisto-chaos"]);

        var result = NotificationRouter.Match(snapshot, [route]);

        result.Any.Should().BeFalse();
        result.SuppressedByUnknownNamespace.Should().BeTrue();
    }

    [Fact]
    public void TheUnknownNamespaceFlagIsQuiet_WhenSomethingElseDelivered()
    {
        // The empty namespace cost nothing here, so reporting it would be noise.
        var snapshot = GivenNotifications.Escalation(ns: string.Empty);
        var routes = new[]
        {
            GivenNotifications.Route(channel: "teams", namespaces: ["hephaisto-chaos"]),
            GivenNotifications.Route(channel: "webhook"),
        };

        var result = NotificationRouter.Match(snapshot, routes);

        result.Channels.Should().Equal("webhook");
        result.SuppressedByUnknownNamespace.Should().BeFalse();
    }

    [Fact]
    public void AnAgentEventMissingAScopedRule_IsNotAMissingNamespace()
    {
        // ModeChanged has no workload, so a namespace-scoped rule correctly excludes it. That is
        // a rule doing its job, not the backlog #33 failure, and must not raise the alarm.
        var route = GivenNotifications.Route(
            events: [NotificationEvent.ModeChanged],
            namespaces: ["hephaisto-chaos"]);

        var result = NotificationRouter.Match(GivenNotifications.ModeChanged(), [route]);

        result.Any.Should().BeFalse();
        result.SuppressedByUnknownNamespace.Should().BeFalse();
    }

    [Fact]
    public void AnAgentEvent_ReachesAnUnscopedRule()
    {
        var route = GivenNotifications.Route(events: [NotificationEvent.ModeChanged]);

        NotificationRouter.Match(GivenNotifications.ModeChanged(), [route]).Any.Should().BeTrue();
    }
}
