using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;
using Hephaisto.Eval;
using Hephaisto.Tests.Investigations;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// The replay seam: a recorded tool surface, answered from the recording.
/// </summary>
/// <remarks>
/// The last test is the one that matters. It drives the real <see cref="InvestigationRunner"/>
/// over replayed tools, so the recorded bytes travel the production path - the real
/// <c>FunctionInvokingChatClient</c>, the real <c>SafeToolDecorator</c>, the real digester and
/// the real step recorder. A test of <see cref="ReplayToolset"/> alone would prove that a
/// dictionary lookup works, which was never in doubt.
/// </remarks>
public class ReplayToolsetTests
{
    private const string LogLine = "FATAL: could not connect to mongo: connection refused";

    private static readonly LlmPricing FreePricing = new(new Dictionary<string, ModelPrice>());

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly string LogsSchema =
        """{"type":"object","properties":{"pod":{"type":"string"}},"required":["pod"]}""";

    private static ToolDeclaration LogsTool() => new()
    {
        Name = "get_pod_logs",
        Description = "Reads a pod's logs.",
        Server = "kubernetes",
        Schema = Schema(LogsSchema),
    };

    private static Cassette CassetteWith(params RecordedCall[] calls) => new()
    {
        Id = "c2-crashloop",
        Description = "api crash-loops because mongo is unreachable",
        ExpectedRootCause = "The api container cannot reach mongo and exits non-zero.",
        Tools = [LogsTool()],
        Calls = calls,
    };

    private static RecordedCall Call(string args, string result) => new()
    {
        ToolName = "get_pod_logs",
        ArgumentsJson = args,
        RawResult = result,
    };

    // ------------------------------------------------------------------ matching

    [Fact]
    public void Argument_order_does_not_decide_a_match()
    {
        ReplayToolset.Key("t", """{"b":2,"a":1}""")
            .Should().Be(ReplayToolset.Key("t", """{"a":1,"b":2}"""));
    }

    [Fact]
    public void An_explicit_null_argument_asks_the_same_question_as_an_absent_one()
    {
        ReplayToolset.Key("t", """{"ns":"x","selector":null}""")
            .Should().Be(ReplayToolset.Key("t", """{"ns":"x"}"""));
    }

    [Fact]
    public void Array_order_is_preserved_because_it_is_a_list_not_a_set()
    {
        ReplayToolset.Key("t", """{"a":[1,2]}""")
            .Should().NotBe(ReplayToolset.Key("t", """{"a":[2,1]}"""));
    }

    [Fact]
    public void Unparseable_arguments_are_still_a_stable_key()
    {
        ReplayToolset.Canonicalise("not json").Should().Be("not json");
    }

    // ------------------------------------------------------------------ resolution

    [Fact]
    public void An_exact_match_replays_the_recorded_output()
    {
        var toolset = new ReplayToolset(CassetteWith(Call("""{"pod":"api"}""", LogLine)));

        toolset.Resolve("get_pod_logs", """{"pod":"api"}""").Should().Be(LogLine);
        toolset.Summarise().Exact.Should().Be(1);
        toolset.Summarise().Missed.Should().Be(0);
    }

    [Fact]
    public void A_single_recording_answers_a_differently_phrased_question_and_says_it_was_fuzzy()
    {
        var toolset = new ReplayToolset(CassetteWith(Call("""{"pod":"api"}""", LogLine)));

        toolset.Resolve("get_pod_logs", """{"pod":"api-7d9f8-xk2p1"}""").Should().Be(LogLine);

        var summary = toolset.Summarise();
        summary.Fuzzy.Should().Be(1);
        summary.Exact.Should().Be(0);
        summary.Missed.Should().Be(0);
    }

    [Fact]
    public void With_two_recordings_an_unrecorded_call_is_a_miss_and_says_so_plainly()
    {
        var toolset = new ReplayToolset(CassetteWith(
            Call("""{"pod":"api"}""", LogLine),
            Call("""{"pod":"worker"}""", "worker is fine")));

        var answer = toolset.Resolve("get_pod_logs", """{"pod":"ghost"}""");

        // The distinction that matters: the model must not read this as "no such logs exist".
        answer.Should().Contain("No output for this call was recorded");
        answer.Should().Contain("unknown rather than as an empty result");

        var summary = toolset.Summarise();
        summary.Missed.Should().Be(1);
        summary.MissRate.Should().Be(1.0);
        summary.MissedTools.Should().ContainSingle().Which.Should().Be("get_pod_logs");
    }

    [Fact]
    public void A_recorded_failure_is_replayed_as_a_failure()
    {
        var cassette = CassetteWith(new RecordedCall
        {
            ToolName = "get_pod_logs",
            ArgumentsJson = """{"pod":"api"}""",
            Error = "the server could not find the requested resource",
        });

        new ReplayToolset(cassette).Resolve("get_pod_logs", """{"pod":"api"}""")
            .Should().Contain("tool error (recorded)");
    }

