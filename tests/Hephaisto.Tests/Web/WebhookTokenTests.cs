using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Options;
using Hephaisto.Agent.Web;

namespace Hephaisto.Tests.Web;

/// <summary>
/// The webhook can check a credential (backlog #138).
/// </summary>
/// <remarks>
/// The code said for five releases that Alertmanager cannot send one. It can -
/// <c>http_config.authorization</c> - and what was missing was this.
/// </remarks>
public sealed class WebhookTokenTests
{
    private const string Token = "a-webhook-token-of-32-characters";

    private static readonly byte[] Expected = SHA256.HashData(Encoding.UTF8.GetBytes(Token));

    [Fact]
    public void The_right_bearer_token_passes() =>
        WebhookTokenFilter.IsAuthorized($"Bearer {Token}", Expected).Should().BeTrue();

    [Fact]
    public void The_scheme_is_case_insensitive_as_rfc_9110_says() =>
        WebhookTokenFilter.IsAuthorized($"bearer {Token}", Expected).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Bearer a-webhook-token-of-32-characterS")]
    [InlineData("Bearer a-webhook-token-of-32-characters-and-more")]
    [InlineData("Basic YWxlcnRtYW5hZ2VyOnNlY3JldA==")]
    [InlineData("a-webhook-token-of-32-characters")]
    public void Anything_else_is_refused(string? header) =>
        WebhookTokenFilter.IsAuthorized(header, Expected).Should().BeFalse();

    [Fact]
    public async Task A_refused_request_is_a_401_with_a_bearer_challenge_and_never_reaches_the_handler()
    {
        var http = new DefaultHttpContext();
        var reached = false;

        var result = await WebhookTokenFilter.Require(Token)(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                reached = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        reached.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        http.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
    }

    [Fact]
    public async Task An_accepted_request_reaches_the_handler()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {Token}";
        var reached = false;

        await WebhookTokenFilter.Require(Token)(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                reached = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        reached.Should().BeTrue();
    }

    /// <summary>A placeholder is not a credential.</summary>
    [Fact]
    public void A_short_token_refuses_to_start()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddHephaistoWeb();
        services.Configure<WebOptions>(o => o.WebhookToken = "changeme");
        using var sp = services.BuildServiceProvider();

        var read = () => sp.GetRequiredService<IOptions<WebOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*at least 16*");
    }

    [Fact]
    public void No_token_is_still_a_valid_configuration() =>
        new WebOptions().WebhookToken.Should().BeNull();
}
