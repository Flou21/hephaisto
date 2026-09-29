using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hephaisto.Agent.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// The MCP endpoint's authentication: a configured token, or - with sign-in on - the identity
/// provider's bearer token. Its own scheme, never a default, and only on <c>/mcp</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every configured token is compared, every time.</b> Both sides are SHA-256 hashed first,
/// as <see cref="Web.WebhookTokenFilter"/> does, so the comparison is constant-time whatever
/// length arrived; and the loop does not stop at a match, so how long a request takes does not
/// say how far down the list its token is.
/// </para>
/// <para>
/// <b>A signed-in user is a reader unless the approver role is configured and held.</b> The
/// console treats an empty approver role as "anyone signed in may approve", which is a
/// defensible setting for people. For a model it is not, so here an empty role means nobody is an
/// approver through sign-in - a Secret token with <c>role: approver</c> still is.
/// </para>
/// <para>
/// A missing and a wrong credential are answered the same way: 401, <c>WWW-Authenticate:
/// Bearer</c>, no body. Why is logged and counted, never returned.
/// </para>
/// </remarks>
public sealed class McpTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<McpOptions> mcp,
    IOptionsMonitor<AuthOptions> auth,
    HephaistoMetrics metrics)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme. Its own, so no console or API request is ever run past it.</summary>
    public const string SchemeName = "McpToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header))
        {
            metrics.McpAuthRefused("missing");
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            metrics.McpAuthRefused("not_bearer");
            return AuthenticateResult.Fail("not a bearer token");
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        var o = mcp.CurrentValue;
        McpTokenOptions? match = null;

        foreach (var token in o.Tokens)
        {
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes((token.Value ?? string.Empty).Trim()));

            if (CryptographicOperations.FixedTimeEquals(presented, expected) && !string.IsNullOrWhiteSpace(token.Value))
            {
                match ??= token;
            }
        }

        if (match is not null)
        {
            return AuthenticateResult.Success(new AuthenticationTicket(McpCaller.Principal(match, SchemeName), SchemeName));
        }

        var a = auth.CurrentValue;

        if (a.Enabled && o.AcceptIdentityProviderTokens)
        {
            var signedIn = await Context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme).ConfigureAwait(false);

            if (signedIn.Succeeded && signedIn.Principal is { } principal)
            {
                return FromIdentityProvider(principal, a);
            }
        }

        metrics.McpAuthRefused("unknown_token");
        return AuthenticateResult.Fail("a token this endpoint does not know");
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    /// <summary>
    /// A role under the configured claim - where Keycloak's realm roles are flattened to - or under
    /// the standard role claim, which is where the JWT handler's inbound mapping puts a top-level
    /// <c>roles</c> claim before anything here reads it.
    /// </summary>
    private static bool Holds(ClaimsPrincipal principal, AuthOptions a, string role) =>
        principal.HasClaim(a.RolesClaim, role) || principal.HasClaim(ClaimTypes.Role, role);

    private AuthenticateResult FromIdentityProvider(ClaimsPrincipal principal, AuthOptions a)
    {
        var name = principal.Identity?.Name?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            metrics.McpAuthRefused("no_name");
            return AuthenticateResult.Fail("the signed-in token names nobody");
        }

        if (McpOptions.IsReserved(name))
        {
            metrics.McpAuthRefused("reserved_name");
            Logger.LogWarning("An MCP request signed in as the reserved name {Name} was refused.", name);
            return AuthenticateResult.Fail("a reserved name");
        }

        if (!string.IsNullOrWhiteSpace(a.ReaderRole) && !Holds(principal, a, a.ReaderRole))
        {
            metrics.McpAuthRefused("no_reader_role");
            return AuthenticateResult.Fail("the signed-in user does not hold the reader role");
        }

        var approver = !string.IsNullOrWhiteSpace(a.ApproverRole) && Holds(principal, a, a.ApproverRole);

        return AuthenticateResult.Success(new AuthenticationTicket(
            McpCaller.Principal(
                name,
                McpCaller.IdentityProviderToken,
                McpTokenOptions.Person,
                approver ? McpTokenOptions.Approver : McpTokenOptions.Reader,
                mayWrite: true,
                SchemeName),
            SchemeName));
    }
}
