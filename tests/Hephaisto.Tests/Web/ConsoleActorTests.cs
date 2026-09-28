using System.Security.Claims;
using System.Text.RegularExpressions;
using Hephaisto.Agent.Components;
using Hephaisto.Agent.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Hephaisto.Tests.Web;

/// <summary>
/// Who the CONSOLE writes into the audit trail.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActorResolutionTests"/> pins the rule: an authenticated request ignores a supplied
/// name. It held for the API from the day OIDC shipped. The console calls
/// <c>IncidentQueries</c> directly, without an HTTP request in between, and so went round it:
/// with sign-in on, the incident page still asked for a name, and recorded whatever was typed.
/// Found on the first production install with OIDC connected, by the person closing an incident.
/// </para>
/// <para>
/// A Razor render test would pin the wording as well as the rule, so the rule is asserted where
/// it lives - <see cref="ConsoleViewer"/> - and the pages are held to using it by reading them.
/// </para>
/// </remarks>
public sealed class ConsoleActorTests
{
    [Fact]
    public void Signed_in_the_name_is_the_tokens_whatever_was_typed()
    {
        var viewer = new ConsoleViewer(User(("preferred_username", "flo")), true, "flo", MayDecide: true);

        viewer.Actor("someone-else").Should().Be("flo");
        viewer.Actor(string.Empty).Should().Be("flo", "a signed-in viewer types nothing, and the buttons must still work");
        viewer.Actor(null).Should().Be("flo");
    }

    [Fact]
    public void Signed_out_the_name_is_what_was_typed()
    {
        ConsoleViewer.Anonymous.Actor("flo").Should().Be("flo");
        ConsoleViewer.Anonymous.Actor("  ").Should().BeNull("nobody is not an actor");
    }

    [Fact]
    public async Task The_viewer_is_resolved_from_the_sign_in_and_the_approver_policy()
    {
        var approver = await ConsoleViewer.ResolveAsync(
            new FixedState(User(("preferred_username", "flo"))),
            new FixedAuthorization(allow: true));

        approver.Authenticated.Should().BeTrue();
        approver.SignedInAs.Should().Be("flo");
        approver.MayDecide.Should().BeTrue();

        var reader = await ConsoleViewer.ResolveAsync(
            new FixedState(User(("preferred_username", "guest"))),
            new FixedAuthorization(allow: false));

        reader.SignedInAs.Should().Be("guest");
        reader.MayDecide.Should().BeFalse("closing and approving sit behind the approver policy on the API, and here");
    }

    [Fact]
    public async Task With_no_identity_provider_everybody_is_anonymous_and_may_decide()
    {
        // An install without OIDC has no roles to hold. The typed name is attribution there, as
        // it always was.
        var viewer = await ConsoleViewer.ResolveAsync(state: null, authorization: null);

        viewer.Authenticated.Should().BeFalse();
        viewer.SignedInAs.Should().BeNull();
        viewer.MayDecide.Should().BeTrue();
    }

    [Fact]
    public void No_page_asks_for_a_name_except_through_the_field_that_knows_who_is_signed_in()
    {
        // ActorField shows "signed in as ..." and takes no input when there is a token. A page
        // with its own text box has no way to know, and is how this happened.
        var offenders = Razor()
            .Where(f => Path.GetFileName(f) != "ActorField.razor")
            .Where(f => File.ReadAllText(f).Contains("placeholder=\"your name\"", StringComparison.Ordinal))
            .Select(Path.GetFileName);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void No_page_hands_a_typed_name_to_a_query()
    {
        // `Queries.CloseIncidentAsync(Id, _submittedBy, ...)` records what was typed. It has to
        // be `Actor`, which is the token's name when there is one.
        var typed = new Regex(@"Queries\.\w+Async\((?:[^;]|\n)*?\b(_submittedBy|_approvalActor|_actor|_typedName)\b", RegexOptions.Compiled);

        var offenders = Razor()
            .Where(f => Path.GetFileName(f) is "IncidentDetail.razor" or "Status.razor" or "AlertNoteSection.razor")
            .SelectMany(f => typed.Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"));

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void No_endpoint_hands_a_name_from_the_body_to_a_query()
    {
        // The API's half of the same rule. Two routes - re-arm and feedback - passed the body's
        // actor straight through until 2026-09-28, and re-arm is the audit row that most needs
        // to name a real person.
        var passed = new Regex(
            @"queries\.\w+Async\((?:[^;]|\n)*?\brequest\.(SubmittedBy|Actor|RequestedBy|ClosedBy|AssignedBy|DecidedBy|Author|UpdatedBy)\b",
            RegexOptions.Compiled);

        var root = Root();

        var offenders = Directory
            .GetFiles(Path.Combine(root, "src", "Hephaisto.Agent"), "*Endpoints.cs", SearchOption.AllDirectories)
            .SelectMany(f => passed.Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: request.{m.Groups[1].Value}"));

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void The_guards_above_can_fail()
    {
        // A guard that reads files and matches nothing passes for the wrong reason. These are
        // the three shapes it exists to catch, as they were written before the fix.
        var typed = new Regex(@"Queries\.\w+Async\((?:[^;]|\n)*?\b(_submittedBy|_approvalActor|_actor|_typedName)\b");
        var passed = new Regex(@"queries\.\w+Async\((?:[^;]|\n)*?\brequest\.(SubmittedBy|Actor|RequestedBy|ClosedBy|AssignedBy|DecidedBy|Author|UpdatedBy)\b");

        typed.IsMatch("() => Queries.CloseIncidentAsync(Id, _submittedBy, _closeReason, token),").Should().BeTrue();
        typed.IsMatch("await Queries.DecideActionAsync(\n    Id, actionId, approve, _approvalActor, ApprovalSource.Ui,").Should().BeTrue();
        passed.IsMatch("var result = await queries.ReArmAsync(request.Actor, ct);").Should().BeTrue();
        passed.IsMatch("return Render(await queries.AddAlertNoteEntryAsync(name, request.Text, request.IncidentId, request.Author, ct));").Should().BeTrue();
        typed.IsMatch("() => Queries.AddAlertNoteEntryAsync(AlertName, _entry, IncidentId, _typedName, CancellationToken.None),").Should().BeTrue();

        typed.IsMatch("() => Queries.CloseIncidentAsync(Id, Actor, _closeReason, token),").Should().BeFalse();
        passed.IsMatch("var actor = ActorResolution.Resolve(http.User, request.Actor);\nawait queries.ReArmAsync(actor, ct);").Should().BeFalse();

        Razor().Should().Contain(f => f.EndsWith("IncidentDetail.razor", StringComparison.Ordinal))
            .And.Contain(f => f.EndsWith("Status.razor", StringComparison.Ordinal))
            .And.Contain(f => f.EndsWith("AlertNoteSection.razor", StringComparison.Ordinal));
    }

    private static List<string> Razor() =>
        [.. Directory.GetFiles(
            Path.Combine(Root(), "src", "Hephaisto.Agent", "Components"),
            "*.razor",
            SearchOption.AllDirectories)];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return dir!.FullName;
    }

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    private sealed class FixedState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FixedAuthorization(bool allow) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(Result());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            policyName.Should().Be(AuthenticationExtensions.ApprovePolicy);

            return Task.FromResult(Result());
        }

        private AuthorizationResult Result() =>
            allow ? AuthorizationResult.Success() : AuthorizationResult.Failed();
    }
}
