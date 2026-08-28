using Microsoft.Extensions.AI;
using Watchtower.Agent.Investigations;
using Watchtower.Agent.Llm;
using Watchtower.Core.Domain;

namespace Watchtower.Tests.Investigations;

/// <summary>
/// Every tool the model can call goes through this, local or MCP. A limit that holds for the
/// tools we wrote and not for the fifty a remote server happens to expose is not a limit.
/// </summary>
public class SafeToolDecoratorTests
{
    private static readonly SafeToolOptions Defaults = new();

    private static InvestigationRecorder NewRecorder(out TestClock clock)
    {
        clock = new TestClock();

        return new InvestigationRecorder(Guid.CreateVersion7(), clock, TimeSpan.FromDays(30));
    }

    private static SafeToolDecorator Wrap(
        AIFunction inner,
        SafeToolOptions? options = null,
        InvestigationBudget? budget = null,
        IInvestigationRecorder? recorder = null) =>
        new(inner, "kubernetes", options ?? Defaults, budget, recorder);

    private static AIFunction Echo(string result, string name = "get_pod_logs") =>
        AIFunctionFactory.Create((string query) => result, name, "test tool");

    // ------------------------------------------------------------------
    // Unbounded queries
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("rate(errors_total[30d])")]
    [InlineData("sum_over_time({app=\"api\"} |= \"error\" [8d])")]
    [InlineData("count_over_time({job=\"x\"}[1y])")]
    [InlineData("avg_over_time(cpu[2w])")]
    public void Rejects_range_selectors_beyond_the_cap(string query)
    {
        // The dangerous case is not that the query fails - it is that it succeeds. A [30d]
        // range against Loki reads every byte it has, and the observability stack this agent
        // depends on to see anything is what falls over.
        var tool = Wrap(Echo("never reached", "query_prometheus"));

        var rejection = tool.Reject(new Dictionary<string, object?> { ["expr"] = query });

        rejection.Should().NotBeNull();
        rejection.Should().StartWith("REFUSED");
    }

    [Theory]
    [InlineData("rate(errors_total[5m])")]
    [InlineData("sum(rate(http_requests_total[1h]))")]
    [InlineData("count_over_time({job=\"x\"}[7d])")]
    public void Allows_range_selectors_within_the_cap(string query)
    {
        var tool = Wrap(Echo("ok", "query_prometheus"));

        tool.Reject(new Dictionary<string, object?> { ["expr"] = query }).Should().BeNull();
    }

    [Fact]
    public void Rejects_a_query_tool_call_with_no_time_bound()
    {
        var tool = Wrap(Echo("ok", "query_loki_logs"));

        var rejection = tool.Reject(new Dictionary<string, object?> { ["expr"] = "{app=\"api\"}" });

        rejection.Should().NotBeNull();
        rejection.Should().Contain("no time bound");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("startRfc3339")]
    [InlineData("start_time")]
    [InlineData("since")]
    [InlineData("duration")]
    public void Accepts_any_recognised_time_bound_argument(string argumentName)
    {
        var tool = Wrap(Echo("ok", "query_loki_logs"));

        var arguments = new Dictionary<string, object?>
        {
            ["expr"] = "{app=\"api\"}",
            [argumentName] = "now-15m",
        };

        tool.Reject(arguments).Should().BeNull();
    }

    [Fact]
    public void A_non_query_tool_needs_no_time_bound()
    {
        // list_datasources cannot be unbounded. Applying the rule to it would refuse a
        // perfectly good call and cost a step to learn nothing.
        var tool = Wrap(Echo("ok", "list_datasources"));

        tool.Reject(new Dictionary<string, object?>()).Should().BeNull();
    }

    [Theory]
    [InlineData("[30d]", 30)]
    [InlineData("[1h30m]", 0)]
    [InlineData("[2w]", 14)]
    [InlineData("[1y]", 365)]
    public void Parses_composite_duration_literals(string literal, int expectedDays)
    {
        var parsed = SafeToolDecorator.ParseDuration(literal.Trim('[', ']'));

        parsed.Days.Should().Be(expectedDays);
    }

    // ------------------------------------------------------------------
    // Redaction
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("token")]
    [InlineData("api_key")]
    [InlineData("Authorization")]
    [InlineData("bearerToken")]
    [InlineData("db_password")]
    public void Redacts_credential_shaped_argument_names(string name)
    {
        // Nothing in this agent should ever pass a credential as a tool argument, but
        // "should" is not a control - and a redaction that only runs where somebody
        // remembered to call it is not one either.
        var tool = Wrap(Echo("ok"));

        var json = tool.Redact(new Dictionary<string, object?> { [name] = "hunter2" });

        json.Should().NotContain("hunter2");
        json.Should().Contain("[redacted]");
    }

    [Fact]
    public void Keeps_ordinary_arguments_readable()
    {
        var tool = Wrap(Echo("ok"));

        tool.Redact(new Dictionary<string, object?> { ["namespace"] = "watchtower-chaos" })
            .Should().Contain("watchtower-chaos");
    }

    // ------------------------------------------------------------------
    // Recording and digestion
    // ------------------------------------------------------------------

    [Fact]
    public async Task Records_the_call_and_shows_the_model_the_step_id()
    {
        var recorder = NewRecorder(out _);
        var tool = Wrap(Echo("FATAL: could not connect to mongo"), recorder: recorder);

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "logs" });

        var step = recorder.Steps.Should().ContainSingle().Subject;

        step.Kind.Should().Be(StepKind.ToolCall);
        step.ToolName.Should().Be("get_pod_logs");
        step.ToolServer.Should().Be("kubernetes");

        // The step id is prepended to what the model sees, because Evidence.StepId is how a
        // citation names its source and the model can only cite an id it was shown.
        result!.ToString().Should().Contain(step.Id.ToString());
        step.ResultDigest.Should().Contain(step.Id.ToString());
    }

    [Fact]
    public async Task What_the_model_sees_is_exactly_what_grounding_checks_against()
    {
        // If these two ever diverge, honest citations start failing and invented ones start
        // passing. This is the single most load-bearing invariant in the tool layer.
        var recorder = NewRecorder(out _);
        var tool = Wrap(Echo("panic: nil map"), recorder: recorder);

        var shown = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "logs" });

        recorder.Steps[0].ResultDigest.Should().Be(shown!.ToString());
    }

    [Fact]
    public async Task Digests_a_large_result_and_keeps_the_raw_blob()
    {
        var recorder = NewRecorder(out _);

        var noisy = string.Join('\n', Enumerable.Range(0, 20_000)
            .Select(i => $"2026-01-01T00:00:00Z health check ok request-id={Guid.NewGuid()}"));

        var tool = Wrap(Echo(noisy), recorder: recorder);

        var shown = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "logs" });

        // Digest for the model, raw for the audit.
        shown!.ToString()!.Length.Should().BeLessThan(noisy.Length / 10);

        var step = recorder.Steps.Should().ContainSingle().Subject;
        step.ResultTruncated.Should().BeTrue();
        step.RawBlobId.Should().NotBeNull();

        recorder.Blobs.Should().ContainSingle().Which.Content.Should().StartWith("2026-01-01");
    }

    [Fact]
    public async Task An_untruncated_result_gets_no_blob()
    {
        // The digest already is the whole result; storing it twice would double the largest
        // table in the database for nothing.
        var recorder = NewRecorder(out _);
        var tool = Wrap(Echo("short and complete"), recorder: recorder);

        await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "logs" });

        recorder.Blobs.Should().BeEmpty();
        recorder.Steps[0].RawBlobId.Should().BeNull();
    }

    [Fact]
    public async Task A_throwing_tool_becomes_a_readable_error_rather_than_an_exception()
    {
        // FunctionInvokingChatClient turns an exception into a failed tool result anyway, so
        // throwing would cost the explanation and gain nothing - and the explanation is what
        // lets the model fix its own call.
        var recorder = NewRecorder(out _);

        var throwing = AIFunctionFactory.Create(
            (Func<string, string>)(query => throw new InvalidOperationException("pod not found")),
            "describe_pod",
            "test tool");

        var tool = Wrap(throwing, recorder: recorder);

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "x" });

        result!.ToString().Should().Contain("pod not found");
        recorder.Steps.Should().ContainSingle().Which.Failed.Should().BeTrue();
    }

    [Fact]
    public async Task A_tool_call_past_the_budget_is_refused_in_text()
    {
        var recorder = NewRecorder(out var clock);
        var budget = new InvestigationBudget(new InvestigationBudgetOptions { MaxToolCalls = 1 }, clock);
        var tool = Wrap(Echo("ok"), budget: budget, recorder: recorder);

        await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "one" });

        var refused = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "two" });

        refused!.ToString().Should().Contain("REFUSED");
        refused!.ToString().Should().Contain("Conclude now");
    }

    [Fact]
    public async Task A_refused_query_never_reaches_the_inner_tool()
    {
        var invoked = false;

        var inner = AIFunctionFactory.Create(
            (string expr) =>
            {
                invoked = true;
                return "should not happen";
            },
            "query_prometheus",
            "test tool");

        var result = await Wrap(inner).InvokeAsync(new AIFunctionArguments
        {
            ["expr"] = "rate(errors_total[30d])",
        });

        invoked.Should().BeFalse();
        result!.ToString().Should().StartWith("REFUSED");
    }

    [Fact]
    public async Task Times_out_a_hanging_tool()
    {
        // Without this the wall-clock budget is enforced only between turns, so one hung
        // datasource query stalls the whole investigation past its deadline.
        var recorder = NewRecorder(out _);

        var hanging = AIFunctionFactory.Create(
            async (string query, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return "never";
            },
            "query_loki_logs",
            "test tool");

        var options = new SafeToolOptions { Timeout = TimeSpan.FromMilliseconds(50) };
        var tool = Wrap(hanging, options, recorder: recorder);

        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "{app=\"api\"}",
            ["start"] = "now-15m",
        });

        result!.ToString().Should().Contain("timed out");
        recorder.Steps.Should().ContainSingle().Which.Failed.Should().BeTrue();
    }

    [Fact]
    public void WrapAll_wraps_every_tool_so_none_can_be_forgotten()
    {
        AIFunction[] tools = [Echo("a", "one"), Echo("b", "two"), Echo("c", "three")];

        var wrapped = SafeToolDecorator.WrapAll(tools, "grafana-mcp", Defaults);

        wrapped.Should().HaveCount(3);
        wrapped.Should().AllBeOfType<SafeToolDecorator>();
        wrapped.Select(t => t.Name).Should().BeEquivalentTo(["one", "two", "three"]);
    }
}
