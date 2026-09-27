using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Web;

namespace Hephaisto.Agent.Components;

/// <summary>
/// How a component reaches the code-fix stage: one DI scope per operation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CodeFixQueries"/> and <see cref="CodeFixCoordinator"/> are scoped over a
/// <c>DbContext</c>, and a Blazor Server component's scope is the whole CIRCUIT - so injecting them
/// straight into a page would share one context between the fallback tick, the live listener and a
/// click that land in the same second ("a second operation was started on this context"). Worse,
/// the coordinator's <c>SELECT ... FOR UPDATE</c> on a context that has already tracked the row
/// returns the tracked instance with its stale values, which is the approve race the lock exists to
/// close. A fresh scope per call is what <see cref="IncidentQueries"/> does internally for the same
/// reason.
/// </para>
/// </remarks>
public static class CodeFixAccess
{
    public static async Task<T> QueryAsync<T>(
        this IServiceScopeFactory scopes, Func<CodeFixQueries, Task<T>> query)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<CodeFixQueries>());
    }

    public static async Task<T> CoordinateAsync<T>(
        this IServiceScopeFactory scopes, Func<CodeFixCoordinator, Task<T>> act)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await act(scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>());
    }
}

/// <summary>Who is looking at the console, as far as deciding a code fix is concerned.</summary>
/// <param name="User">The circuit's principal; anonymous when Auth is off.</param>
/// <param name="Authenticated">Signed in through OIDC. Only then may the typed name be ignored.</param>
/// <param name="SignedInAs">The name the audit trail will carry for a signed-in user.</param>
/// <param name="MayDecide">
/// Holds the approve policy - the same one <c>POST .../codefix/{id}/approve</c> carries, so the
/// console is never a wider door than the API. Always true when Auth is off, as the policy is.
/// </param>
public sealed record ConsoleViewer(ClaimsPrincipal User, bool Authenticated, string? SignedInAs, bool MayDecide)
{
    public static readonly ConsoleViewer Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()), false, null, true);

    /// <summary>
    /// The actor to record: the token's when signed in, the typed name otherwise - exactly
    /// <see cref="ActorResolution.Resolve"/>, which the API uses.
    /// </summary>
    public string? Actor(string? typed) => ActorResolution.Resolve(User, typed);

    public static async Task<ConsoleViewer> ResolveAsync(
        AuthenticationStateProvider? state, IAuthorizationService? authorization)
    {
        if (state is null)
        {
            return Anonymous;
        }

        try
        {
            var user = (await state.GetAuthenticationStateAsync()).User;
            var authenticated = user.Identity?.IsAuthenticated == true;

            var mayDecide = authorization is null
                || (await authorization.AuthorizeAsync(user, AuthenticationExtensions.ApprovePolicy)).Succeeded;

            return new ConsoleViewer(user, authenticated, authenticated ? ActorResolution.Resolve(user, null) : null, mayDecide);
        }
        catch (InvalidOperationException)
        {
            // No authentication state on this render (a prerender with no HttpContext). Treat the
            // viewer as anonymous; the coordinator still refuses an unauthenticated approve.
            return Anonymous;
        }
    }
}
