using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Hephaisto.Agent.Options;

namespace Hephaisto.Agent.Web;

/// <summary>
/// OIDC for the browser, bearer tokens for everything else (#110).
/// </summary>
/// <remarks>
/// <para>
/// Two schemes against one authority. The console is a Blazor Server app a person opens, so it
/// gets the authorization-code flow and a cookie; the JSON API is also called by scripts and by
/// the e2e harness, which have no browser, so it additionally accepts a bearer token from the
/// same issuer. One IdP, one set of roles, two front doors.
/// </para>
/// <para>
/// <b>It fails closed, by decision.</b> If the IdP is unreachable the app is unavailable. The
/// alternative - dropping to an unauthenticated console whenever Keycloak is down - makes the
/// control decorative and hands an attacker the timing of it. The cost is understood and
/// accepted: reading the console during an outage now depends on the IdP, so the IdP is inside
/// the blast radius of one.
/// </para>
/// </remarks>
public static class AuthenticationExtensions
{
    /// <summary>The policy every console and API endpoint carries.</summary>
    public const string ReadPolicy = "hephaisto.read";

    /// <summary>
    /// The policy for the endpoints that change something: approving or denying an action,
    /// closing an incident, re-arming the mode.
    /// </summary>
    public const string ApprovePolicy = "hephaisto.approve";

    public static IServiceCollection AddHephaistoAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateOnStart();

        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        if (!auth.Enabled)
        {
            // No SCHEME, but the authentication services are still registered. Skipping them
            // entirely leaves IAuthenticationSchemeProvider unresolvable, and app.UseAuthentication()
            // then fails to activate its middleware - so the process does not start AT ALL in the
            // default configuration. That is what happened: every unit test passed and
            // `dotnet run` died with "Unable to resolve service for type
            // IAuthenticationSchemeProvider". With no scheme registered the middleware is a
            // pass-through, which is exactly the wanted behaviour.
            services.AddAuthentication();

            // And every policy satisfied. The alternative - a scheme that authenticates nobody -
            // produces 401s on an install that never asked for auth, which is a worse failure
            // than the one being fixed.
            services.AddAuthorizationBuilder()
                .AddPolicy(ReadPolicy, p => p.RequireAssertion(_ => true))
                .AddPolicy(ApprovePolicy, p => p.RequireAssertion(_ => true));

            return services;
        }

