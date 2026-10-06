using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Agent.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The route as Microsoft reaches it: real Kestrel, the real Bot Framework scheme reading a key
/// document over HTTP, the real policy and the port filter. The handler's own rules are in
/// <see cref="TeamsBotActionsTests"/>; this proves the parts only a running pipeline can - that
/// the endorsement is checked by the scheme and not only by a helper, that the <c>serviceurl</c>
/// claim arrives under that name, and that nothing answers without a token.
/// </summary>
public sealed class TeamsBotActionsRouteTests : IAsyncLifetime
{
    private const string AppId = "00000000-0000-0000-0000-0000000000d2";
    private const string Tenant = "00000000-0000-0000-0000-0000000000d1";
    private const string ServiceUrl = "https://smba.example/teams";

    /// <summary>Two people the roster knows. Only the first is mapped to the approver role.</summary>
    private const string Approver = "5f0c0000-0000-0000-0000-000000000001";
    private const string Member = "5f0c0000-0000-0000-0000-000000000002";

    private readonly RSA teams = RSA.Create(2048);
    private readonly RSA webchat = RSA.Create(2048);
    private readonly ITeamsActionTarget target = Substitute.For<ITeamsActionTarget>();
    private WebApplication? app;
    private HttpClient? http;
    private int actionsPort;
    private int otherPort;

