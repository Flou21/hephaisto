using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The three-phase loop, driven end to end against a scripted provider. No network anywhere:
/// the fake sits at the innermost position, so the real <c>FunctionInvokingChatClient</c>,
/// the real budget guard and the real tool decorators are all exercised.
/// </summary>
public class InvestigationRunnerTests
{
    private const string LogLine = "FATAL: could not connect to mongo: connection refused";

    private static readonly LlmPricing FreePricing = new(new Dictionary<string, ModelPrice>());

    private static readonly Regex StepIdInTranscript = new(
        @"\[step ([0-9a-fA-F-]{36})\]", RegexOptions.CultureInvariant);

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

    private static AIFunction LogsTool(string result = LogLine) =>
        AIFunctionFactory.Create(
            (string pod) => result,
            "get_pod_logs",
            "Reads a pod's logs.");

    private static InvestigationRunner Runner(
        FakeChatClientFactory factory,
        IEnumerable<AIFunction>? tools = null,
        LlmOptions? llm = null,
        InvestigationOptions? investigation = null,
        IGlobalLlmBudget? globalBudget = null)
    {
        var clock = new TestClock();

        var grafana = new GrafanaMcpToolProvider(
            new TestOptionsMonitor<GrafanaOptions>(new GrafanaOptions()),
            clock,
            NullLoggerFactory.Instance);

        return new InvestigationRunner(
            factory,
            new PromptComposer(Options.Create(new EnvironmentCardOptions())),
            tools ?? [LogsTool()],
            grafana,
            globalBudget ?? new NullGlobalLlmBudget(),
            new Hephaisto.Agent.Pipeline.InvestigationTracker(clock),
            clock,
            new TestOptionsMonitor<LlmOptions>(llm ?? new LlmOptions()),
            new TestOptionsMonitor<InvestigationOptions>(investigation ?? new InvestigationOptions()),
            NullLogger<InvestigationRunner>.Instance);
    }

    /// <summary>
    /// Builds the arguments the model would send to <c>conclude</c>, quoting a step id it read
    /// out of a tool result - exactly as the real model does.
    /// </summary>
    private static Dictionary<string, object?> ConcludeArgs(
        IReadOnlyList<ChatMessage> conversation,
        string excerpt,
        string? forcedStepId = null)
    {
        var stepId = forcedStepId
            ?? StepIdInTranscript.Match(FakeChatClient.Transcript(conversation)).Groups[1].Value;

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

    private static string PlanJson(bool noAction, params string[] findingIds) =>
        JsonSerializer.Serialize(new ActionPlanDraft
        {
            Summary = "Restart the deployment.",
            NoActionRequired = noAction,
            Actions = noAction
                ? []
                :
                [
                    new ActionDraft
                    {
                        Type = ActionType.RolloutRestart,
                        Namespace = "hephaisto-chaos",
                        Kind = "Deployment",
                        Name = "api",
                        PredictedEffect = "Pods stay Ready for five minutes.",
                        RollbackJson = "{\"undo\":\"none-needed\"}",
                        EvidenceFindingIds = [.. findingIds],
                        Risk = RiskTier.Low,
                    },
                ],
        });

    // ------------------------------------------------------------------

    /// <summary>
    /// Phase 2 constrains the plan's shape with a real schema wherever the provider allows it.
    /// </summary>
    [Fact]
    public async Task Planning_constrains_the_shape_with_a_schema_by_default()
    {
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, ConcludingClient(), planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Plan.Should().NotBeNull();

        var format = planning.ReceivedOptions.Should().ContainSingle().Subject!.ResponseFormat;
        format.Should().BeOfType<ChatResponseFormatJson>()
            .Which.Schema.Should().NotBeNull("the default path enforces the shape rather than asking for it");

        // The schema is the wire contract here, so it has no business also being in the prompt.
        LastUserText(planning).Should().NotContain("no_action_required");
    }

    /// <summary>
    /// A provider that cannot enforce a schema is told the shape instead, and still plans.
    /// </summary>
    /// <remarks>
    /// DeepSeek answers <c>400 "This response_format type is unavailable now"</c> to a
    /// json_schema response format. Measured on 2026-08-31: every planning call failed, across
    /// all nine cassettes, while the investigation phase kept working - so the agent diagnosed
    /// correctly and proposed nothing, which reads as a cautious agent rather than a broken
    /// one. That is the failure this mode exists to remove.
    /// </remarks>
    [Fact]
    public async Task Planning_carries_the_schema_in_the_prompt_when_the_provider_cannot_enforce_one()
    {
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));
        var llm = new LlmOptions { PlanningStructuredOutput = StructuredOutputMode.JsonObject };

