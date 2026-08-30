using Hephaisto.Agent.Notifications;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Asserts that <see cref="NotificationOptions"/> is bound to the exact key shape the chart
/// emits.
/// </summary>
/// <remarks>
/// The same test <see cref="PolicyOptionsBindingTests"/> is, written for the same reason and
/// before the same thing can happen twice. <c>PolicyOptions</c> was never bound to
/// configuration, and nothing said so: <c>IOptionsMonitor&lt;T&gt;</c> resolves happily to a
/// default-constructed instance, so the chart set <c>Policy__AllowedNamespaces__N</c> for two
/// releases and no code read it.
///
/// The failure would look identical here, and be harder to notice: unbound options mean an
/// empty routing table, an empty routing table means nothing is delivered, and nothing being
/// delivered is <b>also</b> what a correctly configured stock install does.
/// </remarks>
public sealed class NotificationOptionsBindingTests
{
    private static NotificationOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddHephaistoNotifications(configuration);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<NotificationOptions>>()
            .CurrentValue;
    }

    [Fact]
    public void The_charts_route_key_shape_binds()
    {
        // Exactly what templates/deployment.yaml renders for
        //   notifications.routes[0] = {channel: teams, events: [...], minSeverity, namespaces}
        var options = Bind(
            ("Notifications:BaseUrl", "https://hephaisto.example"),
            ("Notifications:Teams:WorkflowUrl", "https://logic.example/trigger?sig=x"),
            ("Notifications:Routes:0:Channel", "teams"),
            ("Notifications:Routes:0:MinSeverity", "Warning"),
            ("Notifications:Routes:0:Events:0", "IncidentEscalated"),
            ("Notifications:Routes:0:Events:1", "ApprovalRequired"),
            ("Notifications:Routes:0:Namespaces:0", "hephaisto-chaos"));

        options.BaseUrl.Should().Be("https://hephaisto.example");
        options.Routes.Should().ContainSingle();
        options.Routes[0].Channel.Should().Be("teams");
        options.Routes[0].MinSeverity.Should().Be(Severity.Warning);
        options.Routes[0].Events.Should().Equal(
            NotificationEvent.IncidentEscalated,
            NotificationEvent.ApprovalRequired);
        options.Routes[0].Namespaces.Should().Equal("hephaisto-chaos");
    }

    [Fact]
    public void The_webhook_keys_bind()
    {
        var options = Bind(
            ("Notifications:Webhook:Url", "https://r.example/hook"),
            ("Notifications:Webhook:SigningSecret", "s"));

        options.Webhook.Url.Should().Be("https://r.example/hook");
        options.Webhook.SigningSecret.Should().Be("s");
        options.ConfiguredChannels().Should().Equal(NotificationChannelNames.Webhook);
    }

    [Fact]
    public void With_nothing_configured_the_defaults_deliver_nowhere()
    {
        var options = Bind();

        options.Routes.Should().BeEmpty();
        options.ConfiguredChannels().Should().BeEmpty();
    }

    [Fact]
    public void A_timespan_knob_set_through_extraEnv_binds()
    {
        // The tuning knobs have no first-class chart value on purpose, so extraEnv is their
        // supported route - which only holds if the binding actually parses them.
        Bind(("Notifications:CorrelationCooldown", "00:00:30"))
            .CorrelationCooldown.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_route_naming_an_unconfigured_channel_is_refused_at_startup()
    {
        // Not a warning. A rule that matches and delivers nowhere looks exactly like one that
        // works, which is the failure this whole stream exists to remove.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new("Notifications:BaseUrl", "https://hephaisto.example"),
                new("Notifications:Routes:0:Channel", "teams"),
                new("Notifications:Routes:0:Events:0", "IncidentEscalated"),
            ])
            .Build();

        var services = new ServiceCollection();
        services.AddHephaistoNotifications(configuration);

        var provider = services.BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredService<IOptionsMonitor<NotificationOptions>>().CurrentValue)
            .Should().Throw<OptionsValidationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public void Routes_without_a_base_url_are_refused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new("Notifications:Webhook:Url", "https://r.example/hook"),
                new("Notifications:Routes:0:Channel", "webhook"),
                new("Notifications:Routes:0:Events:0", "IncidentEscalated"),
            ])
            .Build();

        var services = new ServiceCollection();
        services.AddHephaistoNotifications(configuration);

        provider_should_throw(services);

        static void provider_should_throw(ServiceCollection services) =>
            services.BuildServiceProvider()
                .Invoking(p => p.GetRequiredService<IOptionsMonitor<NotificationOptions>>().CurrentValue)
                .Should().Throw<OptionsValidationException>()
                .WithMessage("*BaseUrl*");
    }
}