    [Fact]
    public void The_recorded_schema_is_handed_back_verbatim()
    {
        var function = new ReplayToolset(CassetteWith()).Functions.Single();

        function.Name.Should().Be("get_pod_logs");
        function.Description.Should().Be("Reads a pod's logs.");
        JsonSerializer.Serialize(function.JsonSchema)
            .Should().Be(JsonSerializer.Serialize(Schema(LogsSchema)));
    }

    [Fact]
    public void A_cassette_survives_a_round_trip_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cassette-{Guid.NewGuid():N}.json");

        try
        {
            CassetteWith(Call("""{"pod":"api"}""", LogLine)).Save(path);
            var loaded = Cassette.Load(path);

            loaded.Id.Should().Be("c2-crashloop");
            loaded.Tools.Should().ContainSingle();
            loaded.Calls.Should().ContainSingle();

            new ReplayToolset(loaded).Resolve("get_pod_logs", """{"pod":"api"}""")
                .Should().Be(LogLine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ the real loop

    [Fact]
    public async Task Replayed_output_reaches_the_model_through_the_real_investigation_loop()
    {
        var toolset = new ReplayToolset(CassetteWith(Call("""{"pod":"api"}""", LogLine)));

        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool(
                "c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool(
                "c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(
            """{"summary":"Nothing to do.","noActionRequired":true,"actions":[]}"""));

        var outcome = await Runner(
                new FakeChatClientFactory(FreePricing, investigation, planning),
                toolset.Functions)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);

        // Grounding checks the citation against what the model was SHOWN. That it survives is
        // the proof that the recorded bytes travelled the real decorator and digester path.
        outcome.Investigation.Findings.Should().ContainSingle()
            .Which.Evidence.Should().ContainSingle()
            .Which.Excerpt.Should().Be(LogLine);

        outcome.Rejections.Should().BeEmpty();

        var summary = toolset.Summarise();
        summary.Exact.Should().Be(1);
        summary.Missed.Should().Be(0);
    }

    [Fact]
    public async Task A_tool_the_recording_never_answered_is_reported_rather_than_faked()
    {
        var toolset = new ReplayToolset(CassetteWith(
            Call("""{"pod":"api"}""", LogLine),
            Call("""{"pod":"worker"}""", "worker is fine")));

        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool(
                "c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "ghost" }),
            (_, conversation) => FakeChatClient.CallsTool(
                "c2", "conclude", ConcludeArgs(conversation, "No output for this call was recorded")),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(
            """{"summary":"Nothing to do.","noActionRequired":true,"actions":[]}"""));

        await Runner(new FakeChatClientFactory(FreePricing, investigation, planning), toolset.Functions)
            .RunAsync(NewIncident(), CancellationToken.None);

        var summary = toolset.Summarise();
        summary.Missed.Should().Be(1);
        summary.MissedTools.Should().Contain("get_pod_logs");
        summary.ToString().Should().Contain("1 missed");
    }

    // ------------------------------------------------------------------ scaffolding

    private static Incident NewIncident() => new()
    {
        Title = "hephaisto-chaos/api is crash-looping",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        OpenedAt = DateTimeOffset.UnixEpoch,
        LastSignalAt = DateTimeOffset.UnixEpoch,
        Target = new TargetRef
        {
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "api-7d9f8-xk2p1",
            OwnerKind = "Deployment",
            OwnerName = "api",
        },
    };

    private static InvestigationRunner Runner(
        FakeChatClientFactory factory,
        IEnumerable<AIFunction> tools)
    {
        var clock = new TestClock();

        var grafana = new GrafanaMcpToolProvider(
            new TestOptionsMonitor<GrafanaOptions>(new GrafanaOptions()),
            clock,
            NullLoggerFactory.Instance);

        return new InvestigationRunner(
            factory,
            new PromptComposer(Options.Create(new EnvironmentCardOptions())),
            tools,
            grafana,
            new NullGlobalLlmBudget(),
            new Hephaisto.Agent.Pipeline.InvestigationTracker(clock),
            clock,
            new TestOptionsMonitor<LlmOptions>(new LlmOptions()),
            new TestOptionsMonitor<InvestigationOptions>(new InvestigationOptions()),
            NullLogger<InvestigationRunner>.Instance);
    }

    private static readonly System.Text.RegularExpressions.Regex StepIdInTranscript =
        new(@"\[step ([0-9a-fA-F-]{36})\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static Dictionary<string, object?> ConcludeArgs(
        IReadOnlyList<ChatMessage> conversation,
        string excerpt)
    {
        var stepId = StepIdInTranscript.Match(FakeChatClient.Transcript(conversation)).Groups[1].Value;

        var request = new ConcludeRequest
        {
            Summary = "The container cannot reach mongo and exits.",
            Confidence = 0.85,
            Findings =
            [
                new FindingDraft
                {
                    Category = "dependency",
                    Hypothesis = "api cannot reach mongo and exits non-zero on startup.",
                    Confidence = 0.85,
                    Primary = true,
                    Evidence = [new EvidenceDraft { StepId = stepId, Excerpt = excerpt }],
                },
            ],
        };

        return new Dictionary<string, object?>
        {
            ["request"] = JsonSerializer.SerializeToElement(request),
        };
    }
}
