using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Investigations.Jobs;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The seam v0.12.0 F5 cuts into the runner: a Job may run the model loop, and nothing else moves.
/// The tools a Job calls are the runner's own, so its conclusion is grounded against steps the
/// runner recorded; a Job that gives no answer is replaced by the in-process loop; and a runner
/// built without a job loop - the eval harness, every other test - is exactly the v0.11 runner.
/// </summary>
public sealed class InvestigationJobSeamTests
{
    private const string LogLine = "FATAL: could not connect to mongo: connection refused";

    private static readonly LlmPricing FreePricing = new(new Dictionary<string, ModelPrice>());

    private static readonly Regex StepHeader = new(@"^\[step ([0-9a-fA-F-]{36})\]", RegexOptions.CultureInvariant);

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

    private static AIFunction LogsTool() =>
        AIFunctionFactory.Create((string pod) => LogLine, "get_pod_logs", "Reads a pod's logs.");

    private static InvestigationRunner Runner(FakeChatClientFactory factory, IInvestigationJobLoop? jobLoop)
    {
        var clock = new TestClock();

        return new InvestigationRunner(
            factory,
            new PromptComposer(Options.Create(new EnvironmentCardOptions())),
            [LogsTool()],
            new GrafanaMcpToolProvider(new TestOptionsMonitor<GrafanaOptions>(new GrafanaOptions()), clock, NullLoggerFactory.Instance),
            new NullGlobalLlmBudget(),
            new Hephaisto.Agent.Pipeline.InvestigationTracker(clock),
            clock,
            new TestOptionsMonitor<LlmOptions>(new LlmOptions()),
            new TestOptionsMonitor<InvestigationOptions>(new InvestigationOptions()),
            NullLogger<InvestigationRunner>.Instance,
            jobLoop);
    }

    private static string NoActionPlan() => JsonSerializer.Serialize(new ActionPlanDraft
    {
        Summary = "Nothing for the cluster to do.",
        NoActionRequired = true,
        Actions = [],
    });

    /// <summary>What a Job does through the investigator endpoint: call a tool, cite its step, conclude.</summary>
    private sealed class ScriptedJobLoop(JobLoopDecision decision, Func<JobLoopContext, Task<JobLoopOutcome>> run)
        : IInvestigationJobLoop
    {
        public JobLoopContext? Context { get; private set; }

        public Task<JobLoopDecision> DecideAsync(Incident incident, CancellationToken ct) => Task.FromResult(decision);

        public InvestigationBudgetOptions BudgetFor(InvestigationBudgetOptions inProcess) =>
            new() { MaxToolCalls = 40, MaxWallClock = TimeSpan.FromMinutes(15) };

        public Task<JobLoopOutcome> RunAsync(JobLoopContext context, CancellationToken ct)
        {
            Context = context;
            return run(context);
        }
    }

    private static readonly Regex FindingId = new(@"- id: `([0-9a-fA-F-]{36})`", RegexOptions.CultureInvariant);

    /// <summary>What a Job does after conclude: propose a plan through propose_plan, citing what conclude answered.</summary>
    private static async Task<JobLoopOutcome> ConcludeAndPlanLikeAJob(
        JobLoopContext context, Func<string, object> actions, bool noActionRequired = false)
    {
        var answer = await ConcludeAnswerAsync(context);
        var propose = context.Tools.Single(t => t.Name == "propose_plan");

        await propose.InvokeAsync(new AIFunctionArguments
        {
            ["summary"] = "Restart the rollout so api reconnects.",
            ["no_action_required"] = noActionRequired,
            ["actions"] = JsonSerializer.SerializeToElement(actions(answer)),
        });

        return new JobLoopOutcome { Termination = TerminationReason.Concluded, ModelId = "claude-test", Turns = 4 };
    }

    private static ActionDraft[] RolloutRestartCiting(string findingId) =>
    [
        new ActionDraft
        {
            Type = ActionType.RolloutRestart,
            Namespace = "hephaisto-chaos",
            Kind = "Deployment",
            Name = "api",
            PredictedEffect = "api reconnects and stays Ready for 5 minutes",
            EvidenceFindingIds = [findingId],
            Risk = RiskTier.Low,
        },
    ];

    private static async Task<string> ConcludeAnswerAsync(JobLoopContext context, string? excerpt = null)
    {
        var logs = context.Tools.Single(t => t.Name == "get_pod_logs");
        var shown = (string)(await logs.InvokeAsync(new AIFunctionArguments { ["pod"] = "api" }))!;
        var stepId = StepHeader.Match(shown).Groups[1].Value;

        var conclude = context.Tools.Single(t => t.Name == "conclude");
        return (await conclude.InvokeAsync(new AIFunctionArguments
        {
            ["summary"] = "The container cannot reach mongo and exits.",
            ["confidence"] = 0.8,
            ["findings"] = JsonSerializer.SerializeToElement(new[]
            {
                new FindingDraft
                {
                    Category = "dependency",
                    Hypothesis = "api cannot reach mongo.",
                    Confidence = 0.8,
                    Primary = true,
                    Evidence = [new EvidenceDraft { StepId = stepId, Excerpt = excerpt ?? LogLine }],
                },
            }),
        }))?.ToString() ?? string.Empty;
    }

