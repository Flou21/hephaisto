using System.Net;
using System.Text.Json;
using Hephaisto.Agent.Notifications;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The Teams card. Two things are load-bearing and neither is the layout: the envelope shape
/// Power Automate expects, and the fact that the trigger URL never reaches a log.
/// </summary>
public sealed class TeamsNotificationChannelTests
{
    /// <summary>A Workflows URL's <c>sig</c> parameter is the entire authentication.</summary>
    private const string WorkflowUrl =
        "https://prod-99.westeurope.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?sig=SUPERSECRETSIG";

    [Fact]
    public void The_envelope_is_the_shape_power_automate_forwards()
    {
        var json = TeamsNotificationChannel.BuildEnvelope(Message()).ToJsonString();
        var root = JsonDocument.Parse(json).RootElement;

        root.GetProperty("type").GetString().Should().Be("message");

        var attachment = root.GetProperty("attachments")[0];

        attachment.GetProperty("contentType").GetString()
            .Should().Be("application/vnd.microsoft.card.adaptive");

        var card = attachment.GetProperty("content");

        card.GetProperty("type").GetString().Should().Be("AdaptiveCard");

        // Pinned rather than left to the host: a card that silently degrades to plain text is
        // worse than one that is refused, because it looks delivered.
        card.GetProperty("version").GetString().Should().Be("1.5");
    }

    [Fact]
    public void The_headline_says_what_happened_in_words()
    {
        // Somebody reading this on a phone at 3am must not have to infer the state from a
        // colour. Colour is the third channel here, exactly as it is in the console.
        var card = Card(Message());
        var headline = card.GetProperty("body")[0];

        headline.GetProperty("text").GetString().Should().Contain("Escalated");
        headline.GetProperty("color").GetString().Should().Be("Attention");
    }

    [Theory]
    [InlineData(NotificationEvent.ApprovalRequired, "Approval required")]
    [InlineData(NotificationEvent.IncidentResolved, "Resolved")]
    [InlineData(NotificationEvent.VerificationFailed, "Verification failed")]
    [InlineData(NotificationEvent.ModeChanged, "Autonomy re-armed")]
    [InlineData(NotificationEvent.PolicyChanged, "Policy configuration changed")]
    [InlineData(NotificationEvent.CodeFixPlanReady, "Code fix planned")]
    [InlineData(NotificationEvent.CodeFixPrOpened, "Draft PR opened")]
    [InlineData(NotificationEvent.CodeFixFailed, "Code fix ended without a PR")]
    public void Every_event_has_its_own_headline(NotificationEvent kind, string expected)
    {
        var message = Message() with
        {
            Snapshot = GivenNotifications.Escalation(@event: kind),
        };

        Card(message).GetProperty("body")[0].GetProperty("text").GetString()
            .Should().Contain(expected);
    }

    [Fact]
    public void An_approval_card_links_out_rather_than_offering_a_button()
    {
        // v1 deliberately does not approve in-card: that needs an authenticated inbound
        // surface, which is a security change rather than a feature increment.
        var message = Message() with
        {
            Snapshot = GivenNotifications.Escalation(@event: NotificationEvent.ApprovalRequired),
        };

        var actions = Card(message).GetProperty("actions");

        actions[0].GetProperty("type").GetString().Should().Be("Action.OpenUrl");
        actions[0].GetProperty("title").GetString().Should().Contain("approve");
        actions[0].GetProperty("url").GetString().Should().Be("https://hephaisto.example/incidents/abc");

        // No submit action of any kind, which is what would need an inbound route.
        Card(message).ToString().Should().NotContain("Action.Submit");
    }

