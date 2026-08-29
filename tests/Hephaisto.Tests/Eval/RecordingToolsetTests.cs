using System.Text.Json;
using Microsoft.Extensions.AI;
using Hephaisto.Agent.Investigations;
using Hephaisto.Core.Domain;
using Hephaisto.Eval;
using Hephaisto.Tests.Investigations;
using static Hephaisto.Tests.Eval.EvalScaffolding;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Recording a live tool surface, and the contract between recording and replay.
/// </summary>
/// <remarks>
/// The round-trip test is the one that matters. Record and replay are two halves of one claim -
/// that a cassette reproduces what the cluster said - and either half can be individually correct
/// while the pair disagrees. The most likely way that happens is silent: if the two ever serialise
/// arguments differently, every replay degrades to a miss and the harness still reports a number.
/// </remarks>
public class RecordingToolsetTests
{
    private static AIFunction LogsTool(string result = LogLine) =>
        AIFunctionFactory.Create((string pod) => result, "get_pod_logs", "Reads a pod's logs.");

    private static AIFunction ThrowingTool() =>
        AIFunctionFactory.Create(
            string (string pod) => throw new InvalidOperationException("the server could not find the pod"),
            "get_pod_logs",
            "Reads a pod's logs.");

    // ------------------------------------------------------------------ declarations

    [Fact]
    public void The_declared_surface_is_captured_verbatim()
    {
        var toolset = new RecordingToolset();
        toolset.Wrap([LogsTool()], "kubernetes");

        var declaration = toolset.Declarations.Should().ContainSingle().Subject;

        declaration.Name.Should().Be("get_pod_logs");
        declaration.Description.Should().Be("Reads a pod's logs.");
        declaration.Server.Should().Be("kubernetes");
        declaration.Schema.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void Wrapping_does_not_alter_the_surface_the_model_sees()
    {
        var inner = LogsTool();
        var wrapped = new RecordingToolset().Wrap([inner], "kubernetes").Single();

        wrapped.Name.Should().Be(inner.Name);
        wrapped.Description.Should().Be(inner.Description);
        JsonSerializer.Serialize(wrapped.JsonSchema)
            .Should().Be(JsonSerializer.Serialize(inner.JsonSchema));
    }

    // ------------------------------------------------------------------ calls

    [Fact]
    public async Task A_call_is_recorded_with_its_arguments_and_raw_result()
    {
        var toolset = new RecordingToolset();
        var tool = toolset.Wrap([LogsTool()], "kubernetes").Single();

        await tool.InvokeAsync(new AIFunctionArguments { ["pod"] = "api" }, CancellationToken.None);

        var call = toolset.Calls.Should().ContainSingle().Subject;
        call.ToolName.Should().Be("get_pod_logs");
        call.RawResult.Should().Be(LogLine);
        call.Error.Should().BeNull();
        ReplayToolset.Canonicalise(call.ArgumentsJson).Should().Contain("api");
    }

    [Fact]
    public async Task A_failing_tool_is_recorded_and_the_failure_still_propagates()
    {
        var toolset = new RecordingToolset();
        var tool = toolset.Wrap([ThrowingTool()], "kubernetes").Single();

        // Rethrow matters: the real decorator above has to see the failure and handle it exactly
        // as it would in production. A cassette that swallowed failures would replay a cluster in
        // which nothing ever goes wrong.
        var act = async () => await tool.InvokeAsync(
            new AIFunctionArguments { ["pod"] = "api" }, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        var call = toolset.Calls.Should().ContainSingle().Subject;
        call.RawResult.Should().BeNull();
        call.Error.Should().Contain("could not find the pod");
    }

    [Fact]
    public async Task Grafana_tools_are_recorded_too()
    {
        var toolset = new RecordingToolset();
        var provider = new RecordingGrafanaToolProvider(
            new StubGrafanaTools(AIFunctionFactory.Create(
                (string query) => "no data", "query_loki_logs", "Runs a LogQL query.")),
            toolset);

        var tools = await provider.GetToolsAsync(CancellationToken.None);
        await tools.Single().InvokeAsync(
            new AIFunctionArguments { ["query"] = "{app=\"api\"}" }, CancellationToken.None);

        toolset.Declarations.Should().ContainSingle()
            .Which.Server.Should().Be("grafana-mcp");
        toolset.Calls.Should().ContainSingle()
            .Which.ToolName.Should().Be("query_loki_logs");
    }

    [Fact]
    public async Task Live_recording_never_contains_redacted_arguments()
    {
        var toolset = new RecordingToolset();
        var tool = toolset.Wrap([LogsTool()], "kubernetes").Single();

        await tool.InvokeAsync(new AIFunctionArguments { ["pod"] = "api" }, CancellationToken.None);

        // The recorder sits inside SafeToolDecorator, so it sees what the model sent. This is the
        // property that makes in-process recording work where a database export would not.
        toolset.RedactedArguments.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ the round trip

    [Fact]
    public async Task A_recorded_investigation_replays_identically_with_no_misses()
    {
        // --- record, against a "live" tool ---
        var recorder = new RecordingToolset();

        var recordedOutcome = await Run(recorder.Wrap([LogsTool()], "kubernetes"));

        recordedOutcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);

        var cassette = new Cassette
        {
            Id = "c2-crashloop",
            Description = "api crash-loops because mongo is unreachable",
            ExpectedRootCause = "The api container cannot reach mongo and exits non-zero.",
            Tools = recorder.Declarations,
            Calls = recorder.Calls,
        };

        cassette.Calls.Should().NotBeEmpty("the recording is the input to the replay");

        // --- replay, from the recording alone ---
        var replay = new ReplayToolset(cassette);

        var replayedOutcome = await Run(replay.Functions);

        // Same conclusion, reached from bytes that came off disk rather than off a cluster.
        replayedOutcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);

        replayedOutcome.Investigation.Findings.Should().ContainSingle()
            .Which.Evidence.Should().ContainSingle()
            .Which.Excerpt.Should().Be(
                recordedOutcome.Investigation.Findings.Single().Evidence.Single().Excerpt);

        // The contract: every call the replayed run made was answered exactly. A fuzzy hit or a
        // miss here would mean record and replay disagree about what a call *is*.
        var summary = replay.Summarise();
        summary.Missed.Should().Be(0);
        summary.Fuzzy.Should().Be(0);
        summary.Exact.Should().Be(summary.Total);
        summary.Total.Should().BeGreaterThan(0);
    }

    private static async Task<InvestigationOutcome> Run(IEnumerable<AIFunction> tools)
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool(
                "c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool(
                "c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        return await Runner(
                new FakeChatClientFactory(FreePricing, investigation, NoActionPlanner()),
                tools)
            .RunAsync(NewIncident(), CancellationToken.None);
    }
}
