using System.Security.Claims;

using Hephaisto.Agent.Web;

namespace Hephaisto.Tests.Web;

/// <summary>
/// Who gets written into the audit trail (#110).
/// </summary>
/// <remarks>
/// The audit trail's integrity story was always careful about WRITES - the app serves as a
/// non-owner Postgres role holding INSERT but not UPDATE or DELETE on <c>audit_events</c>,
/// enforced in the migration - and completely uncritical about IDENTITY: the actor was whatever
/// somebody typed into a form. These tests pin the half that was missing.
/// </remarks>
public sealed class ActorResolutionTests
{
    /// <summary>
    /// The one that matters: an authenticated caller cannot name themselves anything else.
    /// </summary>
    /// <remarks>
    /// Preferring the token while still falling back to the body would leave the obvious way to
    /// act as somebody else permanently open, and the result would be indistinguishable in the
    /// audit trail from a genuine action by that person.
    /// </remarks>
    [Fact]
    public void An_authenticated_request_ignores_the_name_in_the_body() =>
        ActorResolution.Resolve(User(("preferred_username", "flo")), "someone-else")
            .Should().Be("flo");

    [Fact]
    public void An_unauthenticated_request_falls_back_to_the_supplied_name() =>
        ActorResolution.Resolve(Anonymous(), "flo").Should().Be("flo");

    /// <summary>Neither a token nor a name is not an anonymous audit row; it is an error.</summary>
    [Fact]
    public void Neither_a_token_nor_a_name_resolves_to_nothing() =>
        ActorResolution.Resolve(Anonymous(), "   ").Should().BeNull();

    /// <summary>
    /// Authenticated but with no usable claim still does not fall back to the body.
    /// </summary>
    /// <remarks>
    /// This is the subtle one. The caller has proved they are <em>someone</em>, so letting them
    /// then choose a display name is strictly worse than the anonymous case - it launders a
    /// chosen name through a real sign-in.
    /// </remarks>
    [Fact]
    public void An_identified_caller_with_no_usable_claim_does_not_fall_back() =>
        ActorResolution.Resolve(User(("irrelevant", "x")), "someone-else")
            .Should().Be("authenticated");

    /// <summary>preferred_username is what a person recognises; sub is a UUID.</summary>
    [Fact]
    public void The_readable_claim_wins_over_the_subject() =>
        ActorResolution.Resolve(
                User(("sub", "8f14e45f-ceea-467a-9b3a-9c0f0f0f0f0f"), ("preferred_username", "flo")),
                supplied: null)
            .Should().Be("flo");

    [Fact]
    public void The_subject_is_used_when_there_is_nothing_friendlier() =>
        ActorResolution.Resolve(User(("sub", "abc-123")), supplied: null).Should().Be("abc-123");

    [Fact]
    public void Surrounding_whitespace_is_trimmed() =>
        ActorResolution.Resolve(Anonymous(), "  flo  ").Should().Be("flo");

    // ----------------------------------------------------------------------------------

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    /// <summary>An identity with an authentication type is what makes IsAuthenticated true.</summary>
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "oidc",
            nameType: "preferred_username",
            roleType: "roles"));
}
