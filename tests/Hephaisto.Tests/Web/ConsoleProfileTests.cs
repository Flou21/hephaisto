using System.Security.Claims;
using Hephaisto.Agent.Components;
using Hephaisto.Agent.Options;

namespace Hephaisto.Tests.Web;

/// <summary>
/// What the account page says about the viewer. The page exists so that "which roles actually
/// arrived" is answerable from the console: the first install with roles held the approver role
/// without the reader one, and finding that out meant decoding a token in the IdP.
/// </summary>
public sealed class ConsoleProfileTests
{
    private static readonly AuthOptions Auth = new()
    {
        Enabled = true,
        Authority = "https://idp.example/realms/x",
        RolesClaim = "roles",
        ReaderRole = "hephaisto-reader",
        ApproverRole = "hephaisto-approver",
    };

    [Fact]
    public void The_roles_this_console_acts_on_come_first_and_say_what_they_grant()
    {
        var profile = ConsoleProfile.From(
            Viewer(
                mayDecide: true,
                ("preferred_username", "florian"),
                ("roles", "offline_access"),
                ("roles", "hephaisto-reader"),
                ("roles", "default-roles-cait"),
                ("roles", "hephaisto-approver")),
            Auth);

        profile.Roles.Select(r => r.Name).Should().Equal(
            "hephaisto-approver", "hephaisto-reader", "default-roles-cait", "offline_access");

        profile.Roles[0].Grants.Should().Be(ConsoleProfile.ApproverGrants);
        profile.Roles[1].Grants.Should().Be(ConsoleProfile.ReaderGrants);
        profile.Roles[2].Grants.Should().BeNull("a realm default is not something this console acts on");
        profile.MayDecide.Should().BeTrue();
    }

    [Fact]
    public void A_reader_is_told_which_role_deciding_needs()
    {
        var profile = ConsoleProfile.From(
            Viewer(mayDecide: false, ("preferred_username", "guest"), ("roles", "hephaisto-reader")),
            Auth);

        profile.MayDecide.Should().BeFalse();
        profile.ApproverRole.Should().Be("hephaisto-approver");
        profile.Roles.Should().ContainSingle().Which.Name.Should().Be("hephaisto-reader");
    }

    [Fact]
    public void Identity_claims_are_read_under_either_spelling()
    {
        var mapped = ConsoleProfile.From(
            Viewer(
                mayDecide: true,
                ("preferred_username", "florian"),
                ("name", "Florian Example"),
                (ClaimTypes.Email, "florian@example.com"),
                (ClaimTypes.NameIdentifier, "0f1e")),
            Auth);

        mapped.Username.Should().Be("florian");
        mapped.FullName.Should().Be("Florian Example");
        mapped.Email.Should().Be("florian@example.com");
        mapped.Subject.Should().Be("0f1e");
        mapped.Initial.Should().Be("F");

        var plain = ConsoleProfile.From(
            Viewer(mayDecide: true, ("preferred_username", "florian"), ("email", "f@example.com"), ("sub", "abcd")),
            Auth);

        plain.Email.Should().Be("f@example.com");
        plain.Subject.Should().Be("abcd");
        plain.FullName.Should().BeNull();
    }

    [Fact]
    public void A_role_listed_twice_is_shown_once()
    {
        var profile = ConsoleProfile.From(
            Viewer(
                mayDecide: false,
                ("preferred_username", "florian"),
                ("roles", "hephaisto-reader"),
                (ClaimTypes.Role, "hephaisto-reader")),
            Auth);

        profile.Roles.Should().ContainSingle();
    }

    [Fact]
    public void With_no_roles_configured_no_role_is_named_as_required()
    {
        var open = new AuthOptions { Enabled = true, Authority = "https://idp.example", ReaderRole = " " };

        var profile = ConsoleProfile.From(
            Viewer(mayDecide: true, ("preferred_username", "florian"), ("roles", "anything")),
            open);

        profile.ReaderRole.Should().BeNull();
        profile.ApproverRole.Should().BeNull();
        profile.Roles.Should().ContainSingle().Which.Grants.Should().BeNull();
    }

    [Fact]
    public void With_sign_in_off_nobody_is_signed_in_and_the_page_can_say_why()
    {
        var profile = ConsoleProfile.From(ConsoleViewer.Anonymous, new AuthOptions());

        profile.SignInEnabled.Should().BeFalse();
        profile.Authenticated.Should().BeFalse();
        profile.Username.Should().BeNull();
        profile.Roles.Should().BeEmpty();
        profile.Initial.Should().Be("?");
    }

    private static ConsoleViewer Viewer(bool mayDecide, params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)), "test", "preferred_username", "roles");
        var user = new ClaimsPrincipal(identity);

        return new ConsoleViewer(user, true, identity.Name, mayDecide);
    }
}
