using System.Text.Json;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// The only channel the coder has back to Hephaisto is its log, which also carries everything the
/// agent printed while reading attacker-influenceable evidence. These tests are what "trust the
/// framing and nothing else" means.
/// </summary>
public sealed class CodeFixResultParserTests
{
    private static readonly Guid Attempt = Guid.Parse("0192a6f0-0000-7000-8000-000000000001");

    private static string Sample(string name) =>
        File.ReadAllText(Path.Combine(SchemaDir, "samples", "valid", name));

    internal static string SchemaDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
                dir = dir.Parent;

            return Path.Combine(dir!.FullName, "src", "Hephaisto.Agent", "CodeFix", "Contract", "schemas");
        }
    }

    private static string Compact(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ValidPlan_Parses()
    {
        var log = "progress...\n" + CodeFixResultParser.Frame(Compact(Sample("plan-result.json")));

        var parse = CodeFixResultParser.ParsePlan(log, Attempt);

        parse.Ok.Should().BeTrue(parse.Violation);
        parse.Result!.Files.Should().ContainSingle().Which.Should().Be("src/Shop.Api/Startup/Endpoints.cs");
    }

    [Fact]
    public void ValidImplement_Parses()
    {
        var parse = CodeFixResultParser.ParseImplement(CodeFixResultParser.Frame(Compact(Sample("implement-result.json"))), Attempt);

        parse.Ok.Should().BeTrue(parse.Violation);
        parse.Result!.DeniedToolCalls.Should().ContainSingle();
    }

    [Fact]
    public void TheLastBlockWins()
    {
        var first = Compact(Sample("plan-result.json")).Replace("\"planned\"", "\"failed\"");
        var log = CodeFixResultParser.Frame(first) + "more output\n" + CodeFixResultParser.Frame(Compact(Sample("plan-result.json")));

        CodeFixResultParser.ParsePlan(log, Attempt).Result!.Outcome.Should().Be("planned");
    }

    [Fact]
    public void AShaMismatch_IsAViolation()
    {
        var framed = CodeFixResultParser.Frame(Compact(Sample("plan-result.json")));
        // Same length, so the byte count still matches and only the digest can catch it.
        var tampered = framed.Replace("\"confidence\":0.9", "\"confidence\":0.1");
        tampered.Should().NotBe(framed);

        CodeFixResultParser.ParsePlan(tampered, Attempt).Violation.Should().Contain("sha256");
    }

    [Fact]
    public void AByteCountMismatch_IsAViolation()
    {
        var framed = CodeFixResultParser.Frame(Compact(Sample("plan-result.json")));
        var wrong = System.Text.RegularExpressions.Regex.Replace(framed, @"bytes=\d+", "bytes=12");

        CodeFixResultParser.ParsePlan(wrong, Attempt).Violation.Should().Contain("declares 12 bytes");
    }

    [Fact]
    public void ATruncatedBlock_IsAViolation()
    {
        var framed = CodeFixResultParser.Frame(Compact(Sample("plan-result.json")));
        var cut = framed[..framed.IndexOf("---HEPHAISTO-RESULT-END", StringComparison.Ordinal)];

        CodeFixResultParser.ParsePlan(cut, Attempt).Ok.Should().BeFalse();
    }

    [Fact]
    public void AnUnknownMember_IsAViolation()
    {
        var json = Compact(File.ReadAllText(Path.Combine(SchemaDir, "samples", "invalid", "plan-result-unknown-member.json")));

        CodeFixResultParser.ParsePlan(CodeFixResultParser.Frame(json), Attempt).Violation.Should().Contain("contract");
    }

    [Fact]
    public void AnotherAttemptsResult_ChangesNothing()
    {
        var parse = CodeFixResultParser.ParsePlan(CodeFixResultParser.Frame(Compact(Sample("plan-result.json"))), Guid.NewGuid());

        parse.Ok.Should().BeFalse();
        parse.Violation.Should().Contain("names attempt");
    }

    [Fact]
    public void APlanResultOffered_AsAnImplementResult_IsRefused()
    {
        CodeFixResultParser.ParseImplement(CodeFixResultParser.Frame(Compact(Sample("plan-result.json"))), Attempt).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just a log with no block\n")]
    public void NoBlock_IsAViolation(string? log)
    {
        CodeFixResultParser.ParsePlan(log, Attempt).Ok.Should().BeFalse();
    }

    [Fact]
    public void AnOversizedPayload_IsAViolation()
    {
        var huge = "{\"x\":\"" + new string('a', CodeFixResultParser.MaxPayloadBytes + 10) + "\"}";

        CodeFixResultParser.ParsePlan(CodeFixResultParser.Frame(huge), Attempt).Violation.Should().Contain("cap");
    }

    // --- post-conditions ---------------------------------------------------------------------

    private static CodeFixImplementResult Opened(string url = "https://github.com/Flou21/hephaisto-fixture-dotnet/pull/7",
        string branch = "hephaisto/codefix-0192a6f00000", bool build = true, bool tests = true) => new()
    {
        AttemptId = Attempt,
        Outcome = "pr_opened",
        Branch = branch,
        PrUrl = url,
        PrNumber = 7,
        BaseCommit = null,
        Files = [],
        BuildPassed = build,
        TestsPassed = tests,
        LogTail = string.Empty,
        Deviations = [],
        CostUsd = 1,
        SessionId = null,
        Error = null,
        DeniedToolCalls = [],
    };

    private static string? Check(CodeFixImplementResult r) => CodeFixResultParser.CheckImplementPostConditions(
        r, "hephaisto/codefix-0192a6f00000", "https://github.com/Flou21/hephaisto-fixture-dotnet.git", ["github.com"], requireGreenBuild: true);

    [Fact]
    public void AGreenPrOnTheMappedRepo_FromTheAssignedBranch_Passes() => Check(Opened()).Should().BeNull();

    [Fact]
    public void AForeignRepository_IsRefused() =>
        Check(Opened(url: "https://github.com/someone/else/pull/7")).Should().Contain("not on the mapped repository");

    [Fact]
    public void AForeignHost_IsRefused() =>
        Check(Opened(url: "https://evil.example/Flou21/hephaisto-fixture-dotnet/pull/7")).Should().Contain("not allowed");

    [Fact]
    public void ADifferentBranch_IsRefused() => Check(Opened(branch: "main")).Should().Contain("assigned branch");

    [Fact]
    public void ARedBuild_IsRefused() => Check(Opened(tests: false)).Should().Contain("not green");

    [Fact]
    public void ANoPrOutcome_HasNothingToCheck() => Check(Opened() with { Outcome = "tests_failed", PrUrl = null }).Should().BeNull();
}
