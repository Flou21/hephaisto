using System.Text.RegularExpressions;

namespace Hephaisto.Tests.E2e;

/// <summary>
/// The issues suite's bookkeeping (v0.14.0, #244), held where every build sees it.
/// </summary>
/// <remarks>
/// The suite needs a cluster; these need nothing. They pin what the pager suite's known-red list
/// is held to - every listed id is a scenario that exists, every entry names the milestone that
/// needs it green, an entry the version floor has passed fails - and two things of this suite's
/// own: a scenario cannot wait out its timeouts against an agent that has no work items, and the
/// runner cannot be pointed at a GitHub or a coder that costs something.
/// </remarks>
public sealed partial class IssuesSuiteTests
{
    [Fact]
    public void Every_known_red_id_is_a_scenario()
    {
        var scenarios = Scenarios().Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal);

        scenarios.Should().NotBeEmpty("scripts/e2e/issues holds one G<nn>.sh per scenario");
        KnownRed().Select(e => e.Id).Should().OnlyContain(id => scenarios.Contains(id),
            "a known-red id with no scenario behind it hides nothing and reads as a promise");
    }

    [Fact]
    public void Every_scenario_declares_its_header()
    {
        foreach (var file in Scenarios())
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var header = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("# issues: ", StringComparison.Ordinal));

            header.Should().NotBeNull($"{id} needs a '# issues:' line - the runner schedules by it");
            header!.Should().MatchRegex($@"^# issues: {id} \| [a-z -]+ \| (shared|exclusive) \| .+$");
            File.ReadAllText(file).Should().Contain("scenario()", $"{id} is run by calling its scenario function");
        }
    }

    /// <summary>
    /// Twelve sentences of the milestone, twelve files. A scenario that was renamed away or never
    /// written is missing from the run without anything turning red.
    /// </summary>
    [Fact]
    public void The_milestones_twelve_scenarios_are_all_there()
    {
        Scenarios().Select(f => Path.GetFileNameWithoutExtension(f)).Should().Contain(
            Enumerable.Range(1, 12).Select(n => $"G{n:00}"));
    }

    /// <summary>
    /// The issue as a conversation (#286, with #252 and #285): the planner's questions on the
    /// issue, an answer and <c>/replan</c>, a refusal of it, a failed attempt planned again, and
    /// a fresh assignment known by its time. Seventeen with the twelve.
    /// </summary>
    [Fact]
    public void The_five_scenarios_of_the_conversation_are_there()
    {
        Scenarios().Select(f => Path.GetFileNameWithoutExtension(f)).Should().Contain(
            Enumerable.Range(13, 5).Select(n => $"G{n:00}"));
    }

    /// <summary>
    /// A plan answered by a reaction (#298): an approver's rocket approves and the reactions to
    /// click are Hephaisto's own (G18); nobody else's and no other reaction does anything, and
    /// an approver's thumbs-down rejects (G19). Nineteen in all, and no file beside them.
    /// </summary>
    [Fact]
    public void The_two_scenarios_of_the_reaction_are_there_and_nothing_else_is()
    {
        Scenarios().Select(f => Path.GetFileNameWithoutExtension(f)).Should().BeEquivalentTo(
            Enumerable.Range(1, 19).Select(n => $"G{n:00}"));
    }

    /// <summary>
    /// Every wait in a scenario is a ceiling of minutes. Against an agent that does not serve
    /// work items at all, each would be waited out in turn; the guard says so in a second.
    /// </summary>
    [Fact]
    public void Every_scenario_asks_first_whether_the_agent_knows_work_items()
    {
        foreach (var file in Scenarios())
        {
            var body = File.ReadAllLines(file)
                .SkipWhile(l => !l.StartsWith("scenario()", StringComparison.Ordinal))
                .Skip(1)
                .FirstOrDefault(l => l.Trim().Length > 0);

            body.Should().NotBeNull();
            body!.Trim().Should().Be("issues_ready || return",
                $"{Path.GetFileName(file)} must start with the guard, before it opens an issue");
        }
    }

    [Fact]
    public void Every_known_red_entry_names_its_milestone_and_why()
    {
        foreach (var line in KnownRedLines())
        {
            EntryPattern().IsMatch(line).Should().BeTrue(
                $"'{line}' must read '<id> <major.minor> <stage and reason>' - a red scenario with no milestone is never due");
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
    public void The_entry_pattern_can_fail()
    {
        // The two guards above pass for the wrong reason if nothing can ever be malformed.
        EntryPattern().IsMatch("G03").Should().BeFalse();
        EntryPattern().IsMatch("G03 stage 2.4").Should().BeFalse();
        EntryPattern().IsMatch("P03 0.14 stage 2.4: approval").Should().BeFalse();
        EntryPattern().IsMatch("G03 0.14 stage 2.4 (#247): approval on the issue").Should().BeTrue();
    }

    /// <summary>
    /// Every scenario opens an issue and most approve a plan. Against github.com that writes on a
    /// repository people read; with the real coder it spends quota twelve times a run.
    /// </summary>
    [Fact]
    public void The_runner_refuses_a_real_github_a_real_coder_and_any_other_cluster()
    {
        var runner = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "e2e", "issues-local.sh"));

        runner.Should().Contain("studio-rancher-desktop|rancher-desktop) ;;");
        runner.Should().Contain("is not a local dev context");
        runner.Should().Contain("which is not this machine");
        runner.Should().Contain("*github-stand-in*)");
        runner.Should().Contain("not the stand-in");
        runner.Should().Contain("[ \"$sdk\" = fake ]");
    }

    /// <summary>
    /// The suite's helpers and the stand-in are two files in two languages. A control the helpers
    /// call and the stand-in does not serve is a 404 that reads as "the agent did nothing".
    /// </summary>
    [Theory]
    [InlineData("\"/repos/{owner}/{repo}/issues\"", "POST \"/repos/$repo/issues\"")]
    [InlineData("/issues/{{number:int}}/{verb}", "\"/repos/$1/issues/$2/assign\"")]
    [InlineData("\"assign\", \"unassign\", \"reassign\"", "\"/repos/$1/issues/$2/reassign\"")]
    [InlineData("\"/repos/{owner}/{repo}/issues/{number:int}/timeline\"", "\"/repos/$2/issues/$3/timeline\"")]
    [InlineData("\"/repos/{owner}/{repo}/issues/{number:int}/comments\"", "\"/repos/$1/issues/$2/comments\"")]
    [InlineData("\"/repos/{owner}/{repo}/pulls/{number:int}\"", "\"/repos/$1/pulls/$2\"")]
    [InlineData("control.MapDelete(\"/repos/{owner}/{repo}/pulls/{number:int}\"", "DELETE \"/repos/$1/pulls/$2\"")]
    [InlineData("\"/fail/{mode}\"", "\"/fail/$1?count=")]
    [InlineData("\"/requests\"", "GET /requests")]
    [InlineData("\"/comments\"", "GET /comments")]
    public void Every_control_the_helpers_call_is_one_the_stand_in_serves(string served, string called)
    {
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "e2e", "notification-receiver", "GitHubStandIn.cs"))
            .Should().Contain(served);
        File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "e2e", "lib", "issues.sh"))
            .Should().Contain(called);
    }

    /// <summary>
    /// How many comments the agent may write for one work item is a constant of the agent's, and
    /// G10 asserts against the suite's copy of it. Two numbers that have to be one.
    /// </summary>
    [Theory]
    [InlineData("ISSUES_ATTEMPT_COMMENT_CAP", Hephaisto.Agent.WorkItems.IssueComments.MaxPerAttempt)]
    [InlineData("ISSUES_ATTEMPT_CAP", Hephaisto.Agent.WorkItems.IssueComments.MaxAttemptsPerWorkItem)]
    [InlineData("ISSUES_COMMENT_CAP", Hephaisto.Agent.WorkItems.IssueComments.MaxPerWorkItem)]
    public void The_suites_comment_caps_are_the_agents_ceilings(string name, int ceiling)
    {
        var lib = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "e2e", "lib", "issues.sh"));
        var cap = Regex.Match(lib, $@"^{name}=""\$\{{{name}:-(\d+)\}}""$", RegexOptions.Multiline);

        cap.Success.Should().BeTrue($"lib/issues.sh sets {name} with a default");
        int.Parse(cap.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(ceiling);
    }

    [Fact]
    public void Nothing_of_the_milestones_twelve_is_known_red_any_more()
    {
        var twelve = Enumerable.Range(1, 12).Select(n => $"G{n:00}").ToHashSet(StringComparer.Ordinal);

        KnownRed().Select(e => e.Id).Where(twelve.Contains).Should().BeEmpty(
            "stage 2.4 (#247) turned the last seven green; a new entry is a new scenario, not one of the twelve");
    }

    private static Version Floor()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var floor = FloorPattern().Match(props);
        floor.Success.Should().BeTrue("Directory.Build.props sets MinVerMinimumMajorMinor");

        return Version.Parse(floor.Groups[1].Value);
    }

    private static List<string> Scenarios() => [.. Directory.EnumerateFiles(Issues(), "G*.sh").Order(StringComparer.Ordinal)];

    private static List<string> KnownRedLines() =>
        [.. File.ReadLines(Path.Combine(Issues(), "KNOWN_RED"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))];

    private static List<(string Id, Version Milestone)> KnownRed() =>
        [.. KnownRedLines()
            .Select(l => EntryPattern().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, Version.Parse(m.Groups[2].Value)))];

    private static string Issues() => Path.Combine(RepoRoot(), "scripts", "e2e", "issues");

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

    [GeneratedRegex(@"^(G[0-9]{2})\s+([0-9]+\.[0-9]+)\s+\S.*$")]
    private static partial Regex EntryPattern();
}
