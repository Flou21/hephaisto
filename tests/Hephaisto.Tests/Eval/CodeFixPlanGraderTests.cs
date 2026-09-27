using Hephaisto.Eval.Scoring;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Grading where the coder's read-only plan puts the fix - the right file, for the right reason,
/// and nothing it must not touch.
/// </summary>
public sealed class CodeFixPlanGraderTests
{
    private static readonly CodeFixAnswerKey C15 = CodeFixAnswerKey.For("c15")!;

    private const string Endpoints = "src/Shop.Api/Startup/Endpoints.cs";

    private const string GoodCause =
        "Endpoints.Primary checks options.Endpoints.Count on line 17, and ShopOptions.Endpoints is "
        + "null when the Shop:Endpoints section is absent, so startup throws.";

    [Fact]
    public void The_right_file_for_the_right_reason_is_the_right_location()
    {
        CodeFixPlanGrader.Grade(C15, [Endpoints], GoodCause, out var reason)
            .Should().Be(CodeFixPlanVerdict.RightLocation);

        reason.Should().Contain(Endpoints);
    }

    [Fact]
    public void Every_shipped_key_grades_its_own_answer_as_the_right_location()
    {
        // The positive control for the grader and the keys together: the key's own files and
        // root cause must pass it, or nothing could.
        foreach (var key in CodeFixAnswerKey.All)
        {
            CodeFixPlanGrader.Grade(key, key.ExpectedFilesAnyOf, key.ExpectedRootCause)
                .Should().Be(CodeFixPlanVerdict.RightLocation, key.Fixture);
        }
    }

    [Theory]
    [InlineData("/work/repos/hephaisto-fixture-dotnet/src/Shop.Api/Startup/Endpoints.cs")]
    [InlineData("./src/Shop.Api/Startup/Endpoints.cs")]
    [InlineData("src\\Shop.Api\\Startup\\Endpoints.cs")]
    [InlineData("src/Shop.Api/Startup/Endpoints.cs:17")]
    [InlineData("src/Shop.Api/Startup/Endpoints.cs:15-21")]
    public void The_same_file_spelled_differently_is_still_the_right_file(string file)
    {
        CodeFixPlanGrader.Grade(C15, [file], GoodCause).Should().Be(CodeFixPlanVerdict.RightLocation);
    }

    [Theory]
    [InlineData("Endpoints.cs")]                          // a bare name could be any of several
    [InlineData("src/Shop.Api/Program.cs")]               // where a catch-all would go
    [InlineData("src/Shop.Api/ShopOptions.cs")]           // defaulting the list hides the guard's absence
    [InlineData("src/Shop.Api/Startup/Endpoints.csx")]
    public void Another_file_is_the_wrong_location(string file)
    {
        CodeFixPlanGrader.Grade(C15, [file], GoodCause, out var reason)
            .Should().Be(CodeFixPlanVerdict.WrongLocation);

        reason.Should().Contain("none of");
    }

    [Theory]
    [InlineData("deploy/shop.yaml")]                                    // "add the config", "raise the limit"
    [InlineData("/work/repos/hephaisto-fixture-dotnet/deploy/shop.yaml")]
    [InlineData(".github/workflows/publish-fixtures.yml")]
    [InlineData("tests/Shop.Api.Tests/Shop.Api.Tests.csproj")]
    public void Touching_a_forbidden_path_is_wrong_even_beside_the_right_file(string forbidden)
    {
        // Approving a plan approves all of it; a plan that fixes Endpoints.cs AND adds the
        // missing variable to the manifest ships the masking change with the real one.
        CodeFixPlanGrader.Grade(C15, [Endpoints, forbidden], GoodCause, out var reason)
            .Should().Be(CodeFixPlanVerdict.WrongLocation);

        reason.Should().Contain("forbids");
    }

    [Fact]
    public void Test_source_is_not_forbidden_only_the_project_file_is()
    {
        // tests/**/*.csproj, not tests/** - adding a test is a fine thing for a fix to do.
        CodeFixPlanGrader.Grade(
                C15, [Endpoints, "tests/Shop.Api.Tests/EndpointsOptionsTests.cs"], GoodCause)
            .Should().Be(CodeFixPlanVerdict.RightLocation);
    }

    [Theory]
    [InlineData("A NullReferenceException is thrown at Endpoints.cs:17 during startup.")]
    [InlineData("The pod is in CrashLoopBackOff because the process exits 134.")]
    [InlineData("")]
    [InlineData(null)]
    public void The_right_file_without_the_mechanism_is_not_the_right_location(string? cause)
    {
        // Copying the location out of the stack trace finds the file and not the fault: what
        // was null, and why, is the half a symptom-restating plan leaves out.
        CodeFixPlanGrader.Grade(C15, [Endpoints], cause, out var reason)
            .Should().Be(CodeFixPlanVerdict.WrongLocation);

        reason.Should().Contain("root cause states none of");
    }

    [Fact]
    public void The_mechanism_is_matched_case_insensitively()
    {
        CodeFixPlanGrader.Grade(C15, [Endpoints], "SHOP:ENDPOINTS IS MISSING SO THE LIST IS NULL")
            .Should().Be(CodeFixPlanVerdict.RightLocation);
    }

    [Fact]
    public void No_file_is_no_plan_whatever_the_root_cause_says()
    {
        // not_a_code_problem, insufficient_context and a failed run all arrive here. Counted, not
        // skipped: a coder that plans less must not score higher.
        CodeFixPlanGrader.Grade(C15, null, GoodCause).Should().Be(CodeFixPlanVerdict.NoPlan);
        CodeFixPlanGrader.Grade(C15, [], GoodCause).Should().Be(CodeFixPlanVerdict.NoPlan);
        CodeFixPlanGrader.Grade(C15, ["", "   "], GoodCause).Should().Be(CodeFixPlanVerdict.NoPlan);
    }

    [Theory]
    [InlineData("deploy/**", "deploy/shop.yaml", true)]
    [InlineData("deploy/**", "deploy/overlays/prod/shop.yaml", true)]
    [InlineData("deploy/**", "deployment.md", false)]
    [InlineData("tests/**/*.csproj", "tests/Shop.Api.Tests/Shop.Api.Tests.csproj", true)]
    [InlineData("tests/**/*.csproj", "tests/Shop.Api.csproj", true)]
    [InlineData("tests/**/*.csproj", "tests/Shop.Api.Tests/EndpointsOptionsTests.cs", false)]
    [InlineData(".github/**", ".github/workflows/publish-fixtures.yml", true)]
    [InlineData(".github/**", "github/x.yml", false)]
    public void Globs_match_whole_path_segments(string glob, string path, bool matches)
    {
        CodeFixAnswerKey.GlobToRegex(glob).IsMatch(path).Should().Be(matches);
    }
}
