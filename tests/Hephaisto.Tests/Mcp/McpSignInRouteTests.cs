using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hephaisto.Agent;
using Hephaisto.Agent.Mcp;
using Hephaisto.Agent.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// The endpoint with sign-in on (#157, F1): the identity provider's bearer tokens and a Secret's
/// static token, side by side - which is how production runs, a gateway's token next to people
/// signed in. Real Kestrel, the real JWT scheme reading a key document over HTTP from a second
/// port, keys made here.
/// </summary>
public sealed class McpSignInRouteTests : IAsyncLifetime
{
    private const string Static = "static-token-static-token-static-token-01";
    private const string ApproverRole = "hephaisto-approver";

    private readonly RSA idp = RSA.Create(2048);
    private readonly RSA stranger = RSA.Create(2048);
    private WebApplication? app;
    private HttpClient? http;
    private int mcpPort;
    private int idpPort;

    private string Issuer => $"http://127.0.0.1:{idpPort}/realm";

    public async ValueTask InitializeAsync()
    {
        mcpPort = FreePort();
        idpPort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{mcpPort}", $"http://127.0.0.1:{idpPort}");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:Authority"] = Issuer,
            ["Auth:RequireHttpsMetadata"] = "false",
            ["Auth:ClientId"] = "hephaisto",
            ["Auth:RolesClaim"] = "roles",
            ["Auth:ApproverRole"] = ApproverRole,
            ["Mcp:Enabled"] = "true",
            ["Mcp:Port"] = mcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mcp:Tokens:0:Name"] = "gateway",
            ["Mcp:Tokens:0:Value"] = Static,
        });

        builder.Services.AddMetrics();
        builder.Services.AddSingleton<HephaistoMetrics>();
        builder.Services.AddHephaistoAuth(builder.Configuration);
        builder.Services.AddHephaistoMcp(builder.Configuration);

        app = builder.Build();
        app.UseMcpPort();
        app.UseAuthentication();
        app.UseAuthorization();

        // The identity provider, on the other port: discovery and its one key.
        app.MapGet("/realm/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = Issuer,
            jwks_uri = $"{Issuer}/jwks",
            authorization_endpoint = $"{Issuer}/auth",
            token_endpoint = $"{Issuer}/token",
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));
        app.MapGet("/realm/jwks", () => Results.Text($$"""{"keys":[{{Jwk(idp, "k1")}}]}""", "application/json"));

        app.MapHephaistoMcp();

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

        idp.Dispose();
        stranger.Dispose();
    }

    [Fact]
    public async Task A_signed_in_approver_is_their_person_with_the_approver_role()
    {
        var who = await Identity(Token("pager-person", roles: [ApproverRole]));

        who!["name"]!.GetValue<string>().Should().Be("pager-person");
        who["kind"]!.GetValue<string>().Should().Be("person");
        who["role"]!.GetValue<string>().Should().Be("approver");
        who["me"]!.GetValue<string>().Should().Be("pager-person");
    }

    [Fact]
    public async Task Keycloak_realm_roles_count_too()
    {
        var who = await Identity(Token("pager-person", realmRoles: [ApproverRole]));

        who!["role"]!.GetValue<string>().Should().Be("approver");
    }

    [Fact]
    public async Task A_signed_in_user_without_the_approver_role_is_a_reader()
    {
        (await Identity(Token("pager-guest", roles: ["somebody-else"])))!["role"]!.GetValue<string>().Should().Be("reader");
    }

    [Fact]
    public async Task The_static_token_still_gets_in_with_sign_in_on()
    {
        (await Identity(Static))!["name"]!.GetValue<string>().Should().Be("mcp/gateway");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("another-key")]
    [InlineData("reserved-name")]
    [InlineData("none")]
    public async Task What_the_identity_provider_did_not_sign_for_this_realm_is_refused(string what)
    {
        var token = what switch
        {
            "expired" => Token("pager-person", expires: DateTime.UtcNow.AddMinutes(-10)),
            "wrong-issuer" => Token("pager-person", issuer: "http://127.0.0.1:1/other"),
            "another-key" => Token("pager-person", key: stranger),
            "reserved-name" => Token("hephaisto/model", roles: [ApproverRole]),
            _ => null,
        };

        (await Rpc(token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<JsonObject?> Identity(string token)
    {
        var response = await Rpc(token, "tools/call", new JsonObject { ["name"] = "get_caller_identity", ["arguments"] = new JsonObject() });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var data = body.Split('\n').Last(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];

        return JsonNode.Parse(JsonNode.Parse(data)!["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
    }

    private async Task<HttpResponseMessage> Rpc(string? token, string method = "tools/list", JsonObject? parameters = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{mcpPort}{McpOptions.Route}")
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http!.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private string Token(
        string user,
        string[]? roles = null,
        string[]? realmRoles = null,
        DateTime? expires = null,
        string? issuer = null,
        RSA? key = null)
    {
        var claims = new Dictionary<string, object> { ["preferred_username"] = user };

        if (roles is not null)
        {
            claims["roles"] = roles;
        }

        if (realmRoles is not null)
        {
            claims["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles };
        }

        var exp = expires ?? DateTime.UtcNow.AddMinutes(10);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = "account",
            IssuedAt = exp.AddMinutes(-20),
            NotBefore = exp.AddMinutes(-20),
            Expires = exp,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? idp) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
    }

    private static string Jwk(RSA key, string kid)
    {
        var p = key.ExportParameters(false);

        return new JsonObject
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = kid,
            ["n"] = Base64UrlEncoder.Encode(p.Modulus),
            ["e"] = Base64UrlEncoder.Encode(p.Exponent),
        }.ToJsonString();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
