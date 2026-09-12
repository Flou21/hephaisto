using System.Text.Json;

using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// The empty-role repair (the fault class carried unfixed through v0.7.0 and v0.8.0).
/// </summary>
/// <remarks>
/// <para>
/// Reproduced on demand while recording the c13 cassette: Ollama's OpenAI-compatible endpoint
/// serving gpt-oss:120b returned a completion choice whose <c>role</c> was the empty string, the
/// SDK threw <c>Unknown ChatMessageRole value</c> out of its deserialiser, and the investigation
/// faulted after nine successful tool calls with its work discarded.
/// </para>
/// <para>
/// Half of these tests assert that nothing is changed. That is the more important half: a handler
/// that rewrites bodies it does not understand would hide the next defect of this class rather
/// than surface it, and it sits on the path of every single model call.
/// </para>
/// </remarks>
public sealed class MalformedRoleRepairTests
{
    [Fact]
    public void An_empty_role_becomes_assistant()
    {
        var repaired = Repair("""
            {"choices":[{"index":0,"message":{"role":"","content":"hello"}}]}
            """);

        Role(repaired, 0).Should().Be("assistant");
    }

    /// <summary>Absent and empty are the same defect: the SDK maps both onto the empty string.</summary>
    [Fact]
    public void A_missing_role_becomes_assistant()
    {
        var repaired = Repair("""
            {"choices":[{"index":0,"message":{"content":"hello"}}]}
            """);

        Role(repaired, 0).Should().Be("assistant");
    }

    [Fact]
    public void Every_broken_choice_is_repaired()
    {
        var repaired = Repair("""
            {"choices":[
              {"index":0,"message":{"role":"","content":"a"}},
              {"index":1,"message":{"role":"assistant","content":"b"}},
              {"index":2,"message":{"content":"c"}}
            ]}
            """);

        Role(repaired, 0).Should().Be("assistant");
        Role(repaired, 1).Should().Be("assistant");
        Role(repaired, 2).Should().Be("assistant");
    }

    /// <summary>
    /// The content is untouched, including tool calls.
    /// </summary>
    /// <remarks>
    /// The fault arrives mid-conversation, on the turn that carries the model's tool calls, so a
    /// repair that dropped them would trade a visible fault for a silent one.
    /// </remarks>
    [Fact]
    public void Repairing_preserves_everything_else()
    {
        var repaired = Repair("""
            {"id":"x","model":"gpt-oss:120b","choices":[{"index":0,"finish_reason":"tool_calls",
              "message":{"role":"","content":null,"tool_calls":[
                {"id":"call_1","type":"function","function":{"name":"get_pod_logs","arguments":"{}"}}]}}],
             "usage":{"prompt_tokens":10,"completion_tokens":2}}
            """);

        using var doc = JsonDocument.Parse(repaired);
        var root = doc.RootElement;

        root.GetProperty("id").GetString().Should().Be("x");
        root.GetProperty("model").GetString().Should().Be("gpt-oss:120b");
        root.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(10);

        var message = root.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("role").GetString().Should().Be("assistant");

        var call = message.GetProperty("tool_calls")[0];
        call.GetProperty("id").GetString().Should().Be("call_1");
        call.GetProperty("function").GetProperty("name").GetString().Should().Be("get_pod_logs");
    }

    // --- the half that must change nothing ---------------------------------------------------

    [Fact]
    public void A_well_formed_response_is_left_alone() =>
        MalformedRoleRepairHandler.TryRepair(
                """{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}]}""",
                out _)
            .Should().BeFalse();

    /// <summary>
    /// A role that is merely unexpected is NOT repaired.
    /// </summary>
    /// <remarks>
    /// Only the empty string is the known defect. Rewriting an unrecognised-but-present role
    /// would be inventing an answer, and the resulting throw is information: it says the provider
    /// is doing something new.
    /// </remarks>
    [Fact]
    public void An_unexpected_but_present_role_is_left_alone() =>
        MalformedRoleRepairHandler.TryRepair(
                """{"choices":[{"index":0,"message":{"role":"something-new","content":"hi"}}]}""",
                out _)
            .Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"error":{"message":"rate limited"}}""")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"index":0}]}""")]
    public void Anything_without_the_defect_is_left_alone(string body) =>
        MalformedRoleRepairHandler.TryRepair(body, out _).Should().BeFalse();

    [Fact]
    public void Malformed_json_does_not_throw()
    {
        var act = () => MalformedRoleRepairHandler.TryRepair("{not json", out _);

        act.Should().NotThrow("the SDK should produce its own error rather than one invented here");
    }

    // ----------------------------------------------------------------------------------

    private static string Repair(string body)
    {
        MalformedRoleRepairHandler.TryRepair(body, out var repaired).Should().BeTrue();
        return repaired;
    }

    private static string? Role(string body, int choice) =>
        JsonDocument.Parse(body).RootElement
            .GetProperty("choices")[choice]
            .GetProperty("message")
            .GetProperty("role")
            .GetString();
}
