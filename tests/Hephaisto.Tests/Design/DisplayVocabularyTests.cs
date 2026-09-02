using System.Text.RegularExpressions;
using Hephaisto.Agent.Components;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Design;

/// <summary>
/// The demo site can still read the glyph vocabulary out of <see cref="Display"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>demo-site/display.mjs</c> parses <c>Display.cs</c> at build time rather than carrying its
/// own copy, because the copy it used to carry had drifted: three of five states rendered with
/// another state's glyph, on every page of demo.hephaisto.dev, for as long as the site existed.
/// </para>
/// <para>
/// C# cannot run the JavaScript, so this asserts the thing that actually breaks it - the shape
/// the regexes match. A <c>switch</c> rewritten as a dictionary, or an arm moved onto two lines,
/// would leave <c>display.mjs</c> parsing nothing; it refuses loudly in that case, and this
/// test is what makes the refusal happen on a pull request instead of in a deploy.
/// </para>
/// </remarks>
public class DisplayVocabularyTests
{
    private static readonly Regex Arm =
        new("""^\s*[A-Za-z_][A-Za-z0-9_]*\.([A-Za-z_][A-Za-z0-9_]*)\s*=>\s*"([^"]*)"\s*,""",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static Regex Method(string name) =>
        new($$"""public static string {{name}}\([^)]*\)\s*=>\s*\w+ switch\s*\{([\s\S]*?)\n    \};""",
            RegexOptions.Multiline);

    [Fact]
    public void The_state_vocabulary_parses_and_agrees_with_the_method()
    {
        var arms = Parse("StateGlyph");

        // Every member, not most of them: a state the demo site cannot name renders as the
        // fallback "?", which looks like a deliberate unknown rather than a missing case.
        foreach (var state in Enum.GetValues<IncidentState>())
        {
            arms.Should().ContainKey(state.ToString());
            arms[state.ToString()].Should().Be(Display.StateGlyph(state));
        }
    }

    [Fact]
    public void The_state_classes_parse_and_agree_with_the_method()
    {
        var arms = Parse("StateClass");

        foreach (var state in Enum.GetValues<IncidentState>())
        {
            arms.Should().ContainKey(state.ToString());
            arms[state.ToString()].Should().Be(Display.StateClass(state));
        }
    }

    /// <summary>
    /// Deny is not named in either decision switch - it is the default arm - so the parser has
    /// to read the fallback as part of the vocabulary rather than as defensive padding.
    /// </summary>
    [Fact]
    public void The_decision_vocabulary_keeps_deny_in_the_default_arm()
    {
        var glyphs = Parse("DecisionGlyph");
        var classes = Parse("DecisionClass");

        glyphs.Should().ContainKey(nameof(PolicyDecision.Allow));
        glyphs.Should().ContainKey(nameof(PolicyDecision.RequireApproval));
        glyphs.Should().NotContainKey(nameof(PolicyDecision.Deny),
            "if Deny gains a named arm, display.mjs still reads it - but this test should be the "
            + "thing that tells you the fallback is no longer load-bearing");

        classes.Should().ContainKey(nameof(PolicyDecision.Allow));
        classes.Should().ContainKey(nameof(PolicyDecision.RequireApproval));

        Display.DecisionGlyph(PolicyDecision.Deny).Should().Be("x");
        Display.DecisionClass(PolicyDecision.Deny).Should().Be("dec-deny");
    }

    private static Dictionary<string, string> Parse(string method)
    {
        var source = File.ReadAllText(DisplayFile());
        var block = Method(method).Match(source);

        block.Success.Should().BeTrue(
            $"demo-site/display.mjs finds Display.{method} with this shape; if it has been "
            + "rewritten, that build renders every value as the fallback");

        return Arm.Matches(block.Groups[1].Value)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
    }

    private static string DisplayFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        return Path.Combine(dir!.FullName, "src", "Hephaisto.Agent", "Components", "Display.cs");
    }
}