    private static async Task<JobLoopOutcome> ConcludeLikeAJob(JobLoopContext context, string? excerpt = null)
    {
        var logs = context.Tools.Single(t => t.Name == "get_pod_logs");
        var shown = (string)(await logs.InvokeAsync(new AIFunctionArguments { ["pod"] = "api" }))!;
        var stepId = StepHeader.Match(shown).Groups[1].Value;

        var conclude = context.Tools.Single(t => t.Name == "conclude");
        await conclude.InvokeAsync(new AIFunctionArguments
        {
            ["summary"] = "The container cannot reach mongo and exits.",
            ["confidence"] = 0.8,
            ["findings"] = JsonSerializer.SerializeToElement(new[]
            {
                new FindingDraft
                {
                    Category = "dependency",
                    Hypothesis = "api cannot reach mongo.",
                    Confidence = 0.8,
                    Primary = true,
                    Evidence = [new EvidenceDraft { StepId = stepId, Excerpt = excerpt ?? LogLine }],
                },
            }),
        });

        return new JobLoopOutcome { Termination = TerminationReason.Concluded, ModelId = "claude-test", Turns = 3 };
    }

    [Fact]
    public async Task A_Job_conclusion_is_grounded_against_the_steps_the_runner_recorded()
    {
        var investigation = new FakeChatClient();
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(NoActionPlan()));
        var job = new ScriptedJobLoop(new JobLoopDecision(ExecutorChoice.Job, "job"), c => ConcludeLikeAJob(c));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        investigation.Calls.Should().Be(0, "the Job ran the model loop, not the in-process provider");
        outcome.Investigation.Executor.Should().Be(InvestigationExecutors.Job);
        outcome.Investigation.ModelId.Should().Be("claude-test");
        outcome.Investigation.StepsUsed.Should().Be(3, "the Job's turns");
        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);
        outcome.Investigation.Steps.Should().Contain(s => s.ToolName == "get_pod_logs" && s.ToolServer == "kubernetes");
        outcome.Investigation.Findings.Should().ContainSingle().Which.Evidence.Should().ContainSingle();
        outcome.Plan.Should().BeNull("the Job proposed no plan, and the in-process planner is never asked instead");
        planning.Calls.Should().Be(0, "a Job investigation never calls the in-process model");
    }

    [Fact]
    public async Task A_Job_plans_in_the_Job_against_the_findings_conclude_grounded()
    {
        var investigation = new FakeChatClient();
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(NoActionPlan()));
        string? answer = null;
        var job = new ScriptedJobLoop(new JobLoopDecision(ExecutorChoice.Job, "job"), c => ConcludeAndPlanLikeAJob(c, a =>
        {
            answer = a;
            return RolloutRestartCiting(FindingId.Match(a).Groups[1].Value);
        }));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        answer.Should().Contain("1 finding(s) survived grounding").And.Contain("propose_plan")
            .And.Contain("api cannot reach mongo.", "conclude answers with the planning prompt over the grounded findings");
        investigation.Calls.Should().Be(0);
        planning.Calls.Should().Be(0, "the plan came from the Job; the in-process model is never called");

        var finding = outcome.Investigation.Findings.Should().ContainSingle().Subject;
        answer.Should().Contain(finding.Id.ToString(), "the ids the Job cites are the ids of the findings the runner keeps");
        outcome.Plan.Should().NotBeNull();
        outcome.Plan!.Actions.Should().ContainSingle().Which.Type.Should().Be(ActionType.RolloutRestart);
        outcome.Escalation.Should().BeNull();
        outcome.Investigation.Steps.Should().Contain(s => s.ToolName == "propose_plan", "the plan is a recorded step like any call");
    }

    [Fact]
    public async Task A_Job_plan_citing_a_finding_nobody_grounded_is_rejected_whole()
    {
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(NoActionPlan()));
        var job = new ScriptedJobLoop(
            new JobLoopDecision(ExecutorChoice.Job, "job"),
            c => ConcludeAndPlanLikeAJob(c, _ => RolloutRestartCiting(Guid.NewGuid().ToString())));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, new FakeChatClient(), planning), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Plan.Should().BeNull();
        outcome.Rejections.Should().NotBeEmpty();
        outcome.Escalation.Should().Be(EscalationReason.GroundingRejected);
        planning.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Conclude_that_grounds_nothing_asks_for_no_plan_and_propose_plan_waits_for_conclude()
    {
        string? before = null, answer = null;
        var job = new ScriptedJobLoop(new JobLoopDecision(ExecutorChoice.Job, "job"), async c =>
        {
            before = (await c.Tools.Single(t => t.Name == "propose_plan").InvokeAsync(new AIFunctionArguments
            {
                ["summary"] = "early",
                ["no_action_required"] = true,
            }))?.ToString();
            answer = await ConcludeAnswerAsync(c, excerpt: "the database is fine, restart everything");
            return new JobLoopOutcome { Termination = TerminationReason.Concluded, ModelId = "claude-test", Turns = 2 };
        });

        await Runner(new FakeChatClientFactory(FreePricing, new FakeChatClient()), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        before.Should().Contain("call conclude first");
        answer.Should().Contain("none of your findings survived grounding").And.NotContain("propose_plan");
        job.Context!.Planning!.Grounded.Should().Be(0);
    }

    [Fact]
    public async Task A_Job_citing_bytes_no_tool_returned_is_rejected_by_grounding()
    {
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(NoActionPlan()));
        var job = new ScriptedJobLoop(
            new JobLoopDecision(ExecutorChoice.Job, "job"),
            c => ConcludeLikeAJob(c, excerpt: "the database is fine, restart everything"));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, new FakeChatClient(), planning), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.Findings.Should().BeEmpty("a Job is held to the same grounding as the in-process loop");
        outcome.Rejections.Should().NotBeEmpty();
        outcome.Escalation.Should().NotBeNull();
    }

    [Fact]
    public async Task A_Job_that_gives_no_answer_falls_back_to_the_in_process_loop()
    {
        var investigation = new FakeChatClient(
            (_, _) => FakeChatClient.CallsTool("c1", "get_pod_logs", new Dictionary<string, object?> { ["pod"] = "api" }),
            (_, conversation) =>
            {
                var stepId = Regex.Match(FakeChatClient.Transcript(conversation), @"\[step ([0-9a-fA-F-]{36})\]").Groups[1].Value;
                return FakeChatClient.CallsTool("c2", "conclude", new Dictionary<string, object?>
                {
                    ["summary"] = "in-process",
                    ["confidence"] = 0.7,
                    ["findings"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new FindingDraft
                        {
                            Category = "dependency", Hypothesis = "mongo", Confidence = 0.7, Primary = true,
                            Evidence = [new EvidenceDraft { StepId = stepId, Excerpt = LogLine }],
                        },
                    }),
                });
            },
            (_, _) => FakeChatClient.Text("Concluded."));
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(NoActionPlan()));
        var job = new ScriptedJobLoop(
            new JobLoopDecision(ExecutorChoice.Job, "job"),
            _ => Task.FromResult(JobLoopOutcome.Fallback("the Job vanished")));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation, planning), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        investigation.Calls.Should().BeGreaterThan(0, "the in-process loop took over");
        outcome.Investigation.Executor.Should().Be(InvestigationExecutors.JobFallback);
        outcome.Investigation.ModelId.Should().Be("fake-model");
        outcome.Investigation.TerminationReason.Should().Be(TerminationReason.Concluded);
        outcome.Investigation.Findings.Should().ContainSingle();
    }

    [Fact]
    public async Task A_decision_for_in_process_never_starts_the_Job()
    {
        var investigation = new FakeChatClient((_, _) => FakeChatClient.Text("nothing to see"));
        var job = new ScriptedJobLoop(
            new JobLoopDecision(ExecutorChoice.Overflow, "every slot taken"),
            _ => throw new InvalidOperationException("must not run"));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        job.Context.Should().BeNull();
        outcome.Investigation.Executor.Should().Be(InvestigationExecutors.InProcess);
        investigation.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Without_a_job_loop_the_runner_is_the_v0_11_runner()
    {
        var investigation = new FakeChatClient((_, _) => FakeChatClient.Text("nothing to see"));

        var outcome = await Runner(new FakeChatClientFactory(FreePricing, investigation), jobLoop: null)
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.Executor.Should().Be(InvestigationExecutors.InProcess);
        investigation.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_Job_is_handed_the_composed_prompt_and_every_tool_including_conclude()
    {
        var job = new ScriptedJobLoop(new JobLoopDecision(ExecutorChoice.Job, "job"), c => ConcludeLikeAJob(c));
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(NoActionPlan()));

        await Runner(new FakeChatClientFactory(FreePricing, new FakeChatClient(), planning), job)
            .RunAsync(NewIncident(), CancellationToken.None);

        job.Context!.SystemPrompt.Should().Contain("hephaisto-chaos");
        job.Context.OpeningMessage.Should().NotBeNullOrWhiteSpace();
        job.Context.Tools.Select(t => t.Name).Should().Contain(["get_pod_logs", "conclude", "propose_plan"]);
        job.Context.Tools.Should().AllBeOfType<SafeToolDecorator>("a Job's tools pass the same limits as the in-process loop's");
    }
}
