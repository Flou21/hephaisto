using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NotificationReceiver;

// A stand-in for the identity provider, for the one install that runs with sign-in on (#157).
//
// Production runs Hephaisto with sign-in on AND an MCP gateway's static token beside it, and no
// other test install covers that pair. This is just enough of an OpenID provider for the agent's
// JWT scheme: a discovery document, one RSA key, and tokens minted on request for a user and a
// role - in the shape Keycloak writes them, realm_access.roles, which the agent flattens.
//
// The key is made when the process starts, so a restart invalidates every token it minted. Which
// is fine: the pager suite mints its tokens right before it uses them.
//
//   GET /oidc/.well-known/openid-configuration
//   GET /oidc/jwks
//   GET /oidc/token?sub=<user>&role=<role|none>   {access_token, token_type, expires_in}
public static class OidcStandIn
{
    private const string Kid = "oidc-stand-in";

    public static void Map(WebApplication app, IConfiguration configuration)
    {
        // What the agent is configured with and fetches the discovery document from. Tokens carry
        // it as their issuer however they were fetched - through a port-forward, say.
        var issuer = (configuration["OIDC_ISSUER"] ?? "http://teams-stand-in.hephaisto-obs:8080/oidc").TrimEnd('/');
        var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);

        app.MapGet("/oidc/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer,
            jwks_uri = $"{issuer}/jwks",
            authorization_endpoint = $"{issuer}/auth",
            token_endpoint = $"{issuer}/token",
            userinfo_endpoint = $"{issuer}/userinfo",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));

        app.MapGet("/oidc/jwks", () => Results.Json(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = Kid, n = B64(p.Modulus!), e = B64(p.Exponent!) },
            },
        }));

        app.MapGet("/oidc/token", (string sub, string? role) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new JsonObject
            {
                ["iss"] = issuer,
                ["sub"] = sub,
                ["preferred_username"] = sub,
                ["aud"] = "account",
                ["azp"] = "hephaisto",
                ["iat"] = now,
                ["nbf"] = now - 30,
                ["exp"] = now + 600,
            };

            if (!string.IsNullOrWhiteSpace(role) && role != "none")
            {
                payload["realm_access"] = new JsonObject { ["roles"] = new JsonArray(role) };
            }

            var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = Kid };
            var unsigned = $"{B64(Encoding.UTF8.GetBytes(header.ToJsonString()))}.{B64(Encoding.UTF8.GetBytes(payload.ToJsonString()))}";
            var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            Console.WriteLine($"OIDC minted a token for {sub} ({role ?? "no role"})");

            return Results.Json(new { access_token = $"{unsigned}.{B64(signature)}", token_type = "Bearer", expires_in = 600 });
        });
    }

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
