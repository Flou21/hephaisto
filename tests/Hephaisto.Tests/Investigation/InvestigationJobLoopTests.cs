using System.Text.Json;
using System.Text.RegularExpressions;
using k8s.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Investigations.Jobs;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The Job-backed model loop against a scripted launcher that plays the Job's part: it reads the
/// token out of the request ConfigMap and calls the run's real tools through the real session,
/// then prints the frame the driver would. What is asserted is everything Hephaisto decides.
/// </summary>
public sealed class InvestigationJobLoopTests
{
    private const string LogLine = "FATAL: could not connect to mongo: connection refused";

    private static readonly Regex StepHeader = new(@"^\[step ([0-9a-fA-F-]{36})\]", RegexOptions.CultureInvariant);

    private readonly TestClock clock = new();

    private readonly InvestigationJobOptions job = new()
    {
        Enabled = true,
        Executor = "job",
        EndpointUrl = "http://hephaisto.hephaisto.svc:8084/investigate",
        Model = "claude-opus-5-5",
        PollInterval = TimeSpan.FromMilliseconds(1),
        Deadline = TimeSpan.FromMinutes(10),
    };

    private readonly CodeFixOptions codeFix = new()
    {
        Image = "hephaisto/coder:dev",
        ContextRepositoryUrl = "http://coder-git.hephaisto-coder.svc/dev-context.git",
        EgressProxyUrl = "http://hephaisto-coder-egress.hephaisto-coder.svc:3128",
        NugetCacheClaim = "nuget",
    };

    private sealed class Switch(InvestigationExecutor effective) : IInvestigationExecutorSwitch
    {
        public InvestigationExecutor Effective { get; set; } = effective;

        public Task<InvestigationExecutorResolution> ResolveAsync(CancellationToken ct) =>
            Task.FromResult(new InvestigationExecutorResolution
            {
                Effective = Effective,
                Declared = Effective,
                DecidedBy = "test",
                Arms = [],
                Enabled = true,
                EmergencyStop = false,
                RunawayLatched = false,
            });
    }

    /// <summary>A launcher whose "Job" runs the script it is given on its first observation.</summary>
    private sealed class ScriptedLauncher(InvestigationJobSessions sessions) : ICodeFixJobLauncher
    {
        public V1Job? Spec { get; private set; }

        public InvestigateRequest? Request { get; private set; }

        public Func<InvestigationJobSession, Task>? Job { get; set; }

        public Queue<CodeFixJobPhase> Phases { get; } = new();

        public Func<InvestigateRequest, string?> Log { get; set; } = _ => null;

        public Exception? RefuseWith { get; set; }

        public List<string> Deleted { get; } = [];

        public bool IsAvailable => true;

        public Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> LaunchAsync(V1Job spec, Func<V1Job, V1ConfigMap> request, CancellationToken ct)
        {
            if (RefuseWith is not null)
                throw RefuseWith;

            Spec = spec;
            spec.Metadata.Uid = Guid.NewGuid().ToString();
            var cm = request(spec);
            Request = JsonSerializer.Deserialize<InvestigateRequest>(cm.Data["request.json"], CodeFixContract.Json);
            cm.Metadata.OwnerReferences.Should().ContainSingle().Which.Uid.Should().Be(spec.Metadata.Uid);

            return Task.FromResult(spec.Metadata.Name);
        }

        public async Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct)
        {
            if (Job is { } run)
            {
                Job = null;
                sessions.TryGet(Request!.Endpoint.Token, out var session).Should().BeTrue("the token in the request opens the run");
                await run(session);
            }

            var phase = Phases.Count > 0 ? Phases.Dequeue() : CodeFixJobPhase.Succeeded;
            return new CodeFixJobObservation(phase, null);
        }

