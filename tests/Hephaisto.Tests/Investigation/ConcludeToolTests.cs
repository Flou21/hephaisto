using System.Text.Json;
using Microsoft.Extensions.AI;
using Hephaisto.Agent.Investigations;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The argument shape the <c>conclude</c> tool accepts.
/// </summary>
/// <remarks>
/// <para>
/// The tool used to take a single <c>ConcludeRequest request</c> parameter, which generates a
/// schema wrapping the whole payload in a property called <c>request</c>. Models do not
/// reliably emit that wrapper. <c>gpt-oss:120b</c> sent the flat object on its first attempt
/// in every one of the ten published demo transcripts and every c12 replay, was told
/// <c>"missing a value for the required parameter 'request'"</c>, and spent two further steps
/// recovering - on every investigation, for as long as the wrapper existed.
/// </para>
/// <para>
/// So the schema is flat now, and these pin that the binder still takes the wrapper. That
/// direction cannot be measured here: the models that send it are DeepSeek and Gemini, both
/// of which cost real money to run, so the guarantee has to come from a test rather than from
/// a bakeoff. See <c>docs/backlog.md</c> #86.
/// </para>
/// </remarks>
public class ConcludeToolTests
{
    private const string Flat = """
        {
          "summary": "The lease at /data/lease names this pod.",
          "confidence": 0.9,
          "findings": [
            {
              "category": "application",
              "hypothesis": "The entrypoint refuses to re-take its own lease.",
              "confidence": 0.9,
              "primary": true,
              "evidence": [ { "step_id": "3", "excerpt": "FATAL: refusing to re-take it" } ]
            }
          ]
        }
        """;

    private static readonly string Wrapped = $$"""{ "request": {{Flat}} }""";

    private static async Task<InvestigationRunner.ConclusionHolder> InvokeAsync(string json)
    {
        var holder = new InvestigationRunner.ConclusionHolder();
        var tool = InvestigationRunner.CreateConcludeTool(holder);

        var arguments = new AIFunctionArguments(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
                .ToDictionary(p => p.Key, p => (object?)p.Value));

        await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);

        return holder;
    }

    [Fact]
    public async Task The_flat_shape_is_recorded()
    {
        var holder = await InvokeAsync(Flat);

        holder.Value.Should().NotBeNull();
        holder.Value!.Summary.Should().Contain("/data/lease");
        holder.Value.Confidence.Should().Be(0.9);
        holder.Value.Findings.Should().ContainSingle()
            .Which.Evidence.Should().ContainSingle()
            .Which.Excerpt.Should().Be("FATAL: refusing to re-take it");
    }

    [Fact]
    public async Task The_wrapped_shape_is_still_recorded()
    {
        var holder = await InvokeAsync(Wrapped);

        holder.Value.Should().NotBeNull();
        holder.Value!.Summary.Should().Contain("/data/lease");
        holder.Value.Confidence.Should().Be(0.9);
        holder.Value.Findings.Should().ContainSingle()
            .Which.Hypothesis.Should().Contain("re-take its own lease");
    }

    /// <summary>
    /// The schema is what the model reads, so it is the thing that has to be flat. A test on
    /// the binder alone would pass just as well with the wrapper back in the schema, and the
    /// wrapper in the schema is what cost the two steps.
    /// </summary>
    [Fact]
    public void The_schema_names_the_fields_and_not_a_wrapper()
    {
        var tool = InvestigationRunner.CreateConcludeTool(new InvestigationRunner.ConclusionHolder());
        var properties = tool.JsonSchema.GetProperty("properties");

        properties.TryGetProperty("findings", out _).Should().BeTrue();
        properties.TryGetProperty("summary", out _).Should().BeTrue();
        properties.TryGetProperty("request", out _).Should().BeFalse();
    }

    /// <summary>
    /// The shape that skipped planning. gpt-oss:120b answers the flat schema by filling the
    /// top-level <c>confidence</c> and leaving the finding's own empty; before the two were
    /// nullable that bound the primary finding to <c>0</c>, which is below
    /// <c>MinConfidenceForPlan</c>, so the run escalated LowConfidence and phase 2 never ran.
    /// It scored NoPlan - the same cell as a planner that considered the incident and
    /// declined.
    /// </summary>
    [Fact]
    public async Task A_top_level_confidence_carries_to_a_finding_that_omitted_one()
    {
        var holder = await InvokeAsync("""
            {
              "summary": "s",
              "confidence": 0.92,
              "findings": [ { "category": "application", "hypothesis": "h", "primary": true } ]
            }
            """);

        var findings = ConcludeMapper.ToFindings(holder.Value!, Guid.NewGuid(), []);

        findings.Should().ContainSingle().Which.Confidence.Should().Be(0.92);
    }

    /// <summary>
    /// And the finding's own number still wins where it gave one, because the top-level field
    /// is a fallback rather than an override.
    /// </summary>
    [Fact]
    public async Task A_finding_that_gave_its_own_confidence_keeps_it()
    {
        var holder = await InvokeAsync(Flat);

        var findings = ConcludeMapper.ToFindings(holder.Value!, Guid.NewGuid(), []);

        findings.Should().ContainSingle().Which.Confidence.Should().Be(0.9);
    }

    /// <summary>
    /// A conclusion that named findings and forgot the summary is still worth grounding.
    /// Throwing on the missing field would turn one omission into an investigation with no
    /// findings at all, which is the failure the wrapper was already causing.
    /// </summary>
    [Fact]
    public async Task A_missing_field_binds_to_its_default_rather_than_failing()
    {
        var holder = await InvokeAsync("""
            { "findings": [ { "category": "unknown", "hypothesis": "h", "primary": true } ] }
            """);

        holder.Value.Should().NotBeNull();
        holder.Value!.Summary.Should().BeEmpty();
        holder.Value.Findings.Should().ContainSingle();
    }
}
