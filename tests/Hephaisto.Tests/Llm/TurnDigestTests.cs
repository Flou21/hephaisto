using Hephaisto.Agent.Llm;
using Microsoft.Extensions.AI;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// What a model turn records about itself.
/// </summary>
/// <remarks>
/// Turns used to record four numbers - tokens, cost, duration - and no text, so the step
/// trace rendered a row that expanded to an empty box. These pin the content that fixed it,
/// and the redaction boundary it must not cross.
/// </remarks>
public class TurnDigestTests
{
    private static ChatResponse Response(params AIContent[] contents) =>
        new(new ChatMessage(ChatRole.Assistant, [.. contents]));

    [Fact]
    public void A_turn_that_produced_nothing_records_null_rather_than_empty()
    {
        // Null lets the console say "no output recorded" instead of showing a blank box that
        // is indistinguishable from a bug.
        BudgetGuardChatClient.DigestOf(Response()).Should().BeNull();
    }

    [Fact]
    public void Prose_is_recorded()
    {
        var digest = BudgetGuardChatClient.DigestOf(
            Response(new TextContent("The pod cannot pull its image.")));

        digest.Should().Contain("The pod cannot pull its image.");
    }

    [Fact]
    public void Reasoning_is_recorded_and_labelled()
    {
        var digest = BudgetGuardChatClient.DigestOf(
            Response(new TextReasoningContent("The tag does not exist upstream.")));

        digest.Should().Contain("reasoning");
        digest.Should().Contain("The tag does not exist upstream.");
    }

    [Fact]
    public void A_tool_calling_turn_records_which_tools_it_asked_for()
    {
        // The common case, and the one that produced empty boxes: with
        // FunctionInvokingChatClient a turn often carries no prose at all, only calls.
        var digest = BudgetGuardChatClient.DigestOf(Response(
            new FunctionCallContent("call-1", "get_pod", new Dictionary<string, object?>()),
            new FunctionCallContent("call-2", "get_events", new Dictionary<string, object?>())));

        digest.Should().NotBeNull();
        digest.Should().Contain("get_pod");
        digest.Should().Contain("get_events");
    }

    [Fact]
    public void Tool_arguments_never_appear_in_a_turn_digest()
    {
        // SafeToolDecorator redacts arguments on the tool-call step. Serialising the raw
        // FunctionCallContent.Arguments here would put an unredacted copy of the same values
        // one expander away and quietly undo that.
        var digest = BudgetGuardChatClient.DigestOf(Response(
            new FunctionCallContent(
                "call-1",
                "query_loki",
                new Dictionary<string, object?> { ["query"] = "password=hunter2" })));

        digest.Should().NotBeNull();
        digest.Should().Contain("query_loki");
        digest.Should().NotContain("hunter2");
        digest.Should().NotContain("password");
    }

    [Fact]
    public void An_over_long_digest_is_clipped_and_says_so()
    {
        var digest = BudgetGuardChatClient.DigestOf(
            Response(new TextContent(new string('x', 50_000))));

        digest.Should().NotBeNull();
        digest!.Length.Should().BeLessThan(20_000);
        digest.Should().EndWith("clipped");
    }
}
