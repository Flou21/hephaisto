using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hephaisto.Agent.Notifications;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The generic outbound HTTP channel: the one with no third-party account behind it, and
/// therefore the one that proves the outbox before any vendor payload exists.
/// </summary>
public sealed class HttpNotificationChannelTests
{
    private const string Url = "https://receiver.example/hooks/hephaisto";
    private const string Secret = "not-a-real-secret";

    [Fact]
    public async Task It_posts_json_to_the_configured_url()
    {
        var (channel, handler) = Build();

        var result = await channel.SendAsync(Message(), TestContext.Current.CancellationToken);

        result.Disposition.Should().Be(DeliveryDisposition.Delivered);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.ToString().Should().Be(Url);
        handler.ContentType.Should().Contain("application/json");
    }

    [Fact]
    public async Task The_delivery_id_goes_on_the_wire_so_a_receiver_can_dedupe()
    {
        // At-least-once delivery makes a duplicate normal rather than a bug, and a receiver with
        // no key to dedupe on cannot tell the difference.
        var (channel, handler) = Build();
        var message = Message();

        await channel.SendAsync(message, TestContext.Current.CancellationToken);

        handler.Requests[0].Headers.GetValues(HttpNotificationChannel.DeliveryHeader)
            .Should().Equal(message.DeliveryId.ToString());
        handler.Requests[0].Headers.GetValues(HttpNotificationChannel.EventHeader)
            .Should().Equal("IncidentEscalated");
    }

