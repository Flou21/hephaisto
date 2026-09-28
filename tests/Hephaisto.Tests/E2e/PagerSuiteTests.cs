using System.Text.RegularExpressions;

namespace Hephaisto.Tests.E2e;

/// <summary>
/// The pager suite's own bookkeeping, held where every build sees it.
/// </summary>
/// <remarks>
/// The suite needs a cluster and runs in its own CI job; these need nothing. They pin the two
/// properties that make its known-red list honest rather than a place scenarios go to be
/// forgotten: every listed id is a scenario that exists, and the list is empty by the time the
/// version floor says the milestone it belongs to is over.
/// </remarks>
public sealed partial class PagerSuiteTests
{
    [Fact]
    public void Every_known_red_id_is_a_scenario()
    {
        var scenarios = Directory.EnumerateFiles(Pager(), "P*.sh")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.Ordinal);

        KnownRed().Should().OnlyContain(id => scenarios.Contains(id),
            "a known-red id with no scenario behind it hides nothing and reads as a promise");
    }

    [Fact]
    public void Every_scenario_declares_its_header()
    {
        foreach (var file in Directory.EnumerateFiles(Pager(), "P*.sh"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var header = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("# pager: ", StringComparison.Ordinal));

            header.Should().NotBeNull($"{id} needs a '# pager:' line - the runner schedules by it");
            header!.Should().MatchRegex($@"^# pager: {id} \| [a-z -]+ \| (shared|exclusive) \| .+$");
        }
    }

    /// <summary>
    /// A milestone is done when its scenarios are green. Once the floor moves past v0.10.0 the
    /// list must be empty, or the release shipped with scenarios it never passed.
    /// </summary>
    [Fact]
    public void The_known_red_list_is_empty_once_v0_10_is_over()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var floor = FloorPattern().Match(props);
        floor.Success.Should().BeTrue("Directory.Build.props sets MinVerMinimumMajorMinor");

        var version = Version.Parse(floor.Groups[1].Value);

        if (version >= new Version(0, 11))
        {
            KnownRed().Should().BeEmpty("v0.10.0 is over, and every pager scenario had to be green for it");
        }
    }

    private static List<string> KnownRed() =>
        [.. File.ReadLines(Path.Combine(Pager(), "KNOWN_RED"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', '\t')[0])];

    private static string Pager() => Path.Combine(RepoRoot(), "scripts", "e2e", "pager");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [GeneratedRegex(@"<MinVerMinimumMajorMinor>\s*([0-9]+\.[0-9]+)\s*</MinVerMinimumMajorMinor>")]
    private static partial Regex FloorPattern();
}
