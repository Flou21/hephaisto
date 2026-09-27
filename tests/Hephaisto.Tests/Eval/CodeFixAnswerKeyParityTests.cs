using System.Text.RegularExpressions;
using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// <c>codefix_truth()</c> in <c>scripts/e2e/lib/judge.sh</c> and <see cref="CodeFixAnswerKey"/>
/// grade the code-fix stage against the same key, asserted against the real file.
/// </summary>
/// <remarks>
/// <see cref="AnswerKeyParityTests"/> exists because the same convention for the diagnosis key
/// drifted the first time a fixture was added from one side only, and nobody noticed until a
/// release gate had graded a denominator that silently left one out. This is that test for the
/// code-fix key, written with the key rather than after its first drift.
/// </remarks>
public class CodeFixAnswerKeyParityTests
{
    // `c15:files|c19:files)   printf '%s\n' "a" "b" ;;` - one arm, one line.
    private static readonly Regex Arm = new(
        """^\s*(?<patterns>c\d+:[a-z_]+(?:\|c\d+:[a-z_]+)*)\)\s*(?<body>.*?)\s*;;\s*$""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Command = new(
        """^(?:echo|printf\s+'%s\\n')\s+(?<args>.+)$""", RegexOptions.Compiled);

    // A "double-quoted" or 'single-quoted' shell word.
    private static readonly Regex Word = new(
        @"""(?<dq>(?:[^""\\]|\\.)*)""|'(?<sq>[^']*)'", RegexOptions.Compiled);

    private static readonly string[] Fields =
    [
        "repo", "base_ref", "root_cause", "root_cause_terms", "files", "symbols",
        "must_not_touch", "test", "forbidden", "max_files", "max_lines",
    ];

    [Fact]
    public void The_shell_key_parses_into_every_field_for_every_fixture()
    {
        var shell = ShellKey();

        // The parse is an assertion of its own: if codefix_truth is rewritten into a shape this
        // cannot read, every comparison below would be vacuous rather than failing.
        shell.Keys.Select(k => k.Fixture).Distinct().Should().NotBeEmpty(
            "codefix_truth() in judge.sh must still be a one-arm-per-line case statement");

        foreach (var fixture in shell.Keys.Select(k => k.Fixture).Distinct())
        {
            foreach (var field in Fields)
            {
                shell.Should().ContainKey((fixture, field),
                    $"codefix_truth {fixture} {field} must answer; a missing field reads as empty in shell");
            }
        }
    }

    [Fact]
    public void Every_shell_fixture_has_a_key_and_every_key_a_shell_fixture()
    {
        var shell = ShellKey().Keys.Select(k => k.Fixture).ToHashSet(StringComparer.Ordinal);
        var csharp = CodeFixAnswerKey.All.Select(k => k.Fixture).ToHashSet(StringComparer.Ordinal);

        shell.Should().BeEquivalentTo(csharp,
            "a fixture graded by one instrument and dropped by the other is the c13 defect again");
    }

    [Fact]
    public void Every_field_is_byte_identical_on_both_sides()
    {
        var shell = ShellKey();

        foreach (var key in CodeFixAnswerKey.All)
        {
            IReadOnlyList<string> Of(string field) => shell[(key.Fixture, field)];

            Of("repo").Should().Equal([key.Repo], $"{key.Fixture} repo");
            Of("base_ref").Should().Equal([key.BaseRef], $"{key.Fixture} base_ref");
            Of("root_cause").Should().Equal([key.ExpectedRootCause], $"{key.Fixture} root_cause");
            Of("root_cause_terms").Should().Equal(key.RootCauseMustMentionAnyOf, $"{key.Fixture} root_cause_terms");
            Of("files").Should().Equal(key.ExpectedFilesAnyOf, $"{key.Fixture} files");
            Of("symbols").Should().Equal(key.ExpectedSymbolsAnyOf, $"{key.Fixture} symbols");
            Of("must_not_touch").Should().Equal(key.MustNotTouch, $"{key.Fixture} must_not_touch");
            Of("test").Should().Equal([key.ExpectedTestToPass], $"{key.Fixture} test");
            Of("forbidden").Should().Equal(key.ForbiddenPatterns, $"{key.Fixture} forbidden");
            Of("max_files").Should().Equal([key.MaxFilesChanged.ToString(System.Globalization.CultureInfo.InvariantCulture)], $"{key.Fixture} max_files");
            Of("max_lines").Should().Equal([key.MaxLinesChanged.ToString(System.Globalization.CultureInfo.InvariantCulture)], $"{key.Fixture} max_lines");
        }
    }

    [Fact]
    public void Every_code_fix_fixture_also_has_a_diagnosis_key()
    {
        // The code-fix stage only runs after an investigation, and an investigation nobody
        // grades is how a code fix gets launched off a wrong diagnosis without anyone seeing it.
        foreach (var key in CodeFixAnswerKey.All)
            AnswerKey.For(key.Fixture).Should().NotBeNull($"{key.Fixture} needs a diagnosis key too");
    }

    [Fact]
    public void The_keys_are_well_formed()
    {
        foreach (var key in CodeFixAnswerKey.All)
        {
            key.BaseRef.Should().StartWith("fixture/", "fixes are made against a fixture branch, never main");
            key.ExpectedFilesAnyOf.Should().NotBeEmpty();
            key.RootCauseMustMentionAnyOf.Should().NotBeEmpty();
            key.MustNotTouch.Should().Contain("deploy/**");
            key.MaxFilesChanged.Should().BePositive();
            key.MaxLinesChanged.Should().BePositive();

            foreach (var pattern in key.ForbiddenPatterns)
                FluentActions.Invoking(() => new Regex(pattern)).Should().NotThrow($"'{pattern}' must be a .NET regex");

            // Self-consistency: the key's own statement of the cause passes its own term check,
            // or no plan could ever be graded RightLocation against it.
            key.RootCauseMustMentionAnyOf
                .Any(t => key.ExpectedRootCause.Contains(t, StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue($"{key.Fixture}'s ExpectedRootCause must mention one of its own terms");
        }
    }

    /// <summary>
    /// Each forbidden pattern catches the non-fix it is there for - a pattern that matches
    /// nothing would pass every diff and look like a strict key.
    /// </summary>
    [Theory]
    [InlineData("try { var p = Endpoints.Primary(shop); } catch { }")]
    [InlineData("catch (NullReferenceException)\n{\n}")]
    [InlineData("catch (Exception ex) { return Local; }")]
    [InlineData("catch (System.NullReferenceException) { return Endpoints.Local; }")]
    [InlineData("[Fact(Skip = \"flaky\")]")]
    [InlineData("#pragma warning disable CS8602")]
    [InlineData("Environment.Exit(0);")]
    public void The_forbidden_patterns_catch_the_non_fixes(string added)
    {
        var key = CodeFixAnswerKey.For("c15")!;

        key.ForbiddenPatterns.Any(p => Regex.IsMatch(added, p)).Should().BeTrue(
            $"'{added}' makes the test green or the pod start without fixing the defect");
    }

    /// <summary>
    /// The positive control: the reference fix the fake coder applies passes every limit the key
    /// sets. A key the known-correct answer fails would grade every coder wrong.
    /// </summary>
    [Fact]
    public void The_reference_fix_satisfies_its_own_key()
    {
        var patch = File.ReadAllLines(RepoFile("coder", "fake-scripts", "hephaisto-fixture-dotnet.fix.patch"));

        var files = patch
            .Where(l => l.StartsWith("+++ b/", StringComparison.Ordinal))
            .Select(l => l["+++ b/".Length..])
            .ToList();

        var added = patch
            .Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal))
            .Select(l => l[1..])
            .ToList();

        var removed = patch.Count(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal));

        foreach (var key in CodeFixAnswerKey.All)
        {
            files.Should().NotBeEmpty();
            files.Should().OnlyContain(f => key.IsExpectedFile(f) && !key.IsForbiddenPath(f));
            files.Count.Should().BeLessThanOrEqualTo(key.MaxFilesChanged);
            (added.Count + removed).Should().BeLessThanOrEqualTo(key.MaxLinesChanged);

            var text = string.Join('\n', added);
            key.ForbiddenPatterns.Should().OnlyContain(p => !Regex.IsMatch(text, p),
                "the reference fix is a null guard, and must not trip any forbidden pattern");
        }
    }

    /// <summary>(fixture, field) → values, as <c>codefix_truth</c> would print them.</summary>
    private static Dictionary<(string Fixture, string Field), IReadOnlyList<string>> ShellKey()
    {
        var judge = File.ReadAllText(RepoFile("scripts", "e2e", "lib", "judge.sh"));

        var start = judge.IndexOf("codefix_truth() {", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "judge.sh must still define codefix_truth()");

        var end = judge.IndexOf("\n}", start, StringComparison.Ordinal);
        var body = judge[start..end];

        var key = new Dictionary<(string, string), IReadOnlyList<string>>();

        foreach (Match arm in Arm.Matches(body))
        {
            var command = Command.Match(arm.Groups["body"].Value);
            command.Success.Should().BeTrue(
                $"arm '{arm.Groups["patterns"].Value}' must be `echo \"...\"` or `printf '%s\\n' ...`");

            var values = Word.Matches(command.Groups["args"].Value)
                .Select(w => w.Groups["dq"].Success ? Unescape(w.Groups["dq"].Value) : w.Groups["sq"].Value)
                .ToList();

            foreach (var pattern in arm.Groups["patterns"].Value.Split('|'))
            {
                var parts = pattern.Split(':');
                key.Add((parts[0], parts[1]), values);
            }
        }

        return key;
    }

    // Inside bash double quotes a backslash escapes only $ ` " \ and newline.
    private static string Unescape(string doubleQuoted) =>
        Regex.Replace(doubleQuoted, """\\([$`"\\])""", "$1");

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        return Path.Combine([dir!.FullName, .. parts]);
    }
}
