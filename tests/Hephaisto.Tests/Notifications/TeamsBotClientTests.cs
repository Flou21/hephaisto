using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The Bot Connector calls, against a handler that records what was actually sent. The shapes
/// asserted here are the ones a real tenant accepted on 2026-09-28.
/// </summary>
public sealed class TeamsBotClientTests
{
    private const string Secret = "not-a-real-secret";
    private const string Channel = "19:abc@thread.tacv2";

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_bot_cannot_delete()
    {
        // Teams leaves "This message has been deleted." behind for a deleted channel post and
        // for a deleted reply alike. The way a message goes away is by being edited, so the
        // capability is absent rather than unused.
        var methods = typeof(ITeamsBotClient).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name);

        methods.Should().NotContain(n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Remove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_call_is_ever_a_delete()
    {
        var (client, handler, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        await client.PostToChannelAsync(Message(), ct);
        await client.FindMemberAsync("it@true-relevance.example", ct);
        await client.OpenChatAsync("29:user", ct);
        await client.SendAsync("a:chat", Message(), ct);
        await client.UpdateAsync("a:chat", "1", Message(), ct);

        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task The_token_is_asked_of_the_tenant_and_not_of_the_shared_endpoint()
    {
        // A single-tenant bot asking botframework.com gets a token Teams refuses.
        var (client, handler, _) = Build();

        await client.SendAsync("a:chat", Message(), TestContext.Current.CancellationToken);

        var token = handler.Requests[0];

        token.Uri.Should().Be("https://login.example/tenant-1/oauth2/v2.0/token");
        token.Body.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=app-1")
            .And.Contain("scope=https%3A%2F%2Fapi.botframework.com%2F.default");
    }

    [Fact]
    public async Task The_token_is_asked_for_once_and_reused()
    {
        var (client, handler, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        await client.SendAsync("a:chat", Message(), ct);
        await client.SendAsync("a:chat", Message(), ct);
        await client.UpdateAsync("a:chat", "1", Message(), ct);

        handler.Requests.Count(r => r.Uri.Contains("/oauth2/", StringComparison.Ordinal)).Should().Be(1);
        handler.Requests.Where(r => !r.Uri.Contains("/oauth2/", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Authorization == "Bearer token-1");
    }

    [Fact]
    public async Task A_token_about_to_lapse_is_renewed_before_it_is_used()
    {
        var (client, handler, clock) = Build();
        var ct = TestContext.Current.CancellationToken;

        await client.SendAsync("a:chat", Message(), ct);

        clock.UtcNow = Now.AddMinutes(56);

        await client.SendAsync("a:chat", Message(), ct);

        handler.Requests.Count(r => r.Uri.Contains("/oauth2/", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task A_refused_token_is_not_presented_again()
    {
        var (client, handler, _) = Build(api: HttpStatusCode.Unauthorized);
        var ct = TestContext.Current.CancellationToken;

        (await client.SendAsync("a:chat", Message(), ct)).Ok.Should().BeFalse();
        await client.SendAsync("a:chat", Message(), ct);

        handler.Requests.Count(r => r.Uri.Contains("/oauth2/", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task A_refused_token_request_says_why_and_never_echoes_the_secret()
    {
        var (client, _, _) = Build(token: HttpStatusCode.Unauthorized);

        var result = await client.SendAsync("a:chat", Message(), TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("token request refused").And.Contain("AADSTS7000215");
        result.Detail.Should().NotContain(Secret);
        result.ToDelivery().Disposition.Should().Be(DeliveryDisposition.Permanent);
    }

    [Fact]
    public async Task A_board_is_posted_as_a_new_thread_in_the_configured_channel()
    {
        var (client, handler, _) = Build();

        var posted = await client.PostToChannelAsync(Message(), TestContext.Current.CancellationToken);

        posted.Ok.Should().BeTrue();
        posted.Value.ConversationId.Should().Be("19:abc@thread.tacv2;messageid=1");
        posted.Value.ActivityId.Should().Be("1");

        var request = handler.Api[0];
        var body = JsonNode.Parse(request.Body)!;

        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be("https://teams.example/teams/v3/conversations");
        body["isGroup"]!.GetValue<bool>().Should().BeTrue();
        body["channelData"]!["channel"]!["id"]!.GetValue<string>().Should().Be(Channel);
        body["channelData"]!["tenant"]!["id"]!.GetValue<string>().Should().Be("tenant-1");
        body["bot"]!["id"]!.GetValue<string>().Should().Be("app-1");
        body["activity"]!["type"]!.GetValue<string>().Should().Be("message");
    }

    [Fact]
    public async Task An_edit_is_a_put_to_the_message_with_its_id_escaped()
    {
        // A channel conversation id holds ':', ';', '=' and '@'. Unescaped, the ';' ends the
        // path segment and Teams answers "Bad format of conversation ID".
        var (client, handler, _) = Build();

        var edited = await client.UpdateAsync(
            "19:abc@thread.tacv2;messageid=1",
            "1",
            Message(),
            TestContext.Current.CancellationToken);

        edited.Ok.Should().BeTrue();
        handler.Api[0].Method.Should().Be(HttpMethod.Put);
        handler.Api[0].Uri.Should().Be(
            "https://teams.example/teams/v3/conversations/19%3Aabc%40thread.tacv2%3Bmessageid%3D1/activities/1");
    }

    [Fact]
    public async Task A_member_is_found_by_mailbox_or_by_login_name_whatever_the_case()
    {
        var (client, handler, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        (await client.FindMemberAsync("IT@True-Relevance.example", ct)).Value.Should().Be("29:it");
        (await client.FindMemberAsync("login@true-relevance.example", ct)).Value.Should().Be("29:other");

        handler.Api[0].Method.Should().Be(HttpMethod.Get);
        handler.Api[0].Uri.Should().Be(
            "https://teams.example/teams/v3/conversations/19%3Aabc%40thread.tacv2/pagedmembers?pageSize=500");
    }

    [Fact]
    public async Task Somebody_who_is_not_in_the_team_is_an_answer_and_not_a_failure()
    {
        var (client, _, _) = Build();

        var found = await client.FindMemberAsync("nobody@elsewhere.example", TestContext.Current.CancellationToken);

        found.Ok.Should().BeTrue();
        found.Value.Should().BeNull();
        found.Detail.Should().Contain("not a member");
    }

    [Fact]
    public async Task A_transport_failure_is_a_value_and_is_worth_trying_again()
    {
        var (client, _, _) = Build(throws: new HttpRequestException("dns failure"));

        var result = await client.SendAsync("a:chat", Message(), TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Status.Should().BeNull();
        result.ToDelivery().Disposition.Should().Be(DeliveryDisposition.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, DeliveryDisposition.Retryable)]
    [InlineData(HttpStatusCode.BadGateway, DeliveryDisposition.Retryable)]
    [InlineData(HttpStatusCode.Forbidden, DeliveryDisposition.Permanent)]
    [InlineData(HttpStatusCode.NotFound, DeliveryDisposition.Permanent)]
    public async Task A_refusal_is_classified_as_every_other_channel_classifies_it(
        HttpStatusCode status,
        DeliveryDisposition expected)
    {
        var (client, _, _) = Build(api: status);

        var result = await client.UpdateAsync("a:chat", "1", Message(), TestContext.Current.CancellationToken);

        result.ToDelivery().Disposition.Should().Be(expected);
        result.Gone.Should().Be(status is HttpStatusCode.NotFound);
    }

    private static JsonObject Message() => new() { ["type"] = "message", ["text"] = "hello" };

    private static (TeamsBotClient Client, RecordingHandler Handler, MovableClock Clock) Build(
        HttpStatusCode token = HttpStatusCode.OK,
        HttpStatusCode api = HttpStatusCode.OK,
        Exception? throws = null)
    {
        var handler = new RecordingHandler(token, api, throws);
        var clock = new MovableClock { UtcNow = Now };

        var client = new TeamsBotClient(
            new HttpClient(handler),
            new TeamsBotTokenCache(),
            clock,
            new StaticOptions(new NotificationOptions
            {
                BaseUrl = "https://hephaisto.example",
                TeamsBot = new TeamsBotOptions
                {
                    TenantId = "tenant-1",
                    AppId = "app-1",
                    ClientSecret = Secret,
                    ChannelId = Channel,
                    ServiceUrl = "https://teams.example/teams/",
                    LoginUrl = "https://login.example",
                },
            }),
            NullLogger<TeamsBotClient>.Instance);

        return (client, handler, clock);
    }

    internal sealed record Recorded(HttpMethod Method, string Uri, string Body, string? Authorization);

    private sealed class RecordingHandler(HttpStatusCode token, HttpStatusCode api, Exception? throws) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        public List<Recorded> Api => [.. Requests.Where(r => !r.Uri.Contains("/oauth2/", StringComparison.Ordinal))];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new Recorded(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                body,
                request.Headers.Authorization?.ToString()));

            var path = request.RequestUri.AbsolutePath;

            if (path.Contains("/oauth2/", StringComparison.Ordinal))
            {
                return token is HttpStatusCode.OK
                    ? Json(token, """{"token_type":"Bearer","expires_in":3600,"access_token":"token-1"}""")
                    : Json(token, """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided."}""");
            }

            if (throws is not null)
            {
                throw throws;
            }

            if (api is not HttpStatusCode.OK)
            {
                return Json(api, """{"error":{"code":"Refused","message":"refused"}}""");
            }

            if (path.EndsWith("/pagedmembers", StringComparison.Ordinal))
            {
                return Json(api, """
                    {"continuationToken":null,"members":[
                      {"id":"29:other","name":"Other","email":"other@true-relevance.example","userPrincipalName":"login@true-relevance.example"},
                      {"id":"29:it","name":"IT","email":"it@true-relevance.example","userPrincipalName":"it@true-relevance.example"}]}
                    """);
            }

            if (path.EndsWith("/v3/conversations", StringComparison.Ordinal))
            {
                return body.Contains("\"isGroup\":true", StringComparison.Ordinal)
                    ? Json(api, """{"id":"19:abc@thread.tacv2;messageid=1","activityId":"1"}""")
                    : Json(api, """{"id":"a:chat"}""");
            }

            return Json(api, """{"id":"2"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class StaticOptions(NotificationOptions value) : IOptionsMonitor<NotificationOptions>
    {
        public NotificationOptions CurrentValue => value;

        public NotificationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NotificationOptions, string?> listener) => null;
    }
}