    [Fact]
    public async Task The_signature_covers_the_exact_bytes_that_were_sent()
    {
        // Re-serialising for the HMAC is how a signature comes out correct in a test and wrong
        // on the wire, so this recomputes it from the body the handler actually received.
        var (channel, handler) = Build(secret: Secret);

        await channel.SendAsync(Message(), TestContext.Current.CancellationToken);

        var expected = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), handler.Body!));

        handler.Requests[0].Headers.GetValues(HttpNotificationChannel.SignatureHeader)
            .Should().Equal(expected);
    }

    [Fact]
    public async Task With_no_secret_there_is_no_signature_header()
    {
        var (channel, handler) = Build();

        await channel.SendAsync(Message(), TestContext.Current.CancellationToken);

        handler.Requests[0].Headers.Contains(HttpNotificationChannel.SignatureHeader).Should().BeFalse();
    }

    [Fact]
    public async Task An_unconfigured_channel_fails_permanently_rather_than_retrying_forever()
    {
        var (channel, handler) = Build(url: null);

        var result = await channel.SendAsync(Message(), TestContext.Current.CancellationToken);

        result.Disposition.Should().Be(DeliveryDisposition.Permanent);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transport_failure_is_retryable_and_does_not_propagate()
    {
        // The condition the outbox exists to outlive. It must come back as a result, not an
        // exception, or one bad endpoint takes down the loop serving the others.
        var (channel, _) = Build(throws: new HttpRequestException("connection refused"));

        var result = await channel.SendAsync(Message(), TestContext.Current.CancellationToken);

        result.Disposition.Should().Be(DeliveryDisposition.Retryable);
        result.Detail.Should().Contain("connection refused");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, DeliveryDisposition.Retryable)]
    [InlineData(HttpStatusCode.TooManyRequests, DeliveryDisposition.Retryable)]
    [InlineData(HttpStatusCode.BadRequest, DeliveryDisposition.Permanent)]
    [InlineData(HttpStatusCode.Accepted, DeliveryDisposition.Delivered)]
    public async Task The_status_decides_whether_it_is_worth_trying_again(
        HttpStatusCode status,
        DeliveryDisposition expected)
    {
        var (channel, _) = Build(status: status);

        (await channel.SendAsync(Message(), TestContext.Current.CancellationToken))
            .Disposition.Should().Be(expected);
    }

    [Fact]
    public async Task An_incident_message_carries_the_diagnosis_and_a_link_to_act_on_it()
    {
        var (channel, handler) = Build();

        await channel.SendAsync(Message(), TestContext.Current.CancellationToken);

        var json = JsonDocument.Parse(handler.Body!).RootElement;

        json.GetProperty("event").GetString().Should().Be("IncidentEscalated");
        json.GetProperty("severity").GetString().Should().Be("Critical");
        json.GetProperty("incident").GetProperty("namespace").GetString().Should().Be("hephaisto-chaos");
        json.GetProperty("incident").GetProperty("summary").GetString().Should().Be("the image tag does not exist");
        json.GetProperty("links").GetProperty("incident").GetString().Should().Contain("/incidents/");
    }

    [Fact]
    public async Task An_agent_event_carries_no_incident_block_at_all()
    {
        // Rather than one full of empty strings, which a receiver would have to know to ignore.
        var (channel, handler) = Build();

        var message = new NotificationMessage
        {
            DeliveryId = Guid.CreateVersion7(),
            Snapshot = GivenNotifications.ModeChanged(),
        };

        await channel.SendAsync(message, TestContext.Current.CancellationToken);

        var json = JsonDocument.Parse(handler.Body!).RootElement;

        json.TryGetProperty("incident", out _).Should().BeFalse();
        json.GetProperty("event").GetString().Should().Be("ModeChanged");
    }

    [Fact]
    public async Task A_suppressed_burst_is_counted_on_the_message_that_does_go_out()
    {
        var (channel, handler) = Build();

        await channel.SendAsync(Message() with { AlsoSuppressed = 12 }, TestContext.Current.CancellationToken);

        JsonDocument.Parse(handler.Body!).RootElement
            .GetProperty("alsoSuppressed").GetInt32().Should().Be(12);
    }

    [Fact]
    public void Describe_says_whether_it_is_on_and_whether_anyone_can_verify_it()
    {
        var (off, _) = Build(url: null);
        off.Describe().Should().Contain("OFF").And.Contain("Notifications:Webhook:Url");

        var (unsigned, _) = Build();
        unsigned.Describe().Should().Contain("ON").And.Contain("UNSIGNED");

        var (signed, _) = Build(secret: Secret);
        signed.Describe().Should().Contain("ON").And.Contain("signed");

        // The webhook URL is a configured address rather than a credential, so naming it is
        // useful. The Teams one is not, and its channel is tested separately for that.
        signed.Describe().Should().NotContain(Secret);
    }

    private static NotificationMessage Message() => new()
    {
        DeliveryId = Guid.CreateVersion7(),
        Snapshot = GivenNotifications.Escalation() with { Summary = "the image tag does not exist" },
        IncidentUrl = "https://hephaisto.example/incidents/abc",
    };

    private static (HttpNotificationChannel Channel, RecordingHandler Handler) Build(
        string? url = Url,
        string? secret = null,
        HttpStatusCode status = HttpStatusCode.OK,
        Exception? throws = null)
    {
        var handler = new RecordingHandler(status, throws);

        var options = new NotificationOptions
        {
            BaseUrl = "https://hephaisto.example",
            Webhook = new HttpChannelOptions { Url = url, SigningSecret = secret },
        };

        var channel = new HttpNotificationChannel(
            new HttpClient(handler),
            new StaticOptions(options),
            NullLogger<HttpNotificationChannel>.Instance);

        return (channel, handler);
    }

    private sealed class RecordingHandler(HttpStatusCode status, Exception? throws) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public byte[]? Body { get; private set; }

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                ContentType = request.Content.Headers.ContentType?.ToString();
            }

            if (throws is not null)
            {
                throw throws;
            }

            return new HttpResponseMessage(status) { Content = new StringContent("ok") };
        }
    }

    private sealed class StaticOptions(NotificationOptions value) : IOptionsMonitor<NotificationOptions>
    {
        public NotificationOptions CurrentValue => value;

        public NotificationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NotificationOptions, string?> listener) => null;
    }
}