    [Fact]
    public void A_pr_card_puts_the_draft_pr_first_and_still_approves_nothing_in_card()
    {
        var message = Message() with
        {
            Snapshot = GivenNotifications.Escalation(@event: NotificationEvent.CodeFixPrOpened) with
            {
                ExternalUrl = "https://github.com/o/r/pull/7",
                Repository = "https://github.com/o/r",
                CodeFixAttemptId = Guid.CreateVersion7(),
            },
        };

        var card = Card(message);
        var actions = card.GetProperty("actions");

        actions[0].GetProperty("title").GetString().Should().Be("Open the Draft PR");
        actions[0].GetProperty("url").GetString().Should().Be("https://github.com/o/r/pull/7");
        card.ToString().Should().Contain("https://github.com/o/r").And.NotContain("Action.Submit");
    }

    [Fact]
    public void A_plan_card_links_to_the_plan_review()
    {
        var message = Message() with
        {
            Snapshot = GivenNotifications.Escalation(@event: NotificationEvent.CodeFixPlanReady) with { CodeFixAttemptId = Guid.CreateVersion7() },
        };

        Card(message).GetProperty("actions")[0].GetProperty("title").GetString().Should().Be("Review the plan in Hephaisto");
    }

    [Fact]
    public void A_work_items_plan_names_the_issue_and_links_its_own_page_and_the_issue()
    {
        var message = new NotificationMessage
        {
            DeliveryId = Guid.CreateVersion7(),
            Snapshot = GivenNotifications.WorkItemPlan(),
            CodeFixUrl = "https://hephaisto.example/codefixes/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c",
        };

        var card = Card(message);
        var text = card.ToString();

        // What an issue has, and nothing an incident has: no severity, kind or target made up.
        text.Should().Contain("octo/shop#12").And.Contain("The order total is null for an empty cart");
        text.Should().NotContain("Severity").And.NotContain("Kind").And.NotContain("Target");

        var actions = card.GetProperty("actions").EnumerateArray()
            .Select(a => (Title: a.GetProperty("title").GetString(), Url: a.GetProperty("url").GetString()))
            .ToList();

        actions.Should().Equal(
            ("Review the plan in Hephaisto", "https://hephaisto.example/codefixes/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"),
            ("Open the issue", "https://github.com/octo/shop/issues/12"));
        text.Should().NotContain("Action.Submit").And.NotContain("Action.Execute");

        // The issue's title and the model's summary are TextRuns, shown as they are: a TextBlock
        // would render "[the docs](https://evil.example)" in the summary as a link.
        var blocks = card.GetProperty("body").EnumerateArray().ToList();
        var runs = blocks.Where(b => b.GetProperty("type").GetString() == "RichTextBlock")
            .Select(b => b.GetProperty("inlines")[0].GetProperty("text").GetString())
            .ToList();

        runs.Should().Contain("The order total is null for an empty cart");
        runs.Should().Contain(r => r!.Contains("[the docs](https://evil.example)"));
        blocks.Where(b => b.GetProperty("type").GetString() == "TextBlock")
            .Select(b => b.GetProperty("text").GetString())
            .Should().NotContain(t => t!.Contains("evil.example") || t.Contains("The order total"));
    }

    [Fact]
    public void An_incidents_card_is_built_of_the_blocks_it_always_was()
    {
        // The work item's plain runs are for a work item: an incident's card is unchanged.
        var blocks = Card(Message()).GetProperty("body").EnumerateArray().ToList();

        blocks.Should().NotContain(b => b.GetProperty("type").GetString() == "RichTextBlock");
        blocks.Should().Contain(b => b.GetProperty("type").GetString() == "TextBlock"
            && b.GetProperty("text").GetString() == "api is crash looping");
    }

    [Fact]
    public void A_suppressed_burst_is_stated_on_the_card_that_does_go_out()
    {
        var card = Card(Message() with { AlsoSuppressed = 7 });

        card.ToString().Should().Contain("7 further notification");
    }

    [Fact]
    public void An_agent_event_carries_no_incident_facts()
    {
        var message = new NotificationMessage
        {
            DeliveryId = Guid.CreateVersion7(),
            Snapshot = GivenNotifications.ModeChanged(),
        };

        // No target and no kind, rather than a FactSet full of blanks.
        Card(message).ToString().Should().NotContain("Target");
    }

