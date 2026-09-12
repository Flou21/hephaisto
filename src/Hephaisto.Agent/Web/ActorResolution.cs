using System.Security.Claims;

namespace Hephaisto.Agent.Web;

/// <summary>
/// Who is doing this, taken from the token when there is one (#110).
/// </summary>
/// <remarks>
/// <para>
/// Every human action in this system - approving, denying, closing, acknowledging, retrying,
/// submitting feedback - writes an actor into <c>audit_events</c>, and until OIDC existed that
/// actor was a string somebody typed into a form. The audit trail's integrity story was already
/// careful: the app serves as a non-owner Postgres role that holds INSERT but not UPDATE or
/// DELETE on that table. The immutability was real; the identity was not.
/// </para>
/// <para>
/// <b>An authenticated request ignores the body's actor entirely.</b> Not "prefers the token" -
/// ignores. Falling back to a supplied name when the claim is missing would leave the obvious
/// way to act as somebody else permanently open, and it would be indistinguishable in the audit
/// trail from the real thing.
/// </para>
/// </remarks>
public static class ActorResolution
{
    /// <summary>
    /// The signed-in subject, or the supplied name when nobody is signed in.
    /// </summary>
    /// <param name="user">The request's principal.</param>
    /// <param name="supplied">The name from the request body. Used only when unauthenticated.</param>
    /// <returns>
    /// The actor to record, or null when there is neither - which the caller turns into a
    /// validation error rather than an anonymous audit row.
    /// </returns>
    public static string? Resolve(ClaimsPrincipal? user, string? supplied)
    {
        if (user?.Identity?.IsAuthenticated == true)
        {
            // preferred_username first because it is what a person recognises as their own name;
            // the subject is a UUID, correct but unreadable in an incident timeline. Name is the
            // configured NameClaimType and so is usually the same thing.
            var claimed =
                user.FindFirst("preferred_username")?.Value
                ?? user.Identity.Name
                ?? user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst("sub")?.Value;

            if (!string.IsNullOrWhiteSpace(claimed))
            {
                return claimed.Trim();
            }

            // Authenticated but unidentifiable. Deliberately not falling through to the body:
            // the caller proved they are someone, so letting them then name themselves anything
            // is strictly worse than the unauthenticated case.
            return "authenticated";
        }

        return string.IsNullOrWhiteSpace(supplied) ? null : supplied.Trim();
    }
}