    public async ValueTask InitializeAsync()
    {
        actionsPort = FreePort();
        otherPort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{actionsPort}", $"http://127.0.0.1:{otherPort}");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:TeamsBot:TenantId"] = Tenant,
            ["Notifications:TeamsBot:AppId"] = AppId,
            ["Notifications:TeamsBot:ClientSecret"] = "not-a-secret",
            ["Notifications:TeamsBot:ChannelId"] = "19:c@thread.tacv2",
            ["Notifications:TeamsBot:Actions:Enabled"] = "true",
            ["Notifications:TeamsBot:Actions:Port"] = actionsPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Notifications:TeamsBot:Actions:RequireHttpsMetadata"] = "false",
            ["Notifications:TeamsBot:Actions:OpenIdMetadataUrl"] = $"http://127.0.0.1:{otherPort}/openid",
            ["Notifications:TeamsBot:Actions:Approvers:0"] = Approver,
        });

        builder.Services.AddOptions<Hephaisto.Core.Notifications.NotificationOptions>()
            .BindConfiguration(Hephaisto.Core.Notifications.NotificationOptions.SectionName);

        var members = Substitute.For<ITeamsMemberDirectory>();
        members.FindAsync(default!, default).ReturnsForAnyArgs(new TeamsBotResult<TeamsMember>(
            HttpStatusCode.OK, new TeamsMember("29:oncall", "oid-1", "On Call", "oncall@example.com", "oncall@example.com"), null));
        target.AcknowledgeAsync(default, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.ReinvestigateAsync(default, default!, default).ReturnsForAnyArgs(new ReinvestigateResult { Outcome = ReinvestigateOutcome.Queued });
        target.CloseAsync(default, default!, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.ApproveAsync(default, default, default!, default).ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.Executed });
        target.DenyAsync(default, default, default!, default).ReturnsForAnyArgs(new ApprovalResult { Outcome = ApprovalOutcome.Denied });
        target.CardAsync(default, default).ReturnsForAnyArgs(new JsonObject { ["type"] = "AdaptiveCard" });

        builder.Services.AddSingleton(members);
        builder.Services.AddSingleton(target);
        builder.Services.AddAuthorization();
        builder.Services.AddHephaistoTeamsBotActions(builder.Configuration);

        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The Bot Framework's key document, on the other port: one key endorsed for Teams, one
        // for another channel. Both are genuine keys the issuer published.
        app.MapGet("/openid", () => Results.Json(new
        {
            issuer = "https://api.botframework.com",
            jwks_uri = $"http://127.0.0.1:{otherPort}/keys",
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));
        app.MapGet("/keys", () => Results.Text(
            $$"""{"keys":[{{Jwk(teams, "teams", "msteams")}},{{Jwk(webchat, "webchat", "webchat")}}]}""",
            "application/json"));

        app.MapTeamsBotActions(actionsPort);

        await app.StartAsync();
        http = new HttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();

        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        teams.Dispose();
        webchat.Dispose();
    }

    [Fact]
    public async Task A_click_signed_by_a_teams_key_acknowledges()
    {
        var response = await Post(actionsPort, Sign(teams, "teams"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await target.Received(1).AcknowledgeAsync(Arg.Any<Guid>(), "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_signed_close_by_a_mapped_approver_closes_with_the_reason_from_the_card()
    {
        // The approver map as the pod reads it: bound from configuration, not handed to the
        // handler by a test. The reason arrives merged into the button's data.
        var response = await Post(actionsPort, Sign(teams, "teams"), Click(TeamsBotVerbs.Close, Approver, reason: "the rollout finished"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await target.Received(1).CloseAsync(Arg.Any<Guid>(), "oncall@example.com", "the rollout finished", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_signed_close_by_a_member_who_is_not_mapped_changes_nothing_and_says_why()
    {
        var response = await Post(actionsPort, Sign(teams, "teams"), Click(TeamsBotVerbs.Close, Member, reason: "the rollout finished"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.activity.message");
        body["value"]!.GetValue<string>().Should().Contain("approver role");
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_reinvestigate_needs_no_approver()
    {
        var response = await Post(actionsPort, Sign(teams, "teams"), Click(TeamsBotVerbs.Reinvestigate, Member));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await target.Received(1).ReinvestigateAsync(Arg.Any<Guid>(), "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TeamsBotVerbs.Approve)]
    [InlineData(TeamsBotVerbs.Deny)]
    public async Task A_signed_decision_is_refused_while_approvals_are_off_as_this_install_started(string verb)
    {
        // This pipeline was configured with the buttons on and an approver mapped, and without
        // Approvals:Enabled - the default. A correctly signed click by that approver, for a
        // well-formed action id, still decides nothing.
        var response = await Post(actionsPort, Sign(teams, "teams"), Click(verb, Approver, actionId: Guid.NewGuid().ToString()));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["value"]!.GetValue<string>().Should().Contain("switched off");
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task With_approvals_on_a_signed_approve_by_a_mapped_approver_approves_that_action()
    {
        SwitchApprovalsOn();
        var action = Guid.NewGuid();

        var response = await Post(actionsPort, Sign(teams, "teams"), Click(TeamsBotVerbs.Approve, Approver, actionId: action.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await target.Received(1).ApproveAsync(Arg.Any<Guid>(), action, "oncall@example.com", Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().DenyAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task With_approvals_on_a_signed_deny_denies_and_a_member_who_is_not_mapped_decides_nothing()
    {
        SwitchApprovalsOn();
        var action = Guid.NewGuid();

        var refused = await Post(actionsPort, Sign(teams, "teams"), Click(TeamsBotVerbs.Deny, Member, actionId: action.ToString()));
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body!["value"]!.GetValue<string>().Should().Contain("approver role");
        target.ReceivedCalls().Should().BeEmpty();

        var response = await Post(actionsPort, Sign(teams, "teams"), Click(TeamsBotVerbs.Deny, Approver, actionId: action.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await target.Received(1).DenyAsync(Arg.Any<Guid>(), action, "oncall@example.com", Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().ApproveAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(TeamsBotVerbs.Reinvestigate)]
    [InlineData(TeamsBotVerbs.Close)]
    [InlineData(TeamsBotVerbs.Approve)]
    [InlineData(TeamsBotVerbs.Deny)]
    public async Task No_new_verb_is_answered_without_a_token_or_on_another_port(string verb)
    {
        SwitchApprovalsOn();
        var click = Click(verb, Approver, reason: "the rollout finished", actionId: Guid.NewGuid().ToString());

        (await Post(actionsPort, token: null, click)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Post(actionsPort, Sign(webchat, "webchat"), click)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Post(otherPort, Sign(teams, "teams"), click)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_signed_by_a_key_endorsed_for_another_channel_changes_nothing()
    {
        var response = await Post(actionsPort, Sign(webchat, "webchat"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_signed_for_another_bot_changes_nothing()
    {
        var response = await Post(actionsPort, Sign(teams, "teams", audience: "another-bot"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_with_no_token_changes_nothing()
    {
        var response = await Post(actionsPort, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task The_route_does_not_answer_on_any_other_port()
    {
        var response = await Post(otherPort, Sign(teams, "teams"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        target.ReceivedCalls().Should().BeEmpty();
    }

    /// <summary>
    /// As if this install had started with <c>Actions:Approvals:Enabled</c>. The options monitor
    /// hands every reader the same instance, so the handler sees it; that the setting binds from
    /// configuration and what startup refuses about it are <see cref="TeamsBotActionsTests"/>'s.
    /// </summary>
    private void SwitchApprovalsOn() =>
        app!.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Hephaisto.Core.Notifications.NotificationOptions>>()
            .CurrentValue.TeamsBot.Actions.Approvals.Enabled = true;

    private async Task<HttpResponseMessage> Post(int port, string? token, JsonObject? click = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{TeamsBotActionsExtensions.Route}")
        {
            Content = new StringContent((click ?? Click()).ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http!.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static JsonObject Click(
        string verb = TeamsBotVerbs.Acknowledge,
        string objectId = Member,
        string? reason = null,
        string? actionId = null)
    {
        var data = new JsonObject { ["incidentId"] = Guid.NewGuid().ToString() };

        if (reason is not null)
        {
            data[TeamsBotVerbs.ReasonInput] = reason;
        }

        if (actionId is not null)
        {
            data[TeamsBotVerbs.ActionId] = actionId;
        }

        return new JsonObject
        {
            ["type"] = "invoke",
            ["name"] = "adaptiveCard/action",
            ["serviceUrl"] = ServiceUrl,
            ["from"] = new JsonObject { ["id"] = "29:oncall", ["aadObjectId"] = objectId },
            ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = Tenant } },
            ["value"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["verb"] = verb,
                    ["data"] = data,
                },
            },
        };
    }

    private static string Sign(RSA key, string kid, string audience = AppId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://api.botframework.com",
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { [BotFrameworkTokens.ServiceUrlClaim] = ServiceUrl },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });

    private static string Jwk(RSA key, string kid, string channel)
    {
        var p = key.ExportParameters(false);

        return new JsonObject
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["kid"] = kid,
            ["n"] = Base64UrlEncoder.Encode(p.Modulus),
            ["e"] = Base64UrlEncoder.Encode(p.Exponent),
            ["endorsements"] = new JsonArray(channel),
        }.ToJsonString();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
