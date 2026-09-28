using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The one route Microsoft calls (#124). What is load-bearing is that every refusal happens
/// before anything changes: a token for another bot, a key endorsed for another channel, an
/// activity from another tenant or service, and a clicker who is not in the team.
/// </summary>
public sealed class TeamsBotActionsTests
{
    private const string AppId = "00000000-0000-0000-0000-0000000000d2";
    private const string Tenant = "00000000-0000-0000-0000-0000000000d1";
    private const string Issuer = "https://api.botframework.com";
    private const string ServiceUrl = "https://smba.example/teams/";
    private const string ObjectId = "5f0c0000-0000-0000-0000-000000000001";

    private static readonly Guid IncidentId = Guid.Parse("0192a6f0-0000-7000-8000-0000000000aa");

    private static readonly TeamsMember Member = new("29:oncall", ObjectId, "On Call", "oncall@example.com", "oncall@example.com");

    // ---------------------------------------------------------------------------------------
    // The token
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_for_this_bot_from_the_bot_framework_is_valid()
    {
        using var key = RSA.Create(2048);
        var token = Sign(key, "msteams-key", audience: AppId, issuer: Issuer);

        var result = await Validate(token, key, "msteams-key");

        result.IsValid.Should().BeTrue(result.Exception?.Message);
    }

    [Theory]
    [InlineData("another-bot", Issuer, "a token issued for another bot")]
    [InlineData(AppId, "https://login.example.com", "a token from another issuer")]
    public async Task A_token_for_something_else_is_refused(string audience, string issuer, string because)
    {
        using var key = RSA.Create(2048);
        var token = Sign(key, "msteams-key", audience, issuer);

        var result = await Validate(token, key, "msteams-key");

        result.IsValid.Should().BeFalse(because);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_nobody_published_is_refused()
    {
        using var published = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var token = Sign(other, "msteams-key", AppId, Issuer);

        var result = await Validate(token, published, "msteams-key");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Only_a_key_endorsed_for_teams_may_sign_a_click()
    {
        // The key set is shared by every Bot Framework channel. A generic JWT library passes a
        // token signed by any of them; the endorsement is the check that makes it Teams.
        var keys = new JsonWebKeySet("""
            {"keys":[
              {"kty":"RSA","kid":"teams","n":"AQAB","e":"AQAB","endorsements":["msteams","skype"]},
              {"kty":"RSA","kid":"webchat","n":"AQAB","e":"AQAB","endorsements":["webchat"]},
              {"kty":"RSA","kid":"bare","n":"AQAB","e":"AQAB"}
            ]}
            """);

        BotFrameworkTokens.IsEndorsed(keys, "teams", "msteams").Should().BeTrue();
        BotFrameworkTokens.IsEndorsed(keys, "webchat", "msteams").Should().BeFalse("endorsed for another channel");
        BotFrameworkTokens.IsEndorsed(keys, "bare", "msteams").Should().BeFalse("endorsed for nothing is not endorsed for Teams");
        BotFrameworkTokens.IsEndorsed(keys, "unknown", "msteams").Should().BeFalse("a key id nobody published");
        BotFrameworkTokens.IsEndorsed(keys, null, "msteams").Should().BeFalse("a token without a key id");
        BotFrameworkTokens.IsEndorsed(null, "teams", "msteams").Should().BeFalse("no key set at all");
    }

    // ---------------------------------------------------------------------------------------
    // The click
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_click_by_a_member_acknowledges_as_the_roster_names_them_and_shows_the_card()
    {
        var (handler, target, _) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge, displayName: "Somebody Else"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");

        // The display name in the click is the sender's to choose. The roster's is not.
        await target.Received(1).AcknowledgeAsync(IncidentId, "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TeamsBotVerbs.Acknowledge)]
    [InlineData(TeamsBotVerbs.AssignToMe)]
    public async Task Every_verb_a_button_can_send_changes_the_incident(string verb)
    {
        TeamsBotVerbs.All.Should().Contain(verb);

        var (handler, target, _) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(verb), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["statusCode"]!.GetValue<int>().Should().Be(200);
        target.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name != nameof(ITeamsActionTarget.CardAsync));
    }

    [Fact]
    public async Task Assign_to_me_assigns_to_the_clicker()
    {
        var (handler, target, _) = Handler();

        await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.AssignToMe), TestContext.Current.CancellationToken);

        await target.Received(1).AssignToAsync(IncidentId, "oncall@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_token_for_another_service_is_refused_before_anything_changes()
    {
        var (handler, target, members) = Handler();

        var answer = await handler.HandleAsync(Caller(serviceUrl: "https://elsewhere.example/"), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
        members.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_from_another_tenant_is_refused_before_anything_changes()
    {
        var (handler, target, members) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge, tenant: "another-tenant"), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
        members.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_by_somebody_outside_the_team_is_refused()
    {
        var (handler, target, _) = Handler(member: new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, null, "not a member"));

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_roster_nobody_could_read_is_not_a_yes()
    {
        var (handler, target, _) = Handler(member: new TeamsBotResult<TeamsMember>(HttpStatusCode.BadGateway, null, "down"));

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(503);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_without_an_object_id_is_refused()
    {
        var (handler, target, _) = Handler();
        var click = Click(TeamsBotVerbs.Acknowledge);
        click["from"]!.AsObject().Remove("aadObjectId");

        var answer = await handler.HandleAsync(Caller(), click, TestContext.Current.CancellationToken);

        answer.Status.Should().Be(403);
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("close")]
    [InlineData("approve")]
    [InlineData("")]
    public async Task A_verb_no_button_sends_changes_nothing(string verb)
    {
        var (handler, target, _) = Handler();

        var answer = await handler.HandleAsync(Caller(), Click(verb), TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body!["type"]!.GetValue<string>().Should().Be("application/vnd.microsoft.error");
        target.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Anything_that_is_not_a_click_changes_nothing()
    {
        var (handler, target, members) = Handler();
        var install = Click(TeamsBotVerbs.Acknowledge);
        install["type"] = "conversationUpdate";

        var answer = await handler.HandleAsync(Caller(), install, TestContext.Current.CancellationToken);

        answer.Status.Should().Be(200);
        answer.Body.Should().BeNull();
        target.ReceivedCalls().Should().BeEmpty();
        members.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_closed_incident_says_nothing_changed()
    {
        var (handler, target, _) = Handler();
        target.AcknowledgeAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(
            new LifecycleResult { Outcome = LifecycleOutcome.IllegalState, Detail = "already closed" });
        target.CardAsync(default, TestContext.Current.CancellationToken).ReturnsForAnyArgs((JsonObject?)null);

        var answer = await handler.HandleAsync(Caller(), Click(TeamsBotVerbs.Acknowledge), TestContext.Current.CancellationToken);

        answer.Body!["value"]!.GetValue<string>().Should().Contain("Nothing changed").And.Contain("already closed");
    }

    // ---------------------------------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Off_by_default_registers_nothing()
    {
        new TeamsBotActionsOptions().Enabled.Should().BeFalse();

        var services = new ServiceCollection();
        services.AddHephaistoTeamsBotActions(Config());

        services.Should().NotContain(d => d.ServiceType == typeof(TeamsBotActionHandler));
    }

    [Fact]
    public void Buttons_without_a_bot_refuse_to_start()
    {
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(("Notifications:TeamsBot:Actions:Enabled", "true")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Theory]
    [InlineData("8080", "0")]
    [InlineData("8081", "8081")]
    public void The_actions_port_must_be_its_own(string port, string webhookPort)
    {
        var act = () => new ServiceCollection().AddHephaistoTeamsBotActions(Config(
            [.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true"),
             ("Notifications:TeamsBot:Actions:Port", port), ("Web:WebhookPort", webhookPort)]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*port of its own*");
    }

    [Fact]
    public void A_configured_bot_with_its_own_port_registers_the_route()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHephaistoTeamsBotActions(Config([.. Bot(), ("Notifications:TeamsBot:Actions:Enabled", "true")]));

        services.Should().Contain(d => d.ServiceType == typeof(TeamsBotActionHandler));
    }

    // ---------------------------------------------------------------------------------------

    private static (TeamsBotActionHandler Handler, ITeamsActionTarget Target, ITeamsMemberDirectory Members) Handler(
        TeamsBotResult<TeamsMember>? member = null)
    {
        var options = Substitute.For<IOptionsMonitor<NotificationOptions>>();
        options.CurrentValue.Returns(new NotificationOptions
        {
            TeamsBot = new TeamsBotOptions { TenantId = Tenant, AppId = AppId, Actions = new TeamsBotActionsOptions { Enabled = true } },
        });

        var members = Substitute.For<ITeamsMemberDirectory>();
        members.FindAsync(default!, default).ReturnsForAnyArgs(member ?? new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, Member, null));

        var target = Substitute.For<ITeamsActionTarget>();
        target.AcknowledgeAsync(default, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.AssignToAsync(default, default!, default).ReturnsForAnyArgs(new LifecycleResult { Outcome = LifecycleOutcome.Applied });
        target.CardAsync(default, default).ReturnsForAnyArgs(new JsonObject { ["type"] = "AdaptiveCard" });

        return (new TeamsBotActionHandler(options, members, target, NullLogger<TeamsBotActionHandler>.Instance), target, members);
    }

    private static ClaimsPrincipal Caller(string serviceUrl = ServiceUrl) =>
        new(new ClaimsIdentity([new Claim(BotFrameworkTokens.ServiceUrlClaim, serviceUrl)], BotFrameworkTokens.Scheme));

    private static JsonObject Click(string verb, string tenant = Tenant, string displayName = "On Call") => new()
    {
        ["type"] = "invoke",
        ["name"] = "adaptiveCard/action",
        ["channelId"] = "msteams",
        ["serviceUrl"] = ServiceUrl.TrimEnd('/'),
        ["from"] = new JsonObject { ["id"] = "29:oncall", ["aadObjectId"] = ObjectId, ["name"] = displayName },
        ["conversation"] = new JsonObject { ["id"] = "a:oncall" },
        ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = tenant } },
        ["value"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "Action.Execute",
                ["verb"] = verb,
                ["data"] = new JsonObject { ["incidentId"] = IncidentId.ToString() },
            },
        },
    };

    private static (string, string?)[] Bot() =>
    [
        ("Notifications:TeamsBot:TenantId", Tenant),
        ("Notifications:TeamsBot:AppId", AppId),
        ("Notifications:TeamsBot:ClientSecret", "not-a-secret"),
        ("Notifications:TeamsBot:ChannelId", "19:c@thread.tacv2"),
    ];

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string Sign(RSA key, string kid, string audience, string issuer)
    {
        var handler = new JsonWebTokenHandler();

        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Claims = new Dictionary<string, object> { [BotFrameworkTokens.ServiceUrlClaim] = ServiceUrl },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });
    }

    private static Task<TokenValidationResult> Validate(string token, RSA published, string kid)
    {
        var parameters = BotFrameworkTokens.Parameters(AppId, Issuer);
        parameters.IssuerSigningKey = new RsaSecurityKey(published.ExportParameters(false)) { KeyId = kid };

        return new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }
}
