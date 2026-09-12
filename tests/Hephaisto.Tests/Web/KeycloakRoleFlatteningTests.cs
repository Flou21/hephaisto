using System.Security.Claims;

using Hephaisto.Agent.Web;

namespace Hephaisto.Tests.Web;

/// <summary>
/// Keycloak's realm roles, flattened onto the claim the role policies read (#110).
/// </summary>
/// <remarks>
/// Keycloak emits realm roles as a JSON object inside a single <c>realm_access</c> claim, so they
/// arrive as one claim whose value is <c>{"roles":["a","b"]}</c> rather than as several role
/// claims. Every role check in ASP.NET Core looks for the latter. Without this step a correctly
/// configured realm produces a user with no roles at all, and the only symptom is a 403 that
/// names nothing - on the endpoints that approve cluster changes.
/// </remarks>
public sealed class KeycloakRoleFlatteningTests
{
    private const string RolesClaim = "roles";

    [Fact]
    public void Realm_roles_become_role_claims()
    {
        var principal = Principal("""{"roles":["hephaisto-reader","hephaisto-approver"]}""");

        AuthenticationExtensions.FlattenKeycloakRoles(principal, RolesClaim);

        principal.FindAll(RolesClaim).Select(c => c.Value)
            .Should().BeEquivalentTo(["hephaisto-reader", "hephaisto-approver"]);
    }

    [Fact]
    public void Running_twice_does_not_duplicate_a_role()
    {
        var principal = Principal("""{"roles":["hephaisto-approver"]}""");

        AuthenticationExtensions.FlattenKeycloakRoles(principal, RolesClaim);
        AuthenticationExtensions.FlattenKeycloakRoles(principal, RolesClaim);

        principal.FindAll(RolesClaim).Should().ContainSingle();
    }

    /// <summary>
    /// Malformed input drops the roles rather than throwing.
    /// </summary>
    /// <remarks>
    /// The safe reading: the user ends up with fewer permissions, not more. Throwing here would
    /// turn somebody else's malformed claim into a failed sign-in for everyone.
    /// </remarks>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"roles":"a string, not an array"}""")]
    [InlineData("""{"something-else":["x"]}""")]
    public void Malformed_realm_access_yields_no_roles_and_does_not_throw(string realmAccess)
    {
        var principal = Principal(realmAccess);

        var act = () => AuthenticationExtensions.FlattenKeycloakRoles(principal, RolesClaim);

        act.Should().NotThrow();
        principal.FindAll(RolesClaim).Should().BeEmpty();
    }

    [Fact]
    public void A_principal_without_realm_access_is_left_alone()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "oidc"));

        AuthenticationExtensions.FlattenKeycloakRoles(principal, RolesClaim);

        principal.FindAll(RolesClaim).Should().BeEmpty();
    }

    [Fact]
    public void A_null_principal_is_tolerated()
    {
        var act = () => AuthenticationExtensions.FlattenKeycloakRoles(null, RolesClaim);

        act.Should().NotThrow();
    }

    private static ClaimsPrincipal Principal(string realmAccess) =>
        new(new ClaimsIdentity([new Claim("realm_access", realmAccess)], "oidc"));
}
