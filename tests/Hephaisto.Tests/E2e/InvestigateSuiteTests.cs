using System.Text.RegularExpressions;

namespace Hephaisto.Tests.E2e;

/// <summary>
/// The investigate suite's bookkeeping (v0.12.0 F5), held where every build sees it.
/// </summary>
/// <remarks>
/// The suite needs a cluster; these need nothing. They pin the same three properties the pager
/// suite's known-red list is held to: every listed id is a scenario that exists, every entry
/// names the milestone that needs it green, and an entry the version floor has passed fails.
/// </remarks>
public sealed partial class InvestigateSuiteTests
{
    [Fact]
    public void Every_known_red_id_is_a_scenario()
    {
        var scenarios = Scenarios();

        scenarios.Should().NotBeEmpty("lib/investigate.sh defines scenario_I<n> functions");
        KnownRed().Select(e => e.Id).Should().OnlyContain(id => scenarios.Contains(id),
            "a known-red id with no scenario behind it hides nothing and reads as a promise");
    }

    [Fact]
    public void Every_scenario_is_in_the_run_list()
    {
        var listed = RunListPattern().Match(Library());
        listed.Success.Should().BeTrue("lib/investigate.sh sets IV_SCENARIOS");

        listed.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().BeEquivalentTo(Scenarios(), "a scenario missing from the run list never runs");
    }

    [Fact]
    public void Every_known_red_entry_names_its_milestone_and_why()
    {
        foreach (var line in KnownRedLines())
        {
            EntryPattern().IsMatch(line).Should().BeTrue(
                $"'{line}' must read '<id> <major.minor> <part and reason>' - a red scenario with no milestone is never due");
        }
    }

    [Fact]
    public void No_known_red_entry_is_older_than_the_version_floor()
    {
        var floor = Floor();

        KnownRed().Where(e => e.Milestone < floor).Select(e => e.Id).Should().BeEmpty(
            $"the floor is {floor}, and every scenario of an earlier milestone had to be green for it");
    }

    [Fact]
    public void The_runner_refuses_to_spend_money()
    {
        var runner = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "e2e", "investigate-local.sh"));

        // The dev cluster's code-fix stage runs the REAL SDK; a c15 escalation during this suite
        // would start a paid coder. And a Job investigation is only $0 with the fake investigator.
        runner.Should().Contain("iv_switch_set codeFixMode off");
        runner.Should().Contain("trap restore EXIT");
        runner.Should().Contain("investigator-sdk is");
    }

    private static HashSet<string> Scenarios() =>
        [.. ScenarioPattern().Matches(Library()).Select(m => m.Groups[1].Value)];

    private static string Library() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "e2e", "lib", "investigate.sh"));

    private static Version Floor()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var floor = FloorPattern().Match(props);
        floor.Success.Should().BeTrue("Directory.Build.props sets MinVerMinimumMajorMinor");

        return Version.Parse(floor.Groups[1].Value);
    }

    private static List<string> KnownRedLines() =>
        [.. File.ReadLines(Path.Combine(RepoRoot(), "scripts", "e2e", "investigate", "KNOWN_RED"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))];

    private static List<(string Id, Version Milestone)> KnownRed() =>
        [.. KnownRedLines()
            .Select(l => EntryPattern().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, Version.Parse(m.Groups[2].Value)))];

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

    [GeneratedRegex(@"^scenario_(I[0-9]+)\(\)", RegexOptions.Multiline)]
    private static partial Regex ScenarioPattern();

    [GeneratedRegex(@"^IV_SCENARIOS=""([^""]*)""", RegexOptions.Multiline)]
    private static partial Regex RunListPattern();

    [GeneratedRegex(@"<MinVerMinimumMajorMinor>\s*([0-9]+\.[0-9]+)\s*</MinVerMinimumMajorMinor>")]
    private static partial Regex FloorPattern();

    [GeneratedRegex(@"^(I[0-9]+)\s+([0-9]+\.[0-9]+)\s+\S.*$")]
    private static partial Regex EntryPattern();
}
