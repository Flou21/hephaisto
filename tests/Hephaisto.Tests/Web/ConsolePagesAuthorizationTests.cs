using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Hephaisto.Agent.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hephaisto.Tests.Web;

/// <summary>
/// The console's pages with sign-in on. They are the only place a browser can be sent to the IdP
/// from - the API answers 401 - so a page has to carry a policy (without one nobody ever signs
/// in, and no approver may close anything) and that policy has to answer a browser with a
/// redirect (v0.12.0-rc7 answered 401). Real Kestrel, because the second half is about status
/// codes on the wire and metadata cannot show it.
/// </summary>
public sealed class ConsolePagesAuthorizationTests : IAsyncLifetime
{
    private const string ReaderRole = "hephaisto-reader";

    private WebApplication? app;
    private HttpClient? http;
    private int port;

    private string Base => $"http://127.0.0.1:{port}";

    private string Issuer => $"{Base}/realm";

    public async ValueTask InitializeAsync()
    {
        port = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Base);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:Authority"] = Issuer,
            ["Auth:RequireHttpsMetadata"] = "false",
            ["Auth:ClientId"] = "hephaisto",
            ["Auth:RolesClaim"] = "roles",
            ["Auth:ReaderRole"] = ReaderRole,
        });

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddHephaistoAuth(builder.Configuration);

        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The identity provider: its discovery document and an empty key set, which is all a challenge reads.
        app.MapGet("/realm/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = Issuer,
            jwks_uri = $"{Issuer}/jwks",
            authorization_endpoint = $"{Issuer}/auth",
            token_endpoint = $"{Issuer}/token",
        }));

        app.MapGet("/realm/jwks", () => Results.Text("""{"keys":[]}""", "application/json"));

        // A session without the IdP's round trip: the cookie the callback would have written.
        app.MapGet("/test/sign-in", async (HttpContext context, string? role) =>
        {
            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new Claim("preferred_username", "flo"));

            if (role is not null)
            {
                identity.AddClaim(new Claim("roles", role));
            }

            await context.SignInAsync(new ClaimsPrincipal(identity));
        });

        // One stand-in each, carrying the policy the real ones carry: the real pages need the
        // whole composition root to render, and what is under test is the door.
        app.MapGet("/page", () => "the console").RequireAuthorization(AuthenticationExtensions.PagesPolicy);
        app.MapGet("/api/thing", () => "the api").RequireAuthorization(AuthenticationExtensions.ReadPolicy);

        app.MapHephaistoConsolePages();

        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true });
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();

        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void Every_page_and_the_circuit_require_the_pages_policy()
    {
        var pages = ((IEndpointRouteBuilder)app!).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } route
                && !route.StartsWith("/realm", StringComparison.Ordinal)
                && !route.StartsWith("/test", StringComparison.Ordinal)
                && route is not "/page" and not "/api/thing")
            .ToList();

        pages.Select(e => e.RoutePattern.RawText).Should()
            .Contain("/incidents/{Id:guid}", "the incident page is where closing happens");

        pages.Should().AllSatisfy(endpoint =>
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .Should().Contain(AuthenticationExtensions.PagesPolicy, $"{endpoint.RoutePattern.RawText} is the console"));
    }

    [Theory]
    [InlineData("/page")]
    [InlineData("/")]
    [InlineData("/account")]
    [InlineData("/incidents/01a0f157-a77e-7177-ae44-eed0ab2e0554")]
    public async Task A_browser_that_is_not_signed_in_is_sent_to_the_identity_provider(string path)
    {
        using var response = await Browser(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a 401 is a blank page to a person");
        response.Headers.Location!.ToString().Should().StartWith($"{Issuer}/auth?");
    }

    [Fact]
    public async Task The_api_still_answers_401_rather_than_a_login_page()
    {
        using var response = await http!.GetAsync($"{Base}/api/thing", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_signed_in_reader_gets_the_page_and_one_without_the_role_a_403()
    {
        await http!.GetAsync($"{Base}/test/sign-in", TestContext.Current.CancellationToken);

        using (var refused = await Browser("/page"))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "signed in, without the role - not a redirect to a page that does not exist");
        }

        await http.GetAsync($"{Base}/test/sign-in?role={ReaderRole}", TestContext.Current.CancellationToken);

        using var allowed = await Browser("/page");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Browser(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Base + path);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");

        return await http!.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
