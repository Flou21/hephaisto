using System.Security.Claims;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// Who is calling the MCP endpoint, as the tools see it.
/// </summary>
/// <remarks>
/// <para>
/// Built from the claims <see cref="McpTokenHandler"/> puts on the principal - never from
/// anything in a tool's arguments. <see cref="Actor"/> is what the audit trail records: the
/// person for a person token or a signed-in user, the token itself (<c>mcp/&lt;name&gt;</c>) for
/// a shared one.
/// </para>
/// <para>
/// <see cref="Me"/> is who "me" means in a filter. For a shared token it is nobody: a gateway's
/// token is used by many people, and answering "my incidents" with the token's own would be
/// answering a question nobody asked.
/// </para>
/// </remarks>
public sealed record McpCaller(
    string Actor,
    string Token,
    string Kind,
    string Role,
    bool MayWrite)
{
    /// <summary>How a shared token is recorded: <c>mcp/litellm</c>.</summary>
    public const string SharedPrefix = "mcp/";

    /// <summary>The token name for a caller signed in through the identity provider.</summary>
    public const string IdentityProviderToken = "identity-provider";

    public const string TokenClaim = "hephaisto.mcp.token";
    public const string KindClaim = "hephaisto.mcp.kind";
    public const string RoleClaim = "hephaisto.mcp.role";
    public const string WriteClaim = "hephaisto.mcp.write";

    public bool IsShared => Kind == McpTokenOptions.Shared;

    public bool IsApprover => Role == McpTokenOptions.Approver;

    /// <summary>Who <c>me</c> means, or null for a shared token.</summary>
    public string? Me => IsShared ? null : Actor;

    /// <summary>The actor a configured token records.</summary>
    public static string ActorFor(McpTokenOptions token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return token.Kind == McpTokenOptions.Person
            ? (token.Subject ?? string.Empty).Trim()
            : SharedPrefix + token.Name;
    }

    /// <summary>The principal for a configured token.</summary>
    public static ClaimsPrincipal Principal(McpTokenOptions token, string scheme) =>
        Principal(ActorFor(token), token.Name, token.Kind, token.Role, token.MayWrite, scheme);

    public static ClaimsPrincipal Principal(string actor, string token, string kind, string role, bool mayWrite, string scheme) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, actor),
                new Claim(TokenClaim, token),
                new Claim(KindClaim, kind),
                new Claim(RoleClaim, role),
                new Claim(WriteClaim, mayWrite ? "true" : "false"),
            ],
            scheme,
            ClaimTypes.Name,
            ClaimTypes.Role));

    /// <summary>The caller, or null when the principal did not come through the MCP scheme.</summary>
    public static McpCaller? From(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true } identity
            || user.FindFirst(TokenClaim)?.Value is not { Length: > 0 } token
            || user.FindFirst(KindClaim)?.Value is not { Length: > 0 } kind
            || user.FindFirst(RoleClaim)?.Value is not { Length: > 0 } role
            || identity.Name is not { Length: > 0 } actor)
        {
            return null;
        }

        return new McpCaller(actor, token, kind, role, user.FindFirst(WriteClaim)?.Value == "true");
    }
}