        public Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct) =>
            Task.FromResult(Log(Request!));

        public Task DeleteAsync(string jobName, CancellationToken ct)
        {
            Deleted.Add(jobName);
            return Task.CompletedTask;
        }
    }

    private readonly StubImages images = new();

    private sealed class StubImages : IWorkloadImageReader
    {
        public string? Image { get; set; } = "ghcr.io/flou21/hephaisto-fixture-dotnet:c15-583b1e5b75add2ba341319eef1ae438e8346c4b0";

        public Task<(string? Image, string? Revision)> ReadAsync(TargetRef target, CancellationToken ct) =>
            Task.FromResult<(string?, string?)>((Image, "1"));

        public Task<TargetRef> ResolveWorkloadAsync(TargetRef target, CancellationToken ct) => Task.FromResult(target);
    }

    private (KubernetesInvestigationJobLoop Loop, ScriptedLauncher Launcher, InvestigationJobSessions Sessions, Switch Switch) Build(
        InvestigationExecutor effective = InvestigationExecutor.Job, string agentCluster = "")
    {
        var sessions = new InvestigationJobSessions(clock);
        var launcher = new ScriptedLauncher(sessions);
        var executor = new Switch(effective);

        var loop = new KubernetesInvestigationJobLoop(
            executor,
            sessions,
            launcher,
            images,
            new PromptComposer(Options.Create(new EnvironmentCardOptions { ClusterName = agentCluster })),
            new Hephaisto.Agent.Pipeline.InvestigationTracker(clock),
            new TestOptionsMonitor<InvestigationJobOptions>(job),
            new TestOptionsMonitor<CodeFixOptions>(codeFix),
            clock,
            NullLogger<KubernetesInvestigationJobLoop>.Instance);

        return (loop, launcher, sessions, executor);
    }

    private static Incident NewIncident(string? cluster = null) => new()
    {
        Title = "shop-api is crash-looping",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        OpenedAt = DateTimeOffset.UnixEpoch,
        LastSignalAt = DateTimeOffset.UnixEpoch,
        Target = new TargetRef
        {
            Cluster = cluster ?? string.Empty,
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "shop-api-7d9f8-xk2p1",
            OwnerKind = "Deployment",
            OwnerName = "shop-api",
        },
    };

    private JobLoopContext Context(InvestigationRecorder recorder, InvestigationRunner.ConclusionHolder holder)
    {
        var budget = new InvestigationBudget(new InvestigationBudgetOptions(), clock);
        var logs = AIFunctionFactory.Create((string @namespace, string name) => LogLine, "get_pod_logs", "Reads a pod's logs.");

        return new JobLoopContext
        {
            Incident = NewIncident(),
            InvestigationId = recorder.InvestigationId,
            SystemPrompt = "You are Hephaisto. The incident is in hephaisto-chaos.",
            OpeningMessage = "Investigate.",
            Tools =
            [
                new SafeToolDecorator(logs, "kubernetes", new SafeToolOptions(), budget, recorder),
                new SafeToolDecorator(InvestigationRunner.CreateConcludeTool(holder), "internal", new SafeToolOptions(), null, recorder),
            ],
            Recorder = recorder,
            Conclusion = holder,
        };
    }

    private static async Task Investigate(InvestigationJobSession session)
    {
        var shown = (string)(await session.Tools["get_pod_logs"].InvokeAsync(
            new AIFunctionArguments { ["namespace"] = "hephaisto-chaos", ["name"] = "shop-api-1" }))!;

        await session.Tools["conclude"].InvokeAsync(new AIFunctionArguments
        {
            ["summary"] = "mongo is unreachable",
            ["confidence"] = 0.8,
            ["findings"] = JsonSerializer.SerializeToElement(new[]
            {
                new FindingDraft
                {
                    Category = "dependency", Hypothesis = "mongo", Confidence = 0.8, Primary = true,
                    Evidence = [new EvidenceDraft { StepId = StepHeader.Match(shown).Groups[1].Value, Excerpt = LogLine }],
                },
            }),
        });
    }

    private static string Frame(InvestigateRequest request, string outcome, string billing = "subscription", string? error = null) =>
        CodeFixResultParser.Frame(JsonSerializer.Serialize(new InvestigateResult
        {
            AttemptId = request.AttemptId,
            Outcome = outcome,
            CostUsd = 0.42m,
            Billing = billing,
            InputTokens = 48_000,
            OutputTokens = 2_300,
            Turns = 9,
            Model = "claude-opus-5-5",
            SessionId = null,
            ContextSha = null,
            Source = null,
            CodeRefs = [],
            Error = error,
            DeniedToolCalls = [],
        }, CodeFixContract.Json));

    private InvestigationRecorder NewRecorder() => new(Guid.CreateVersion7(), clock, TimeSpan.FromDays(30));

    [Fact]
    public async Task A_Job_that_concludes_through_the_endpoint_is_a_concluded_run()
    {
        var (loop, launcher, sessions, _) = Build();
        var recorder = NewRecorder();
        var holder = new InvestigationRunner.ConclusionHolder();
        launcher.Phases.Enqueue(CodeFixJobPhase.Running);
        launcher.Job = Investigate;
        launcher.Log = r => Frame(r, "concluded");

        var outcome = await loop.RunAsync(Context(recorder, holder), CancellationToken.None);

        outcome.FellBack.Should().BeFalse();
        outcome.Termination.Should().Be(TerminationReason.Concluded);
        outcome.ModelId.Should().Be("claude-opus-5-5");
        outcome.Turns.Should().Be(9);
        holder.Value.Should().NotBeNull();
        recorder.Steps.Should().Contain(s => s.ToolName == "get_pod_logs");
        sessions.ActiveCount.Should().Be(0, "the session closes with the run");
    }

    [Fact]
    public async Task A_subscription_run_is_recorded_at_no_charge_and_says_what_it_would_have_cost()
    {
        var (loop, launcher, _, _) = Build();
        var recorder = NewRecorder();
        launcher.Job = Investigate;
        launcher.Log = r => Frame(r, "concluded");

        await loop.RunAsync(Context(recorder, new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        var turn = recorder.Steps.Should().ContainSingle(s => s.Kind == StepKind.LlmTurn).Subject;
        turn.CostUsd.Should().Be(0m, "a subscription is not billed per token; charging it would trip the API spend caps");
        turn.InputTokens.Should().Be(48_000);
        turn.ResultDigest.Should().Contain("notional $0.4200");
    }

    [Fact]
    public async Task An_api_key_run_is_charged_what_it_cost()
    {
        var (loop, launcher, _, _) = Build();
        var recorder = NewRecorder();
        launcher.Job = Investigate;
        launcher.Log = r => Frame(r, "concluded", billing: "api");

        await loop.RunAsync(Context(recorder, new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        recorder.Steps.Single(s => s.Kind == StepKind.LlmTurn).CostUsd.Should().Be(0.42m);
    }

    [Theory]
    [InlineData("rate_limited")]
    [InlineData("no_credential")]
    [InlineData("failed")]
    public async Task A_Job_that_could_not_investigate_falls_back(string outcome)
    {
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, outcome, error: "usage limit reached");

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain(outcome);
    }

    [Theory]
    [InlineData("no_conclusion", TerminationReason.Stalled)]
    [InlineData("max_turns", TerminationReason.StepBudgetExhausted)]
    [InlineData("budget_exhausted", TerminationReason.CostBudgetExhausted)]
    public async Task A_Job_that_ran_and_did_not_conclude_ends_as_that(string outcome, TerminationReason expected)
    {
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, outcome);

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeFalse("it had its chance; running it again in-process would double the spend for the same evidence");
        result.Termination.Should().Be(expected);
    }

    [Fact]
    public async Task A_refused_launch_falls_back()
    {
        var (loop, launcher, sessions, _) = Build();
        launcher.RefuseWith = new CodeFixLaunchRefusedException("forbidden");

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("forbidden");
        sessions.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task A_Job_that_vanishes_before_concluding_falls_back()
    {
        var (loop, launcher, _, _) = Build();
        launcher.Phases.Enqueue(CodeFixJobPhase.Running);
        launcher.Phases.Enqueue(CodeFixJobPhase.Missing);

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("disappeared");
    }

    [Fact]
    public async Task A_forged_or_missing_frame_after_a_conclude_costs_the_bookkeeping_not_the_diagnosis()
    {
        var (loop, launcher, _, _) = Build();
        launcher.Job = Investigate;
        launcher.Log = _ => "---HEPHAISTO-RESULT-BEGIN sha256=" + new string('0', 64) + " bytes=2---\n{}\n---HEPHAISTO-RESULT-END---\n";

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeFalse();
        result.Termination.Should().Be(TerminationReason.Concluded);
        result.Error.Should().Contain("could not be read");
    }

    [Fact]
    public async Task A_Job_past_its_deadline_is_deleted_and_falls_back()
    {
        var (loop, launcher, _, _) = Build();
        for (var i = 0; i < 3; i++)
            launcher.Phases.Enqueue(CodeFixJobPhase.Running);
        launcher.Job = _ =>
        {
            clock.Advance(TimeSpan.FromMinutes(12));
            return Task.CompletedTask;
        };

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("deadline");
        launcher.Deleted.Should().ContainSingle();
    }

    [Fact]
    public async Task With_the_fallback_off_a_failure_is_the_termination()
    {
        job.FallbackToInProcess = false;
        var (loop, launcher, _, _) = Build();
        launcher.RefuseWith = new CodeFixLaunchRefusedException("forbidden");

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeFalse();
        result.Termination.Should().Be(TerminationReason.Faulted);
        result.Error.Should().Contain("forbidden");
    }

    [Fact]
    public async Task Missing_configuration_falls_back_without_starting_anything()
    {
        codeFix.ContextRepositoryUrl = string.Empty;
        var (loop, launcher, _, _) = Build();

        var result = await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("CodeFix:ContextRepositoryUrl");
        launcher.Spec.Should().BeNull();
    }

    [Fact]
    public async Task The_Job_is_an_investigator_with_only_the_models_credentials_and_a_direct_route_to_the_agent()
    {
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, "no_conclusion");

        await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        var spec = launcher.Spec!;
        var pod = spec.Spec.Template.Spec;
        var env = pod.Containers[0].Env;

        spec.Metadata.Name.Should().StartWith("investigate-");
        spec.Spec.Template.Metadata.Labels["app.kubernetes.io/name"].Should().Be("hephaisto-investigator");
        pod.AutomountServiceAccountToken.Should().BeFalse();
        pod.Containers[0].SecurityContext.ReadOnlyRootFilesystem.Should().BeTrue();
        spec.Spec.BackoffLimit.Should().Be(0);
        spec.Spec.ActiveDeadlineSeconds.Should().Be(600);

        env.Where(e => e.ValueFrom?.SecretKeyRef is not null).Select(e => e.Name)
            .Should().BeEquivalentTo(["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"], "no GitHub or NuGet token without source access");
        env.Should().NotContain(e => e.Value != null && e.Name.Contains("TOKEN"));
        env.Single(e => e.Name == "NO_PROXY").Value.Should().Contain("hephaisto.hephaisto.svc");
        env.Single(e => e.Name == "no_proxy").Value.Should().Contain("hephaisto.hephaisto.svc");
        env.Single(e => e.Name == "CODEFIX_MODEL").Value.Should().Be("claude-opus-5-5");
        pod.Volumes.Should().NotContain(v => v.PersistentVolumeClaim != null, "an investigator never builds");
    }

    [Fact]
    public async Task A_scripted_investigator_is_handed_no_model_credential()
    {
        job.Sdk = "fake";
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, "no_conclusion", billing: "fake");

        await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        launcher.Spec!.Spec.Template.Spec.Containers[0].Env
            .Should().NotContain(e => e.ValueFrom != null && e.ValueFrom.SecretKeyRef != null,
                "the runner refuses fake mode beside a real credential, and a $0 run must not become a paid one");
    }

    [Fact]
    public async Task The_request_carries_the_composed_prompt_the_endpoint_and_a_run_scoped_token()
    {
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, "no_conclusion");
        var recorder = NewRecorder();

        await loop.RunAsync(Context(recorder, new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        var request = launcher.Request!;
        request.Phase.Should().Be("investigate");
        request.InvestigationId.Should().Be(recorder.InvestigationId);
        request.SystemPrompt.Should().Contain("hephaisto-chaos");
        request.Endpoint.Url.Should().Be(job.EndpointUrl);
        request.Endpoint.Token.Length.Should().BeGreaterThanOrEqualTo(32);
        request.Incident.Target.Workload.Should().Be("hephaisto-chaos/Deployment/shop-api");
        request.Budget.MaxTurns.Should().Be(job.MaxTurns);
        request.Source.Should().BeNull();
    }

    [Fact]
    public async Task With_source_access_a_mapped_workload_is_cloned_at_its_running_image()
    {
        job.Source.Enabled = true;
        codeFix.Repositories = [new() { Workload = "hephaisto-chaos/Deployment/shop-api", Url = "https://github.com/Flou21/hephaisto-fixture-dotnet", DefaultBranch = "fixture/c15-null-deref" }];
        codeFix.AllowedRepositoryHosts = ["github.com"];
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, "no_conclusion");

        await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        var source = launcher.Request!.Source.Should().NotBeNull().And.Subject.As<InvestigateSource>();
        source.Url.Should().Be("https://github.com/Flou21/hephaisto-fixture-dotnet");
        source.DefaultBranch.Should().Be("fixture/c15-null-deref");
        source.Image.Should().Contain("583b1e5b");
        launcher.Spec!.Spec.Template.Spec.Containers[0].Env.Should().Contain(e => e.Name == "GITHUB_TOKEN",
            "the driver clones with it; the agent inside never sees it");
    }

    [Theory]
    [InlineData(false, "github.com")]
    [InlineData(true, "gitlab.com")]
    public async Task Without_access_or_an_allowed_host_there_is_no_source_and_no_GitHub_token(bool mapped, string allowedHost)
    {
        job.Source.Enabled = true;
        codeFix.Repositories = mapped
            ? [new() { Workload = "hephaisto-chaos/Deployment/shop-api", Url = "https://github.com/Flou21/hephaisto-fixture-dotnet" }]
            : [];
        codeFix.AllowedRepositoryHosts = [allowedHost];
        var (loop, launcher, _, _) = Build();
        launcher.Log = r => Frame(r, "no_conclusion");

        await loop.RunAsync(Context(NewRecorder(), new InvestigationRunner.ConclusionHolder()), CancellationToken.None);

        launcher.Request!.Source.Should().BeNull("no source is never a failed investigation");
        launcher.Spec!.Spec.Template.Spec.Containers[0].Env.Should().NotContain(e => e.Name == "GITHUB_TOKEN");
    }

    [Fact]
    public async Task Deciding_follows_the_policy()
    {
        var (loop, _, sessions, executor) = Build();

        (await loop.DecideAsync(NewIncident(), CancellationToken.None)).Choice.Should().Be(ExecutorChoice.Job);

        executor.Effective = InvestigationExecutor.InProcess;
        (await loop.DecideAsync(NewIncident(), CancellationToken.None)).Choice.Should().Be(ExecutorChoice.InProcessByMode);

        executor.Effective = InvestigationExecutor.Job;
        sessions.Open(new InvestigationJobSession
        {
            InvestigationId = Guid.NewGuid(),
            IncidentId = Guid.NewGuid(),
            Tools = new Dictionary<string, AIFunction>(),
            Conclusion = new InvestigationRunner.ConclusionHolder(),
            ExpiresAt = clock.UtcNow + TimeSpan.FromMinutes(5),
        });
        (await loop.DecideAsync(NewIncident(), CancellationToken.None)).Choice.Should().Be(ExecutorChoice.Overflow);
    }

    [Fact]
    public async Task Another_clusters_incident_stays_in_process()
    {
        var (loop, _, _, _) = Build(agentCluster: "studio");

        (await loop.DecideAsync(NewIncident(cluster: "eu-prod"), CancellationToken.None)).Choice
            .Should().Be(ExecutorChoice.ForeignCluster);
    }

    [Theory]
    [InlineData("not configured: CodeFix:Image is not set", "not_configured")]
    [InlineData("the Job was not started: forbidden", "launch_refused")]
    [InlineData("the Job disappeared before it concluded", "vanished")]
    [InlineData("the Job gave no answer within its 8-minute deadline", "deadline")]
    [InlineData("the executor was switched mid-run (...)", "switched")]
    [InlineData("the Job gave no readable answer: sha mismatch", "unreadable")]
    [InlineData("the Job ended rate_limited: usage limit", "rate_limited")]
    [InlineData("the Job ended no_credential", "no_credential")]
    [InlineData("the Job ended failed: endpoint_unauthorized", "failed")]
    public void A_fallback_reason_becomes_a_closed_label(string reason, string kind)
    {
        // A metric label from free text is a cardinality leak waiting for its first long error.
        KubernetesInvestigationJobLoop.FallbackKind(reason).Should().Be(kind);
    }

    [Fact]
    public void The_Jobs_tools_get_a_turn_each_and_the_Jobs_deadline()
    {
        var (loop, _, _, _) = Build();

        var budget = loop.BudgetFor(new InvestigationBudgetOptions { MaxToolCalls = 20, MaxWallClock = TimeSpan.FromMinutes(10) });

        budget.MaxToolCalls.Should().Be(job.MaxTurns);
        budget.MaxWallClock.Should().Be(job.Deadline);
    }
}
