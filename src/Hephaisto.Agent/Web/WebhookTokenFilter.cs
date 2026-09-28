using System.Security.Cryptography;
using System.Text;

namespace Hephaisto.Agent.Web;

/// <summary>
/// Refuses a webhook delivery that does not carry the configured bearer token (backlog #138).
/// </summary>
/// <remarks>
/// <para>
/// For five releases the code said Alertmanager cannot send a credential. It can:
/// <c>http_config.authorization</c> (and <c>credentials_file</c>) has existed on every receiver
/// for years. What was missing was the code to check one, so the NetworkPolicy was the webhook's
/// entire protection - and with <c>webhookPort: 0</c> an Ingress for the console was an Ingress
/// for the webhook. As the only incident system a forged alert does not just cost an
/// investigation; it sends a person a message in the agent's name.
/// </para>
/// <para>
/// <b>Both sides are hashed before they are compared.</b> <see cref="CryptographicOperations.FixedTimeEquals"/>
/// is constant-time only for inputs of equal length, and returns early on a length mismatch -
/// which would leak the token's length. SHA-256 of each makes both 32 bytes, whatever arrived.
/// </para>
/// <para>
/// Optional, because turning it on is a change to every Alertmanager receiver in the same
/// commit: a token the receiver does not send refuses every alert, and Alertmanager's retry does
/// not fix that. Without a token the startup log says the webhook is unauthenticated.
/// </para>
/// </remarks>
public static class WebhookTokenFilter
{
    /// <summary>The shortest token accepted, so a placeholder cannot pass for a credential.</summary>
    public const int MinimumLength = 16;

    /// <summary>An endpoint filter that answers 401 unless the request carries <paramref name="token"/>.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return async (context, next) =>
        {
            if (IsAuthorized(context.HttpContext.Request.Headers.Authorization.ToString(), expected))
            {
                return await next(context);
            }

            // The challenge is the whole body: nothing about why, so a caller probing for the
            // token learns nothing from the difference between a missing and a wrong one.
            context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Unauthorized();
        };
    }

    /// <summary>Whether an <c>Authorization</c> header value carries the token hashed as <paramref name="expectedHash"/>.</summary>
    public static bool IsAuthorized(string? authorization, byte[] expectedHash)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);

        const string scheme = "Bearer ";

        if (string.IsNullOrEmpty(authorization)
            || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[scheme.Length..].Trim()));

        return CryptographicOperations.FixedTimeEquals(presented, expectedHash);
    }
}
