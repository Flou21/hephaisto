using System.Text.RegularExpressions;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// The two graders score the same fixtures against the same truth, asserted against the real
/// files rather than against goodwill.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AnswerKey"/>'s own remarks say <c>ExpectedRootCause</c> is copied verbatim from
/// <c>fixture_truth()</c> in <c>scripts/e2e/lib/judge.sh</c>, "because two graders scoring the
/// same fixture against differently worded truths would produce two incomparable numbers".
/// Nothing enforced that, and it drifted the first time a fixture was added from the C# side:
/// c13 shipped with an answer key, entered <c>FULL_FIXTURES</c>, and had no <c>fixture_truth</c>
/// arm at all. The shell guard was a bare <c>continue</c> that fired before the skip branch, so
/// the release gate graded a denominator that silently omitted the one fixture the release's
/// headline claim rests on - and printed no line saying so.
/// </para>
/// <para>
/// That is the same defect as backlog #37, which the comment immediately below that guard
/// describes. A convention tracked by hand came back the moment nobody was looking, so it is a
/// test now. Same shape as <c>ShippedAlertRulesTests</c>: read the file that actually ships and
/// fail on drift.
/// </para>
/// </remarks>
public class AnswerKeyParityTests
{
    // `c13) echo "..." ;;` - the arms of the case statement, as written.
    private static readonly Regex TruthArm = new(
        """^\s*(c\d+)\)\s*echo\s*"((?:[^"\\]|\\.)*)"\s*;;""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FullFixtures = new(
        @"^FULL_FIXTURES=""([^""]*)""", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void Every_shell_truth_is_the_answer_key_verbatim()
    {
        var arms = ShellTruths();

        // The parse itself is an assertion: a case statement rewritten as an associative array
        // would silently match nothing and pass every comparison below.
        arms.Should().HaveCountGreaterThan(9,
            "fixture_truth() in judge.sh must still be a case statement this regex can read - "
            + "an empty parse would make every assertion in this class vacuous");

        foreach (var (fixture, shellTruth) in arms)
        {
            var key = AnswerKey.For(fixture);

            key.Should().NotBeNull(
                $"judge.sh grades {fixture}, so hephaisto-eval must grade it against the same truth");

            key!.ExpectedRootCause.Should().Be(shellTruth,
                $"the two graders must score {fixture} against a byte-identical truth, or their "
                + "numbers are not comparable and neither is 'the' accuracy");
        }
    }

    [Fact]
    public void Every_answer_key_has_a_shell_truth()
    {
        var shell = ShellTruths().Select(a => a.Fixture).ToHashSet(StringComparer.Ordinal);

        var missing = AnswerKey.All
            .Select(k => k.Fixture)
            .Where(f => !shell.Contains(f))
            .ToList();

        missing.Should().BeEmpty(
            "a fixture with an answer key but no fixture_truth() arm is graded by the replay "
            + "harness and dropped by the cluster harness, which is how c13 entered the release "
            + "gate ungraded");
    }

    /// <summary>
    /// The release gate cannot contain a fixture neither grader can score.
    /// </summary>
    /// <remarks>
    /// This is the assertion that would have caught c13 on the commit that added it. The two
    /// tests above compare the graders to each other; this one compares them to the corpus the
    /// gate actually runs, which is the set that decides what the release claims.
    /// </remarks>
    [Fact]
    public void Every_fixture_in_the_release_gate_can_be_graded()
    {
        var chaos = File.ReadAllText(RepoFile("scripts", "e2e", "lib", "chaos.sh"));

        var declared = FullFixtures.Match(chaos);
        declared.Success.Should().BeTrue("chaos.sh must still declare FULL_FIXTURES as a literal");

        var gate = declared.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        gate.Should().NotBeEmpty();

        var shell = ShellTruths().Select(a => a.Fixture).ToHashSet(StringComparer.Ordinal);

        foreach (var fixture in gate)
        {
            shell.Should().Contain(fixture,
                $"--full runs {fixture}, so judge.sh must be able to grade it rather than "
                + "dropping it from the denominator");

            AnswerKey.For(fixture).Should().NotBeNull(
                $"--full runs {fixture}, so it needs an answer key");
        }
    }

    private static List<(string Fixture, string Truth)> ShellTruths()
    {
        var judge = File.ReadAllText(RepoFile("scripts", "e2e", "lib", "judge.sh"));

        return TruthArm.Matches(judge)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();
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
