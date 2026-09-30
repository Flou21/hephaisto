using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaisto.Agent.CodeFix.Contract;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// Holds the C# records to the vendored JSON Schemas. The runner holds its zod types to the same
/// files and the same lock, so the two programs cannot drift apart without a lock change in a PR.
/// </summary>
public sealed class CodeFixSchemaParityTests
{
    private static string Dir => CodeFixResultParserTests.SchemaDir;

    [Fact]
    public void TheLock_MatchesEveryVendoredFile()
    {
        var lines = File.ReadAllLines(Path.Combine(Dir, "SCHEMAS.lock")).Where(l => !l.StartsWith('#') && l.Length > 0).ToList();

        lines.Should().NotBeEmpty();

        foreach (var line in lines)
        {
            var (hash, path) = (line[..64], line[66..]);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Dir, path))));

            actual.Should().Be(hash, $"{path} was edited by hand; run scripts/sync-schemas.sh instead");
        }

        var vendored = Directory.GetFiles(Dir, "*.json", SearchOption.AllDirectories).Length;
        lines.Should().HaveCount(vendored, "every vendored file is locked, and nothing unlocked is vendored");
    }

    public static TheoryData<string, Type> ValidSamples => new()
    {
        { "request-plan.json", typeof(CodeFixRequest) },
        { "request-implement.json", typeof(CodeFixRequest) },
        { "plan-result.json", typeof(CodeFixPlanResult) },
        { "implement-result.json", typeof(CodeFixImplementResult) },
        { "investigate-request.json", typeof(InvestigateRequest) },
        { "investigate-request-no-source.json", typeof(InvestigateRequest) },
        { "investigate-result.json", typeof(InvestigateResult) },
        { "investigate-result-failed.json", typeof(InvestigateResult) },
    };

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public void EveryValidSample_RoundTripsThroughTheRecords(string sample, Type type)
    {
        var json = File.ReadAllText(Path.Combine(Dir, "samples", "valid", sample));

        var value = JsonSerializer.Deserialize(json, type, CodeFixContract.Json);
        var back = JsonSerializer.Serialize(value, type, CodeFixContract.Json);

        JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)).Should().BeTrue(
            $"{sample} must survive a round trip unchanged; a lost or renamed member means the record and the schema disagree");
    }

    /// <summary>
    /// The structural refusals the C# side makes itself. Pattern and enum checks (the branch shape,
    /// the outcome vocabulary) are enforced by the parser's explicit checks, not by deserialisation.
    /// </summary>
    [Theory]
    [InlineData("plan-result-unknown-member.json", typeof(CodeFixPlanResult))]
    [InlineData("request-missing-budget.json", typeof(CodeFixRequest))]
    [InlineData("investigate-result-unknown-member.json", typeof(InvestigateResult))]
    [InlineData("investigate-request-missing-max-turns.json", typeof(InvestigateRequest))]
    public void StructurallyInvalidSamples_AreRefused(string sample, Type type)
    {
        var json = File.ReadAllText(Path.Combine(Dir, "samples", "invalid", sample));

        var act = () => JsonSerializer.Deserialize(json, type, CodeFixContract.Json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void EveryRequiredSchemaProperty_IsARecordProperty()
    {
        foreach (var (schema, type) in new[]
                 {
                     ("codefix-request.schema.json", typeof(CodeFixRequest)),
                     ("codefix-plan-result.schema.json", typeof(CodeFixPlanResult)),
                     ("codefix-implement-result.schema.json", typeof(CodeFixImplementResult)),
                     ("investigate-request.schema.json", typeof(InvestigateRequest)),
                     ("investigate-result.schema.json", typeof(InvestigateResult)),
                 })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, schema)));
            var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
            var properties = type.GetProperties()
                .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod is not null)
                .Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name))
                .ToHashSet();

            properties.Should().BeEquivalentTo(required, $"{type.Name} and {schema} must name the same members");
        }
    }
}