        if (string.IsNullOrWhiteSpace(auth.Authority))
        {
            // Fail at startup rather than at the first request. An agent that came up and then
            // 500ed every sign-in would look like an IdP problem for as long as it took somebody
            // to read the config.
            throw new InvalidOperationException(
                $"{AuthOptions.SectionName}:Enabled is true but {AuthOptions.SectionName}:Authority "
                + "is not set. There is nothing to authenticate against.");
        }

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "hephaisto.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Not Always: the console is reached over plain HTTP on a port-forward, which is
                // what the chart's own NOTES tell an operator to do, and a Secure-only cookie
                // would make sign-in silently impossible there. The transport is the deployment's
                // to secure - through the ingress that terminates TLS.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = auth.Authority;
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                options.ClientId = auth.ClientId;
                options.ClientSecret = auth.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;

                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "preferred_username",
                    RoleClaimType = auth.RolesClaim,
                };

                // Keycloak nests realm roles under realm_access.roles, which no standard claim
                // mapping flattens. Without this the role policies below can never be satisfied
                // and every approver is refused - with a 403 that names nothing.
                options.Events.OnTokenValidated = context =>
                {
                    FlattenKeycloakRoles(context.Principal, auth.RolesClaim);
                    return Task.CompletedTask;
                };

                // A browser gets sent to the IdP; anything else gets told no. Without this an
                // API caller with an expired or absent token receives a 302 to a login page, so
                // a script sees HTML and a 200-shaped redirect chain instead of an error - which
                // is indistinguishable from success until something downstream fails to parse.
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (WantsJson(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                    }

                    return Task.CompletedTask;
                };
            });

        services.AddAuthentication()
            .AddJwtBearer(options =>
            {
                options.Authority = auth.Authority;
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // The audience is the client id when Keycloak is issuing for this client, but
                    // a service-account token often carries `account` instead. Validating the
                    // issuer is the check that matters here; validating the audience as well
                    // turns every automation token into a 401 that reads like a bad secret.
                    ValidateAudience = false,
                    NameClaimType = "preferred_username",
                    RoleClaimType = auth.RolesClaim,
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        FlattenKeycloakRoles(context.Principal, auth.RolesClaim);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, policy => Build(policy, auth.ReaderRole, auth.RolesClaim))
            .AddPolicy(ApprovePolicy, policy => Build(policy, auth.ApproverRole, auth.RolesClaim));

        // Said once, at startup, for the two settings whose weakening is otherwise invisible.
        services.AddSingleton<IHostedService>(sp => new AuthStartupReport(
            auth, sp.GetRequiredService<ILogger<AuthStartupReport>>()));

        return services;
    }

    /// <summary>
    /// A role requirement when one is named, otherwise "any authenticated user".
    /// </summary>
    /// <remarks>
    /// The permissive branch is deliberate and reported at startup. An install that has an IdP
    /// but has not decided on roles is still far better off than one with no authentication at
    /// all, and refusing to start until roles exist would push people back to no auth.
    /// </remarks>
    private static void Build(AuthorizationPolicyBuilder policy, string? role, string rolesClaim)
    {
        // BOTH schemes, and this is not optional. A policy with no scheme list uses only the
        // default authenticate scheme - the cookie - so a perfectly valid bearer token is never
        // examined and every API caller is redirected to a login page it cannot complete. That
        // is exactly what happened: the token was correct, the roles were correct, and every
        // request came back 302.
        policy.AddAuthenticationSchemes(
            CookieAuthenticationDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme);

        policy.RequireAuthenticatedUser();

        if (!string.IsNullOrWhiteSpace(role))
        {
            policy.RequireClaim(rolesClaim, role);
        }
    }

    /// <summary>
    /// Whether this request would rather have a status code than a login page.
    /// </summary>
    /// <remarks>
    /// An Authorization header means the caller already tried to authenticate itself, and an
    /// Accept of JSON means it is not a browser. Either way a redirect is useless to it.
    /// </remarks>
    private static bool WantsJson(HttpRequest request) =>
        request.Headers.ContainsKey("Authorization")
        || request.Headers.Accept.Any(value =>
            value is not null && value.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        || request.Path.StartsWithSegments("/api");

    /// <summary>
    /// Copies Keycloak's <c>realm_access.roles</c> onto the configured role claim.
    /// </summary>
    /// <remarks>
    /// Keycloak emits roles as a JSON object nested inside a single claim, so they arrive as one
    /// claim whose value is <c>{"roles":["a","b"]}</c> rather than as several role claims. Every
    /// role check in ASP.NET Core looks for the latter, so without this step a correctly
    /// configured realm produces a user with no roles and a 403 nobody can explain.
    /// </remarks>
    internal static void FlattenKeycloakRoles(ClaimsPrincipal? principal, string rolesClaim)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var realmAccess = identity.FindFirst("realm_access")?.Value;

        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(realmAccess);

            if (!document.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind is not System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            foreach (var role in roles.EnumerateArray())
            {
                if (role.GetString() is { Length: > 0 } name
                    && !identity.HasClaim(rolesClaim, name))
                {
                    identity.AddClaim(new Claim(rolesClaim, name));
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A realm_access that is not JSON is somebody else's bug, and dropping the roles is
            // the safe reading: the user ends up with fewer permissions, not more.
        }
    }
}

/// <summary>
/// One line at startup for the auth settings whose weakening is otherwise invisible.
/// </summary>
/// <remarks>
/// Modelled on <c>OutboundStartupReport</c>, and for the same reason: a permissive setting that
/// nobody states reads exactly like a strict one. Both of these are choices somebody can make
/// defensibly and neither should be silent.
/// </remarks>
internal sealed class AuthStartupReport(AuthOptions options, ILogger<AuthStartupReport> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Authentication is ON against {Authority}. It fails closed: if the IdP is unreachable "
            + "this agent is unavailable.",
            options.Authority);

        if (!options.RequireHttpsMetadata)
        {
            logger.LogWarning(
                "Auth:RequireHttpsMetadata is false, so the discovery document and signing keys "
                + "are fetched in clear. Anything on that network path can choose who this agent "
                + "believes. Acceptable for an in-cluster issuer; not for one across the internet.");
        }

        if (string.IsNullOrWhiteSpace(options.ApproverRole))
        {
            logger.LogWarning(
                "Auth:ApproverRole is empty, so ANY authenticated user may approve an action, "
                + "close an incident or re-arm the mode. Set it to separate watching from acting.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
