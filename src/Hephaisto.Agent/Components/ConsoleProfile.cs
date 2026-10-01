using System.Security.Claims;

using Hephaisto.Agent.Options;
using Hephaisto.Agent.Web;

namespace Hephaisto.Agent.Components;

/// <summary>One role the sign-in carried, and what this console does with it.</summary>
/// <param name="Name">The role as the IdP names it.</param>
/// <param name="Grants">What it allows here, or null for a role this console does not act on.</param>
public sealed record ConsoleRole(string Name, string? Grants);

/// <summary>
/// What the account page says about whoever is looking: who the sign-in said they are, and which
/// of their roles this console acts on.
/// </summary>
/// <remarks>
/// It exists because the two questions it answers were unanswerable from the console. The first
/// production install with roles spent an afternoon on "why can I not close this" - the account
/// held the approver role and not the reader one - and the only way to see which roles had
/// actually arrived was to decode a token by hand in the IdP's admin console.
/// </remarks>
/// <param name="SignInEnabled">Whether this install has an IdP at all.</param>
/// <param name="Authenticated">Signed in through it.</param>
/// <param name="Username">The name the audit trail carries - <see cref="ActorResolution"/>'s.</param>
/// <param name="FullName">The IdP's display name, when it sent one.</param>
/// <param name="Roles">Every role on the sign-in, the ones this console acts on first.</param>
/// <param name="ReaderRole">The role reading requires; null when any signed-in user may read.</param>
/// <param name="ApproverRole">The role deciding requires; null when any signed-in user may decide.</param>
/// <param name="MayDecide">Holds the approve policy, as <see cref="ConsoleViewer.MayDecide"/>.</param>
public sealed record ConsoleProfile(
    bool SignInEnabled,
    bool Authenticated,
    string? Username,
    string? FullName,
    string? Email,
    string? Subject,
    IReadOnlyList<ConsoleRole> Roles,
    string? ReaderRole,
    string? ApproverRole,
    bool MayDecide)
{
    public const string ReaderGrants = "opens the console and reads every incident";

    public const string ApproverGrants = "approves and denies actions, closes incidents, re-arms the mode";

    /// <summary>The letter in the navigation's badge.</summary>
    public string Initial => Username is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?";

    public static ConsoleProfile From(ConsoleViewer viewer, AuthOptions auth)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(auth);

        var reader = NullIfBlank(auth.ReaderRole);
        var approver = NullIfBlank(auth.ApproverRole);

        if (!viewer.Authenticated)
        {
            return new ConsoleProfile(auth.Enabled, false, null, null, null, null, [], reader, approver, viewer.MayDecide);
        }

        var user = viewer.User;

        // Both spellings of each claim. The id token's claims pass through the handler's inbound
        // mapping (email and sub arrive under their long .NET names) while the userinfo ones keep
        // the short ones, and which of the two a given IdP fills is not something to guess at.
        var roles = user.FindAll(auth.RolesClaim)
            .Concat(user.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .Select(role => new ConsoleRole(role, Grants(role, reader, approver)))
            .OrderBy(role => role.Grants is null)
            .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ConsoleProfile(
            auth.Enabled,
            true,
            viewer.SignedInAs,
            NullIfBlank(user.FindFirst("name")?.Value),
            NullIfBlank(user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value),
            NullIfBlank(user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value),
            roles,
            reader,
            approver,
            viewer.MayDecide);
    }

    private static string? Grants(string role, string? reader, string? approver)
    {
        var isReader = string.Equals(role, reader, StringComparison.Ordinal);
        var isApprover = string.Equals(role, approver, StringComparison.Ordinal);

        return (isReader, isApprover) switch
        {
            (true, true) => $"{ReaderGrants}; {ApproverGrants}",
            (true, false) => ReaderGrants,
            (false, true) => ApproverGrants,
            _ => null,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
