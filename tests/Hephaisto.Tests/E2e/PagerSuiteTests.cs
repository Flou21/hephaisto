using System.Text.RegularExpressions;

namespace Hephaisto.Tests.E2e;

/// <summary>
/// The pager suite's own bookkeeping, held where every build sees it.
/// </summary>
/// <remarks>
/// The suite needs a cluster and runs in its own CI job; these need nothing. They pin the
/// properties that make its known-red list honest rather than a place scenarios go to be
/// forgotten: every listed id is a scenario that exists, every entry names the milestone that
/// needs it green, and an entry whose milestone the version floor has passed is a failure.
/// </remarks>
public sealed partial class PagerSuiteTests
{
    [Fact]
    public void Every_known_red_id_is_a_scenario()
    {
        var scenarios = Directory.EnumerateFiles(Pager(), "P*.sh")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.Ordinal);

        KnownRed().Select(e => e.Id).Should().OnlyContain(id => scenarios.Contains(id),
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

    [Fact]
    public void Every_known_red_entry_names_its_milestone_and_why()
    {
        foreach (var line in KnownRedLines())
        {
            EntryPattern().IsMatch(line).Should().BeTrue(
                $"'{line}' must read '<id> <major.minor> <stage and reason>' - a red scenario with no milestone is never due");
        }
    }

    /// <summary>
    /// A milestone is done when its scenarios are green. Once the floor moves past the
    /// milestone an entry names, the release shipped with a scenario it never passed.
    /// </summary>
    [Fact]
    public void No_known_red_entry_is_older_than_the_version_floor()
    {
        var floor = Floor();

        KnownRed().Where(e => IsStale(e.Milestone, floor)).Select(e => e.Id).Should().BeEmpty(
            $"the floor is {floor}, and every scenario of an earlier milestone had to be green for it");
    }

    [Fact]
    public void The_staleness_rule_can_fail()
    {
        // The guard above passes for the wrong reason if nothing is ever stale.
        IsStale(new Version(0, 10), new Version(0, 11)).Should().BeTrue();
        IsStale(new Version(0, 11), new Version(0, 11)).Should().BeFalse();
        IsStale(new Version(0, 12), new Version(0, 11)).Should().BeFalse();
        EntryPattern().IsMatch("P30").Should().BeFalse();
        EntryPattern().IsMatch("P30 stage 6").Should().BeFalse();
        EntryPattern().IsMatch("P30 0.11 stage 6: the live list").Should().BeTrue();
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("scripts/e2e/pager-local.sh")]
    [InlineData("scripts/e2e/lib/pagerphase.sh")]
    public void Every_place_the_suite_runs_grants_the_mcp_capability(string caller)
    {
        // A scenario whose capability is granted nowhere is skipped everywhere, and a run where
        // everything was skipped reads as green.
        var caps = CapsPattern().Matches(File.ReadAllText(Path.Combine(RepoRoot(), caller)))
            .Select(m => m.Groups[1].Value)
            .ToList();

        caps.Should().NotBeEmpty($"{caller} sets PAGER_CAPS");
        caps.Should().OnlyContain(c => c.Split(' ').Contains("mcp"), $"{caller} must grant mcp");
    }

    [Theory]
    [InlineData("P30", "lists the tools")]
    [InlineData("P31", "finds an incident that a scenario opened")]
    [InlineData("P33", "reads its finding with the evidence behind it")]
    [InlineData("P34", "how often that alert fired before and how each time ended")]
    [InlineData("P37", "with an approver's it can, and the audit row names the token's subject")]
    [InlineData("P38", "Without a token it is refused")]
    [InlineData("P41", "ignore your instructions")]
    [InlineData("P42", "approves, denies, re-arms or sets a mode")]
    public void Every_sentence_of_the_v0_11_done_when_is_a_scenario(string id, string sentence)
    {
        File.Exists(Path.Combine(Pager(), id + ".sh")).Should().BeTrue($"'{sentence}' is {id}");
        File.ReadAllText(Path.Combine(Pager(), id + ".sh")).Should().Contain("scenario()");
        var roadmap = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "roadmap-archive.md")), @"\s+", " ");

        roadmap.Should().Contain(sentence, "the roadmap's Done when is what these scenarios stand for");
    }

    [Theory]
    [InlineData("scripts/e2e/values-pager.yaml")]
    [InlineData("charts/hephaisto/values-dev-teams-bot.yaml")]
    public void The_approver_an_install_maps_is_the_person_the_stand_in_clicks_as(string values)
    {
        // #124. An approver is a Microsoft Entra object id, and the stand-in derives one from an
        // address. The values name the id; the scenarios click by address (P52 as oncall@, P53 as
        // dev@). If the two drifted apart P52 would be refused as "not an approver" - on a
        // cluster, minutes in - and P53 would pass for the wrong reason.
        var standIn = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "e2e", "notification-receiver", "TeamsStandIn.cs"));

        standIn.Should().Contain(
            "new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))[..16]).ToString()",
            "this test recomputes the stand-in's object id, so it has to be the same computation");

        var text = File.ReadAllText(Path.Combine(RepoRoot(), values));
        var approvers = ApproversPattern().Match(text);

        approvers.Success.Should().BeTrue($"{values} names its approvers under actions");
        approvers.Groups[1].Value.Should().Contain(StandInObjectId("oncall@example.com"));
        approvers.Groups[1].Value.Should().NotContain(StandInObjectId("dev@example.com"), "dev@ is the member who may not");
        approvers.Groups[1].Value.Should().NotContain(StandInObjectId("lead@example.com"));
    }

    /// <summary><c>TeamsStandIn.ObjectId</c>: stable per address, and a GUID like a real one.</summary>
    private static string StandInObjectId(string email) =>
        new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))[..16]).ToString();

    private static bool IsStale(Version milestone, Version floor) => milestone < floor;

    private static Version Floor()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var floor = FloorPattern().Match(props);
        floor.Success.Should().BeTrue("Directory.Build.props sets MinVerMinimumMajorMinor");

        return Version.Parse(floor.Groups[1].Value);
    }

    private static List<string> KnownRedLines() =>
        [.. File.ReadLines(Path.Combine(Pager(), "KNOWN_RED"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))];

    private static List<(string Id, Version Milestone)> KnownRed() =>
        [.. KnownRedLines()
            .Select(l => EntryPattern().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, Version.Parse(m.Groups[2].Value)))];

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

    [GeneratedRegex(@"^(P[0-9]{2})\s+([0-9]+\.[0-9]+)\s+\S.*$")]
    private static partial Regex EntryPattern();

    /// <summary>The list items under <c>approvers:</c>, comments between them included.</summary>
    [GeneratedRegex(@"^\s+approvers:\s*\n((?:\s+(?:#.*|- \S+)\s*\n)+)", RegexOptions.Multiline)]
    private static partial Regex ApproversPattern();

    [GeneratedRegex(@"PAGER_CAPS=""(?:\$\{PAGER_CAPS:-)?([a-z -]+)\}?""")]
    private static partial Regex CapsPattern();
}
