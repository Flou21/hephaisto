using Hephaisto.Eval.Cli;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// The CLI's argument parsing.
/// </summary>
/// <remarks>
/// Worth its own tests because of what a silent misparse costs here. An arm run with
/// <c>--repeats</c> quietly dropped is a single noisy sample published as a three-pass mean, and
/// nothing downstream would notice.
/// </remarks>
public class EvalArgumentsTests
{
    [Fact]
    public void Options_parse_as_both_separated_and_equals_forms()
    {
        var args = EvalArguments.Parse(["--label", "baseline", "--repeats=3"]);

        args.Value("label").Should().Be("baseline");
        args.IntValue("repeats", 1).Should().Be(3);
    }

    [Fact]
    public void A_flag_does_not_swallow_the_next_option()
    {
        // `--no-judge --repeats 3` must not read "--repeats" as the value of --no-judge, which
        // would leave repeats unset and silently run one pass.
        var args = EvalArguments.Parse(["--no-judge", "--repeats", "3"]);

        args.Flag("no-judge").Should().BeTrue();
        args.IntValue("repeats", 1).Should().Be(3);
    }

    [Fact]
    public void A_repeated_option_keeps_every_value()
    {
        var args = EvalArguments.Parse(
            ["--set", "Llm:Investigation:MaxSteps=20", "--set", "Llm:Investigation:MaxOuterTurns=12"]);

        args.Multiple("set").Should().BeEquivalentTo(
            ["Llm:Investigation:MaxSteps=20", "Llm:Investigation:MaxOuterTurns=12"]);
    }

    [Fact]
    public void Bare_arguments_stay_positional()
    {
        var args = EvalArguments.Parse(["c4.json", "c7.json", "--label", "x"]);

        args.Positional.Should().BeEquivalentTo(["c4.json", "c7.json"]);
    }

    [Fact]
    public void An_unreadable_number_throws_rather_than_falling_back()
    {
        // Falling back would report a one-pass run as if three had been asked for.
        var args = EvalArguments.Parse(["--repeats", "thre"]);

        args.Invoking(a => a.IntValue("repeats", 1))
            .Should().Throw<ArgumentException>().WithMessage("*repeats*");
    }

    [Fact]
    public void An_absent_option_takes_the_fallback()
    {
        EvalArguments.Parse([]).IntValue("repeats", 1).Should().Be(1);
        EvalArguments.Parse([]).Value("label").Should().BeNull();
        EvalArguments.Parse([]).Flag("no-judge").Should().BeFalse();
    }

    [Fact]
    public void Overrides_accept_the_helm_style_double_underscore_separator()
    {
        // So an arm proven locally can be pasted into a chart's extraEnv, and back, unchanged.
        var parsed = EvalHost.ParseOverrides(["Llm__Investigation__MaxSteps=20"]).ToList();

        parsed.Should().ContainSingle();
        parsed[0].Key.Should().Be("Llm:Investigation:MaxSteps");
        parsed[0].Value.Should().Be("20");
    }

    [Fact]
    public void An_override_without_a_value_is_rejected()
    {
        var parse = () => EvalHost.ParseOverrides(["Llm:Investigation:MaxSteps"]).ToList();

        parse.Should().Throw<ArgumentException>();
    }
}