        var outcome = await Runner(
                new FakeChatClientFactory(FreePricing, ConcludingClient(), planning), llm: llm)
            .RunAsync(NewIncident(), CancellationToken.None);

        var format = planning.ReceivedOptions.Should().ContainSingle().Subject!.ResponseFormat;
        format.Should().BeOfType<ChatResponseFormatJson>()
            .Which.Schema.Should().BeNull("json_object mode carries no schema on the wire");

        // Derived from the same CLR type the reply is parsed against, so the two cannot drift.
        LastUserText(planning).Should().Contain("no_action_required").And.Contain("evidence_finding_ids");

        outcome.Plan.Should().NotBeNull("a plan must still be produced when the shape is only requested");
        outcome.Plan!.NoActionRequired.Should().BeTrue();
    }

    private static FakeChatClient ConcludingClient() => new(
        (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
        (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
        (_, _) => FakeChatClient.Text("Concluded."));

    private static string LastUserText(FakeChatClient client) =>
        string.Concat(
            client.Received[^1]
                .Where(m => m.Role == ChatRole.User)
                .Select(m => m.Text));

    [Fact]
    public async Task Concludes_grounds_the_evidence_and_plans()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);
        outcome.Investigation.Findings.Should().ContainSingle();
        outcome.Investigation.Findings[0].Evidence.Should().ContainSingle()
            .Which.Excerpt.Should().Be(LogLine);

        outcome.Rejections.Should().BeEmpty();
        outcome.Escalation.Should().BeNull();
        outcome.Plan.Should().NotBeNull();
        outcome.Plan!.NoActionRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Records_both_turns_and_tool_calls_in_the_order_they_happened()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        var steps = outcome.Investigation.Steps;

        // A turn, then the tool call it caused, then the next turn. Two independent ordinal
        // counters would produce a plausible-looking sequence that interleaves wrongly.
        steps.Select(s => s.Ordinal).Should().BeInAscendingOrder();
        steps.Select(s => s.Kind).Should().StartWith(
            [StepKind.LlmTurn, StepKind.ToolCall, StepKind.LlmTurn, StepKind.ToolCall]);

        steps.Should().Contain(s => s.ToolName == "get_pod_logs" && s.ToolServer == "kubernetes");
        steps.Should().Contain(s => s.ToolName == "conclude" && s.ToolServer == "internal");

        outcome.Investigation.ToolCallsUsed.Should().Be(2);
    }

    [Fact]
    public async Task A_budget_hit_escalates_and_never_resolves()
    {
        // Running out of steps is the agent being interrupted mid-thought, not a green light.
        // Whatever it had reached at that moment was not what it considered its answer.
        var investigation = new FakeChatClient(
            (i, _) => FakeChatClient.CallsTool(
                $"c{i}", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }));

        var llm = new LlmOptions();
        llm.Investigation.MaxSteps = 3;

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation), llm: llm)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.StepBudgetExhausted);
        outcome.Escalation.Should().Be(EscalationReason.BudgetExhausted);
        outcome.Plan.Should().BeNull();

        // Three, plus the one step reserved for a conclusion. This model never concludes -
        // it asks for the same tool forever - so the reserve is spent and the run still ends
        // on its budget, which is the point: the reserve is a chance to answer, not a way to
        // keep going.
        outcome.Investigation.StepsUsed.Should().Be(4);
    }

    [Fact]
    public async Task A_model_that_concludes_on_the_reserved_step_ends_concluded()
    {
        // The case the reserve exists for. Before it, a run that spent every step asking had
        // none left to answer with: on the dev cluster every surviving investigation ended
        // StepBudgetExhausted at exactly 12 of 12 steps and not one produced a finding.
        var turn = 0;

        var investigation = new FakeChatClient((i, _) =>
            ++turn <= 3
                ? FakeChatClient.CallsTool(
                    $"c{i}", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" })
                : FakeChatClient.CallsTool(
                    $"c{i}",
                    "conclude",
                    new Dictionary<string, object?>
                    {
                        ["request"] = new Dictionary<string, object?>
                        {
                            ["summary"] = "The container is being OOM killed.",
                            ["confidence"] = 0.8,
                        },
                    }));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));

        var llm = new LlmOptions();
        llm.Investigation.MaxSteps = 3;

        var outcome = await Runner(
                new FakeChatClientFactory(FreePricing, investigation, planning), llm: llm)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);
    }

    [Fact]
    public async Task The_tool_call_budget_stops_the_run_with_its_own_reason()
    {
        var investigation = new FakeChatClient(
            (i, _) => FakeChatClient.CallsTool(
                $"c{i}", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }));

        var llm = new LlmOptions();
        llm.Investigation.MaxToolCalls = 2;

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation), llm: llm)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.ToolCallBudgetExhausted);
        outcome.Escalation.Should().Be(EscalationReason.BudgetExhausted);
    }

    [Fact]
    public async Task Two_consecutive_turns_with_no_tool_call_stalls()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.Text("Let me think about this some more."));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Stalled);
        outcome.Escalation.Should().Be(EscalationReason.NoPlanProduced);
        outcome.Plan.Should().BeNull();

        // One nudge, then it gives up. Nudging again costs a full turn's tokens to re-read
        // the whole transcript and has already failed once.
        investigation.Calls.Should().Be(2);
    }

    [Fact]
    public async Task One_idle_turn_is_nudged_rather_than_abandoned()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.Text("Thinking."),
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);

        // The nudge is a user message, so the second call sees one more than the first.
        investigation.Received[1].Should().Contain(m => m.Role == ChatRole.User && m.Text.Contains("conclude"));
    }

    [Fact]
    public async Task An_invented_citation_takes_the_finding_with_it_and_escalates()
    {
        // The model reports a cause it cannot show. This is the case the whole grounding
        // mechanism exists for, and it is worth a human's attention rather than a quiet
        // "no findings".
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool(
                "c2",
                "conclude",
                ConcludeArgs(conversation, "FATAL: the database rejected our credentials")),
            (_, _) => FakeChatClient.Text("Concluded."));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);
        outcome.Investigation.Findings.Should().BeEmpty();
        outcome.Escalation.Should().Be(EscalationReason.GroundingRejected);
        outcome.Plan.Should().BeNull();

        outcome.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.ExcerptNotFound);
        outcome.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.FindingWithoutEvidence);
    }

    [Fact]
    public async Task A_citation_naming_another_investigations_step_fails()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool(
                "c2",
                "conclude",
                ConcludeArgs(conversation, LogLine, forcedStepId: Guid.CreateVersion7().ToString())),
            (_, _) => FakeChatClient.Text("Concluded."));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.Findings.Should().BeEmpty();
        outcome.Rejections.Should().Contain(r => r.Reason == GroundingRejectionReason.UnknownStep);
        outcome.Escalation.Should().Be(EscalationReason.GroundingRejected);
    }

    [Fact]
    public async Task A_plan_citing_a_finding_that_does_not_exist_is_rejected()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient(
            (_, _) => FakeChatClient.Text(PlanJson(noAction: false, Guid.CreateVersion7().ToString())));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        // Whole-plan rejection, not per-action: an action justified by an invented finding
        // says this investigation's reasoning is not trustworthy.
        outcome.Draft.Should().NotBeNull();
        outcome.Plan.Should().BeNull();
        outcome.Escalation.Should().Be(EscalationReason.GroundingRejected);
        outcome.Rejections.Should().Contain(
            r => r.Reason == GroundingRejectionReason.ActionCitesDroppedFinding);
    }

    [Fact]
    public async Task A_plan_citing_a_grounded_finding_maps_to_a_domain_plan()
    {
        FakeChatClient? planning = null;
        Guid findingId = default;

        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        // The planning script reads the finding id out of the prompt it is given, the way the
        // model does - the ids do not exist until grounding has run.
        planning = new FakeChatClient((_, conversation) =>
        {
            findingId = Guid.Parse(Regex.Match(
                FakeChatClient.Transcript(conversation),
                @"id: `([0-9a-fA-F-]{36})`").Groups[1].Value);

            return FakeChatClient.Text(PlanJson(noAction: false, findingId.ToString()));
        });

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Plan.Should().NotBeNull();

        var action = outcome.Plan!.Actions.Should().ContainSingle().Subject;

        action.Type.Should().Be(ActionType.RolloutRestart);
        action.EvidenceFindingIds.Should().ContainSingle().Which.Should().Be(findingId);
        action.RollbackSpec.Should().NotBeNull();

        // Left at their defaults on purpose: a default-deny policy engine whose input arrives
        // pre-approved is not default-deny.
        action.State.Should().Be(ActionState.Proposed);
        action.Decision.Should().Be(PolicyDecision.Deny);
    }

    [Fact]
    public async Task The_planning_call_is_given_no_tools()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) => FakeChatClient.CallsTool("c2", "conclude", ConcludeArgs(conversation, LogLine)),
            (_, _) => FakeChatClient.Text("Concluded."));

        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(PlanJson(noAction: true)));

        await Runner(new FakeChatClientFactory(FreePricing, investigation, planning))
            .RunAsync(NewIncident(), CancellationToken.None);

        // The phase that produces actions has no tools at all. This is the security property
        // the whole three-phase split exists for.
        planning.ReceivedOptions.Should().ContainSingle()
            .Which!.Tools.Should().BeNullOrEmpty();

        planning.ReceivedOptions[0]!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
    }

    [Fact]
    public async Task The_investigation_call_is_given_the_read_only_tools_plus_conclude()
    {
        var investigation = new FakeChatClient((_, _) => FakeChatClient.Text("Thinking."));

        await Runner(new FakeChatClientFactory(FreePricing, investigation))
            .RunAsync(NewIncident(), CancellationToken.None);

        var tools = investigation.ReceivedOptions[0]!.Tools!;

        tools.Select(t => t.Name).Should().BeEquivalentTo(["get_pod_logs", "conclude"]);

        // Every tool, without exception, is wrapped. A limit that holds for the tools we wrote
        // and not for the ones a remote MCP server exposes is not a limit.
        tools.Should().AllBeOfType<SafeToolDecorator>();
    }

    [Fact]
    public async Task A_provider_fault_is_recorded_rather_than_retried()
    {
        var investigation = new FakeChatClient((_, _) => FakeChatClient.Text("never"))
        {
            Throws = new HttpRequestException("503 from the provider"),
        };

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Faulted);
        outcome.Investigation.Error.Should().Contain("503");
        outcome.Escalation.Should().Be(EscalationReason.InvestigationFailed);

        investigation.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_global_budget_stops_the_run_before_a_single_token_is_spent()
    {
        var investigation = new FakeChatClient((_, _) => FakeChatClient.Text("should never be called"));

        var outcome = await Runner(
                new FakeChatClientFactory(FreePricing, investigation),
                globalBudget: new RefusingGlobalBudget())
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Escalation.Should().Be(EscalationReason.BudgetExhausted);
        outcome.Investigation.CostUsd.Should().Be(0m);
        investigation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task The_investigation_row_carries_the_trace_id_when_a_listener_is_recording()
    {
        // The join key between the database and Tempo. Null when nothing is sampling, which is
        // the case in a bare unit test - so this asserts the wiring under a real listener.
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Hephaisto",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)
                => System.Diagnostics.ActivitySamplingResult.AllData,
        };

        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var investigation = new FakeChatClient((_, _) => FakeChatClient.Text("Thinking."));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.TraceId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_fenced_plan_still_parses()
    {
        // Gemini honours the response schema, but a fenced block turns up occasionally -
        // usually when the model also decided to explain itself. Two lines to strip; an
        // entire investigation's work to lose by not stripping it.
        var draft = InvestigationRunner.Deserialise(
            "```json\n{\"summary\":\"nothing to do\",\"no_action_required\":true,\"actions\":[]}\n```");

        draft.Should().NotBeNull();
        draft!.NoActionRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I'm sorry, I can't help with that.")]
    public void An_unparseable_plan_yields_null_rather_than_throwing(string? text) =>
        InvestigationRunner.Deserialise(text).Should().BeNull();

    private sealed class RefusingGlobalBudget : IGlobalLlmBudget
    {
        public Task<GlobalBudgetVerdict> CheckAsync(Guid incidentId, CancellationToken ct) =>
            Task.FromResult(new GlobalBudgetVerdict(false, "$3.0012 this hour (max $3.00)"));

        public Task RecordAsync(
            Guid incidentId,
            long inputTokens,
            long outputTokens,
            decimal costUsd,
            CancellationToken ct) => Task.CompletedTask;

        public void Enlist(
            Guid incidentId,
            Guid? investigationId,
            long inputTokens,
            long outputTokens,
            decimal costUsd)
        {
        }
    }
}