    [Fact]
    public void A_card_with_no_links_carries_no_empty_action_bar()
    {
        var message = new NotificationMessage
        {
            DeliveryId = Guid.CreateVersion7(),
            Snapshot = GivenNotifications.Escalation(),
        };

        Card(message).TryGetProperty("actions", out _).Should().BeFalse();
    }

    [Fact]
    public void Describe_never_prints_the_signature()
    {
        // The URL is a bearer credential in a query string. Printing it - which is what every
        // other channel here can safely do - writes a live credential into the pod log and from
        // there into whatever collects it.
        var (channel, _) = Build();

        var described = channel.Describe();

        described.Should().NotContain("SUPERSECRETSIG");
        described.Should().NotContain("sig=");
        described.Should().Contain("ON").And.Contain("prod-99.westeurope.logic.azure.com");
    }

    [Fact]
    public void Describe_names_the_missing_key_when_it_is_off()
    {
        var (channel, _) = Build(url: null);

        channel.Describe().Should().Contain("OFF").And.Contain("Notifications:Teams:WorkflowUrl");
    }

    [Fact]
    public async Task An_unconfigured_channel_fails_permanently()
    {
        var (channel, handler) = Build(url: null);

        (await channel.SendAsync(Message(), TestContext.Current.CancellationToken))
            .Disposition.Should().Be(DeliveryDisposition.Permanent);

        handler.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task A_workflows_202_is_a_delivery()
    {
        // Power Automate answers 202 to a trigger. Treating only 200 as success would fail
        // every Teams delivery while reporting a transport problem.
        var (channel, _) = Build(status: HttpStatusCode.Accepted);

        (await channel.SendAsync(Message(), TestContext.Current.CancellationToken))
            .Disposition.Should().Be(DeliveryDisposition.Delivered);
    }

    [Fact]
    public async Task A_transport_failure_is_retryable_and_does_not_propagate()
    {
        var (channel, _) = Build(throws: new HttpRequestException("dns failure"));

        (await channel.SendAsync(Message(), TestContext.Current.CancellationToken))
            .Disposition.Should().Be(DeliveryDisposition.Retryable);
    }

    private static JsonElement Card(NotificationMessage message) =>
        JsonDocument.Parse(TeamsNotificationChannel.BuildEnvelope(message).ToJsonString())
            .RootElement.GetProperty("attachments")[0].GetProperty("content");

    private static NotificationMessage Message() => new()
    {
        DeliveryId = Guid.CreateVersion7(),
        Snapshot = GivenNotifications.Escalation() with { Summary = "the image tag does not exist" },
        IncidentUrl = "https://hephaisto.example/incidents/abc",
    };

    private static (TeamsNotificationChannel Channel, StubHandler Handler) Build(
        string? url = WorkflowUrl,
        HttpStatusCode status = HttpStatusCode.OK,
        Exception? throws = null)
    {
        var handler = new StubHandler(status, throws);

        var channel = new TeamsNotificationChannel(
            new HttpClient(handler),
            new StaticOptions(new NotificationOptions
            {
                BaseUrl = "https://hephaisto.example",
                Teams = new TeamsChannelOptions { WorkflowUrl = url },
            }),
            NullLogger<TeamsNotificationChannel>.Instance);

        return (channel, handler);
    }

    private sealed class StubHandler(HttpStatusCode status, Exception? throws) : HttpMessageHandler
    {
        public bool Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Sent = true;

            if (throws is not null)
            {
                throw throws;
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
        }
    }

    private sealed class StaticOptions(NotificationOptions value) : IOptionsMonitor<NotificationOptions>
    {
        public NotificationOptions CurrentValue => value;

        public NotificationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NotificationOptions, string?> listener) => null;
    }
}
