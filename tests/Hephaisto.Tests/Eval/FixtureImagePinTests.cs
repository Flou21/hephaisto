using System.Text.RegularExpressions;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Every chaos fixture built from <c>hephaisto-fixture-dotnet</c> runs an image pinned to
/// <c>&lt;fixture id&gt;-&lt;full commit sha&gt;</c>, and says which commit that is.
/// </summary>
/// <remarks>
/// <para>
/// A code-fix fixture is graded against a commit: the planted test must fail on it and pass on
/// the PR head, and a cassette records the sha it was made against. A floating tag - <c>:c15</c>,
/// <c>:latest</c> - would let a force-push to the fixture branch change what a recorded run was
/// graded against without a line changing in this repository. The fixture repo's workflow
/// never pushes one; this is the other half of that rule.
/// </para>
/// <para>
/// The annotations are checked against the tag for the same reason: the code-fix stage reads
/// <c>hephaisto.dev/source-ref</c> and <c>hephaisto.dev/source-sha</c> to know what to clone, so
/// an annotation that disagrees with the image would send the coder to a commit the pod is not
/// running.
/// </para>
/// </remarks>
public class FixtureImagePinTests
{
    private const string Repository = "ghcr.io/flou21/hephaisto-fixture-dotnet";

    private static readonly Regex Image = new(
        @"^\s*(?:-\s*)?image:\s*[""']?(?<ref>[^\s""']+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Pinned = new(
        @"^ghcr\.io/flou21/hephaisto-fixture-dotnet:(?<id>c\d+)-(?<sha>[0-9a-f]{40})$", RegexOptions.Compiled);

    [Fact]
    public void Every_fixture_repo_image_is_pinned_to_its_id_and_a_full_sha()
    {
        var uses = FixtureImages().ToList();

        // Non-vacuous: with no fixture using the image, every assertion below passes on nothing.
        uses.Should().NotBeEmpty($"at least one infra/chaos manifest must run {Repository}");

        foreach (var (file, reference, _) in uses)
        {
            var pinned = Pinned.Match(reference);

            pinned.Success.Should().BeTrue(
                $"{file} runs {reference}; it must be {Repository}:<id>-<40-hex sha>, never a "
                + "floating tag, a short sha or a digest-only reference");

            pinned.Groups["id"].Value.Should().Be(FixtureId(file),
                $"{file} must run its own fixture's image, not another fixture's");
        }
    }

    [Fact]
    public void The_source_annotations_name_the_commit_the_image_was_built_from()
    {
        foreach (var (file, reference, yaml) in FixtureImages())
        {
            var pinned = Pinned.Match(reference);
            if (!pinned.Success)
                continue; // reported by the test above

            Annotation(yaml, "hephaisto.dev/source-sha").Should().Be(pinned.Groups["sha"].Value,
                $"{file}'s source-sha must be the commit its image tag names");

            var key = CodeFixAnswerKey.For(FixtureId(file));
            key.Should().NotBeNull($"{file} runs a code-fix fixture image, so it needs a CodeFixAnswerKey");

            Annotation(yaml, "hephaisto.dev/source-ref").Should().Be(key!.BaseRef,
                $"{file}'s source-ref must be the branch its answer key grades against");
        }
    }

    [Fact]
    public void Every_code_fix_key_has_a_chaos_fixture_that_runs_it()
    {
        var ids = FixtureImages().Select(u => FixtureId(u.File)).ToHashSet(StringComparer.Ordinal);

        foreach (var key in CodeFixAnswerKey.All)
            ids.Should().Contain(key.Fixture, $"{key.Fixture} has a key and nothing in infra/chaos runs it");
    }

    [Theory]
    [InlineData("ghcr.io/flou21/hephaisto-fixture-dotnet:c15")]
    [InlineData("ghcr.io/flou21/hephaisto-fixture-dotnet:latest")]
    [InlineData("ghcr.io/flou21/hephaisto-fixture-dotnet:c15-583b1e5")]
    [InlineData("ghcr.io/Flou21/hephaisto-fixture-dotnet:c15-583b1e5b75add2ba341319eef1ae438e8346c4b0")]
    [InlineData("ghcr.io/flou21/hephaisto-fixture-dotnet@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ghcr.io/flou21/hephaisto-fixture-dotnet:main-583b1e5b75add2ba341319eef1ae438e8346c4b0")]
    public void The_pin_rule_refuses_what_it_should(string reference)
    {
        // The negative control. An over-permissive pattern would pass every manifest and look
        // like a strict rule; each of these is a way to lose the commit a run was graded against
        // (or, for main-, to run the healthy control where a fixture was meant).
        Pinned.IsMatch(reference).Should().BeFalse();
    }

    private static IEnumerable<(string File, string Reference, string Yaml)> FixtureImages()
    {
        foreach (var path in Directory.GetFiles(RepoFile("infra", "chaos"), "*.yaml").Order(StringComparer.Ordinal))
        {
            var yaml = File.ReadAllText(path);

            foreach (Match m in Image.Matches(yaml))
            {
                var reference = m.Groups["ref"].Value;
                if (reference.Contains("hephaisto-fixture-dotnet", StringComparison.OrdinalIgnoreCase))
                    yield return (Path.GetFileName(path), reference, yaml);
            }
        }
    }

    // c15-null-deref.yaml -> c15, the convention scripts/e2e/lib/chaos.sh applies with `${f}-*.yaml`.
    private static string FixtureId(string file) => file[..file.IndexOf('-', StringComparison.Ordinal)];

    private static string? Annotation(string yaml, string name)
    {
        var m = Regex.Match(yaml, $@"^\s*{Regex.Escape(name)}:\s*[""']?(?<v>[^\s""']+)", RegexOptions.Multiline);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        return Path.Combine([dir!.FullName, .. parts]);
    }
}
