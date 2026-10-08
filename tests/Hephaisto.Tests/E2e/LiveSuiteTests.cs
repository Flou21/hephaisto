using System.Text.RegularExpressions;

namespace Hephaisto.Tests.E2e;

/// <summary>
/// The live tier's bookkeeping (v0.14.0, #249), held where every build sees it.
/// </summary>
/// <remarks>
/// <c>scripts/e2e/github-live.sh</c> is the one suite that writes on github.com, as a person and
/// through a bot whose tokens see a whole organisation. It needs accounts and a cluster; these
/// need nothing. What they pin is the part of it that must not erode quietly: that it can only
/// run where everything lands in one sandbox repository, that no line of it can name another
/// repository, and that it cannot merge, push or delete anything but its own branches.
/// </remarks>
public sealed partial class LiveSuiteTests
{
    private const string Sandbox = "TrueRelevance/hephaisto-sandbox";

    [Fact]
    public void Every_scenario_declares_its_header_and_asks_first_whether_the_agent_knows_work_items()
    {
        var scenarios = Scenarios();

        scenarios.Select(f => Path.GetFileNameWithoutExtension(f)).Should().BeEquivalentTo(
            ["L01", "L02", "L03", "L04", "L05"],
            "the four the tier was written down as (#249), and the conversation on an issue (#286); a sixth is added here too");

        foreach (var file in scenarios)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var text = File.ReadAllText(file);

            File.ReadLines(file).First().Should().MatchRegex($@"^# live: {id} \| .+$", "the runner lists and schedules by this line");
            text.Should().Contain("scenario()", $"{id} is run by calling its scenario function");
            ScenarioStart().Match(text).Success.Should().BeTrue(
                $"{id} must begin with `issues_ready || return`: every wait behind it is minutes long");
        }
    }

    /// <summary>
    /// A run opens issues with a person's account and has a bot open a pull request. Each of
    /// these is a way for that to land somewhere it must not.
    /// </summary>
    [Fact]
    public void The_runner_refuses_everything_but_the_sandbox_the_script_and_an_approver()
    {
        var runner = File.ReadAllText(Path.Combine(E2e(), "github-live.sh"));

        runner.Should().Contain("studio-rancher-desktop|rancher-desktop) ;;");
        runner.Should().Contain("is not a local dev context");
        runner.Should().Contain("which is not this machine");
        runner.Should().Contain("https://api.github.com) ;;");
        runner.Should().Contain("[ \"$repositories\" = \"$LIVE_REPO\" ]");
        runner.Should().Contain("[ \"$sdk\" = fake ]");
        runner.Should().Contain("[ \"$(env_value CodeFix__Gh)\" != shim ]");
        runner.Should().Contain("grep -qx \"$ISSUES_APPROVER_ID\"");
        runner.Should().Contain("actions/permissions");
        runner.Should().Contain("trap 'cleanup' EXIT");

        // And when somebody else works in the sandbox: another install with the bot's token
        // plans every issue of a run with ITS coder - production did, with a real model.
        runner.Should().Contain("others=$(live_foreign_takers 6");
        runner.Should().Contain("refusing: another Hephaisto took issues of the last run");
        runner.IndexOf("live_foreign_takers 6", StringComparison.Ordinal)
            .Should().BeLessThan(runner.IndexOf("trap 'cleanup' EXIT", StringComparison.Ordinal), "asked before anything is written");

        // What the last run left does not change when that install is taken off the sandbox,
        // so the refusal is lifted by a person saying so - and only that one: the flag turns
        // a `die` into a warning nowhere else (#289).
        runner.Should().Contain("--other-install-gone) OTHER_GONE=true; shift ;;");
        runner.Should().Contain("$OTHER_GONE || die \"refusing: another Hephaisto took issues of the last run");
        Regex.Matches(runner, @"\$OTHER_GONE\b").Should().HaveCount(1, "one refusal can be lifted, and no other");

        // And the statement is not trusted for longer than a scenario: this run's issues are
        // asked after each one, flag or no flag, and the run stops at the first that was taken.
        var loop = runner[runner.IndexOf("run_one \"$f\" || true", StringComparison.Ordinal)..];
        loop.Should().Contain("live_foreign_takers_of $(sort -un \"$LIVE_CREATED\")");
        loop.Should().Contain("|| die \"stopping: another Hephaisto took issues of THIS run");
        File.ReadAllText(Path.Combine(E2e(), "lib", "live.sh")).Should().Contain("!= 404 ] || echo \"$n $uuid\"", "an agent that does not answer says nothing about who took an issue");
    }

    /// <summary>
    /// The sandbox is a constant of the library, and the values file that points the dev agent
    /// at github.com lists that repository and no other.
    /// </summary>
    [Fact]
    public void The_one_repository_is_a_constant_and_the_values_file_lists_the_same_one()
    {
        var lib = File.ReadAllText(Path.Combine(E2e(), "lib", "live.sh"));

        lib.Should().Contain($"readonly LIVE_REPO=\"{Sandbox}\"");
        lib.Should().NotContain("${LIVE_REPO:-", "a repository that can be set from outside is a repository somebody will set");

        var values = File.ReadAllText(Path.Combine(RepoRoot(), "charts", "hephaisto", "values-dev-github-live.yaml"));
        var listed = Regex.Match(values, @"(?m)^  issues:\n    repositories:\n((?:      - .+\n)+)");

        listed.Success.Should().BeTrue("values-dev-github-live.yaml lists github.issues.repositories");
        listed.Groups[1].Value.Trim().Should().Be($"- {Sandbox}");
    }

    /// <summary>
    /// Every call to GitHub goes through one of two functions that put the repository's name
    /// into the request themselves. A bare <c>gh</c> anywhere else is a call that could name
    /// another one.
    /// </summary>
    [Fact]
    public void Nothing_calls_gh_but_the_two_doors()
    {
        var files = Scenarios().Append(Path.Combine(E2e(), "lib", "live.sh")).Append(Path.Combine(E2e(), "github-live.sh"));

        foreach (var file in files)
        {
            var calls = File.ReadLines(file)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith('#'))
                .SelectMany(l => BareGh().Matches(l).Select(m => (Line: l, Call: m.Value)))
                .Where(c => !Allowed.Any(a => c.Line.Contains(a, StringComparison.Ordinal)))
                .Select(c => c.Line)
                .ToList();

            calls.Should().BeEmpty($"{Path.GetFileName(file)} may reach GitHub only through _live_api and _live_gh");
        }
    }

    [Fact]
    public void It_cannot_merge_push_or_delete_anything_but_its_own_branches()
    {
        var all = string.Join("\n", Scenarios().Append(Path.Combine(E2e(), "lib", "live.sh")).Append(Path.Combine(E2e(), "github-live.sh"))
            .SelectMany(File.ReadLines)
            .Where(l => !l.TrimStart().StartsWith('#')));

        all.Should().NotMatchRegex(@"\bpr merge\b|/merge\b|merge_method", "a merge moves main, and the scripted fix applies to main as it is");
        all.Should().NotMatchRegex(@"\bgit\s+push\b", "the suite plays a person at github.com, and pushes nothing");
        all.Should().NotContain("actions/permissions\" -", "it reads whether Actions are off and never turns them on");

        // The one DELETE of a ref is behind the pattern of the runner's own branches.
        var deletes = Regex.Matches(all, @"_live_api DELETE ""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        deletes.Should().BeEquivalentTo(["issues/$2/assignees", "git/refs/heads/$1"]);
        all.Should().Contain("[[ \"$1\" =~ $LIVE_BRANCH_RE ]] || { printf 'refusing to delete %s");
        all.Should().Contain("readonly LIVE_BRANCH_RE='^hephaisto/codefix-[0-9a-f]{12}$'");
    }

    /// <summary>What the stand-in can do and GitHub cannot is refused, not imitated.</summary>
    [Theory]
    [InlineData("gh_pr_merge")]
    [InlineData("gh_pr_forget")]
    [InlineData("gh_fail")]
    [InlineData("gh_requests")]
    [InlineData("gh_issue_edit")]
    public void A_control_of_the_stand_in_is_refused_on_real_github(string control)
    {
        File.ReadAllText(Path.Combine(E2e(), "lib", "live.sh")).Should().MatchRegex($@"(?m)^{control}\(\)\s+\{{ _live_no {control}; \}}$");

        foreach (var file in Scenarios())
        {
            File.ReadAllText(file).Should().NotMatchRegex($@"\b{control}\b", $"{Path.GetFileName(file)} runs against github.com");
        }
    }

    // The two doors themselves, and the one question the runner asks about who is logged in.
    private static readonly string[] Allowed =
    [
        "gh api --method \"$method\"",
        "gh \"$noun\" \"$verb\" --repo \"$LIVE_REPO\"",
        "me=$(gh api user 2>/dev/null)",
        "gh api user >/dev/null 2>&1 ||",
        "for t in gh jq curl kubectl",
    ];

    private static List<string> Scenarios() => [.. Directory.EnumerateFiles(Path.Combine(E2e(), "live"), "L*.sh").Order(StringComparer.Ordinal)];

    private static string E2e() => Path.Combine(RepoRoot(), "scripts", "e2e");

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

    // `gh` as a command: at the start of a line, or after $( | && ; - never a function of the
    // suite's own (gh_comments), a path, or a word of prose in a string.
    [GeneratedRegex(@"(?:^|\$\(|[|;&]\s*)gh\s")]
    private static partial Regex BareGh();

    [GeneratedRegex(@"scenario\(\)\s*\{\s*\n\s*issues_ready \|\| return\b")]
    private static partial Regex ScenarioStart();
}
