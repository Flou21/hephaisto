using Hephaisto.ServiceDefaults;

namespace Hephaisto.Tests;

/// <summary>
/// How the running version is read out of the assembly stamp.
/// </summary>
/// <remarks>
/// This is small but load-bearing. <c>/api/version</c>, the console footer and
/// <c>hephaisto_build_info</c> all report whatever comes out of here, and every one of them
/// is read during an incident to answer "is this the build we rolled out?". A parse that
/// quietly reports the wrong thing is worse than one that fails: an operator who believes
/// they are looking at 0.0.2 will not think to check.
/// </remarks>
public class BuildInfoTests
{
    [Fact]
    public void A_tagged_release_reports_the_tag_and_the_commit()
    {
        var (version, commit) = BuildInfo.Parse("0.0.1+80ed67df2e9ebf34f0620de24a910de997f411eb");

        version.Should().Be("0.0.1");
        commit.Should().Be("80ed67df2e9ebf34f0620de24a910de997f411eb");
    }

    [Fact]
    public void An_untagged_main_build_keeps_the_whole_prerelease_label()
    {
        // The height and the branch identifier are the useful part of an untagged build:
        // "0.0.2-main.0.42" says how far past the last release it is. Truncating at the
        // first dash would collapse every commit since the tag into one indistinguishable
        // version.
        var (version, commit) = BuildInfo.Parse("0.0.2-main.0.42+3f1a9c2");

        version.Should().Be("0.0.2-main.0.42");
        commit.Should().Be("3f1a9c2");
    }

    /// <summary>
    /// OCI tags cannot contain '+', so the version half must be usable as an image tag on its
    /// own. If this ever failed, the chart would ask for a tag the registry does not have.
    /// </summary>
    [Fact]
    public void The_version_half_is_always_safe_as_an_oci_tag()
    {
        var (version, _) = BuildInfo.Parse("0.0.2-main.0.42+3f1a9c2");

        version.Should().NotContain("+");
    }

    [Fact]
    public void A_version_with_no_build_metadata_reports_an_unknown_commit()
    {
        var (version, commit) = BuildInfo.Parse("0.0.1");

        version.Should().Be("0.0.1");
        commit.Should().Be("unknown");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_stamp_reports_unknown_rather_than_a_plausible_zero(string? informational)
    {
        var (version, commit) = BuildInfo.Parse(informational);

        // Specifically NOT "0.0.0". A version-shaped answer would be read as a real
        // deployed version, and the whole point of this type is to be believable.
        version.Should().Be("unknown");
        commit.Should().Be("unknown");
    }

    [Fact]
    public void A_trailing_plus_with_no_commit_reports_unknown()
    {
        var (version, commit) = BuildInfo.Parse("0.0.1+");

        version.Should().Be("0.0.1");
        commit.Should().Be("unknown");
    }

    [Fact]
    public void A_leading_plus_reports_an_unknown_version_rather_than_an_empty_one()
    {
        var (version, commit) = BuildInfo.Parse("+3f1a9c2");

        version.Should().Be("unknown");
        commit.Should().Be("3f1a9c2");
    }

    // ---------------------------------------------------------------------------------
    // AGPL section 13. The console is served over a network, so the people using it never
    // receive the binary; the footer link is how they get the source of the build they are
    // actually talking to. Getting this wrong is not cosmetic - it is the licence term.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void An_unmodified_build_links_to_the_exact_commit_upstream()
    {
        // "whatever is on main today" is not the source of the version someone is using.
        BuildInfo.SourceUrlForThisBuild(null)
            .Should().Be($"{BuildInfo.SourceUrl}/tree/{BuildInfo.Commit}");
    }

    [Fact]
    public void A_configured_fork_url_wins()
    {
        BuildInfo.SourceUrlForThisBuild("https://git.example.com/me/hephaisto")
            .Should().Be("https://git.example.com/me/hephaisto");
    }

    [Fact]
    public void A_configured_fork_url_is_never_rewritten_into_an_upstream_path()
    {
        // Appending /tree/<sha> to someone else's host would produce a dead link, and a dead
        // source link is worse than none: it looks discharged and is not.
        BuildInfo.SourceUrlForThisBuild("https://git.example.com/me/hephaisto/")
            .Should().Be("https://git.example.com/me/hephaisto");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_configured_url_falls_back_to_upstream_rather_than_to_nothing(string? configured)
    {
        // An empty href in the footer would silently offer users no source at all.
        BuildInfo.SourceUrlForThisBuild(configured).Should().StartWith(BuildInfo.SourceUrl);
    }

    /// <summary>
    /// The real assembly, not a hand-made string: this is what actually ships. It asserts the
    /// build plumbing did something, without pinning a number that changes every commit.
    /// </summary>
    [Fact]
    public void The_running_assembly_carries_a_real_version()
    {
        BuildInfo.Version.Should().NotBeNullOrWhiteSpace();
        BuildInfo.Version.Should().NotContain("+");
        BuildInfo.ShortCommit.Length.Should().BeLessThanOrEqualTo(12);
    }
}
