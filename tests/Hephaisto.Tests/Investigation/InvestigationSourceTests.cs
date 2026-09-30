using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Investigations.Jobs;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// Source access (v0.12.0 F5, part 8): the investigator may point at file and line in the running
/// revision, and those pointers ride along - on the finding, into a code fix - without ever
/// becoming evidence.
/// </summary>
public sealed class InvestigationSourceTests
{
    private const string LogLine = "Unhandled exception. System.NullReferenceException at Endpoints.cs:line 17";

    private static readonly LlmPricing FreePricing = new(new Dictionary<string, ModelPrice>());

    private static readonly Regex StepHeader = new(@"^\[step ([0-9a-fA-F-]{36})\]", RegexOptions.CultureInvariant);

    [Fact]
    public void The_Jobs_conclude_offers_code_refs_and_the_in_process_one_does_not()
    {
        var job = InvestigationRunner.CreateJobConcludeTool(new InvestigationRunner.ConclusionHolder());
        var inProcess = InvestigationRunner.CreateConcludeTool(new InvestigationRunner.ConclusionHolder());

        job.JsonSchema.GetRawText().Should().Contain("code_refs");
        inProcess.JsonSchema.GetRawText().Should().NotContain("code_refs",
            "the in-process schema is part of every cassette's fingerprint and must not move");
        job.Name.Should().Be("conclude");
    }

    [Fact]
    public async Task The_Jobs_conclude_binds_with_code_refs_present()
    {
        var holder = new InvestigationRunner.ConclusionHolder();

        await InvestigationRunner.CreateJobConcludeTool(holder).InvokeAsync(new AIFunctionArguments
        {
            ["summary"] = "null endpoints",
            ["confidence"] = 0.8,
            ["findings"] = JsonSerializer.SerializeToElement(new[] { new FindingDraft { Category = "application", Hypothesis = "h", Primary = true } }),
            ["code_refs"] = JsonSerializer.SerializeToElement(new[] { new { finding = 0, path = "src/A.cs", line = 17 } }),
        }, TestContext.Current.CancellationToken);

        holder.Value.Should().NotBeNull();
        holder.Value!.Findings.Should().ContainSingle();
    }

    private sealed class ConcludingJob(IReadOnlyList<InvestigateCodeRef> refs, bool groundIt = true) : IInvestigationJobLoop
    {
        public Task<JobLoopDecision> DecideAsync(Incident incident, CancellationToken ct) =>
            Task.FromResult(new JobLoopDecision(ExecutorChoice.Job, "job"));

        public InvestigationBudgetOptions BudgetFor(InvestigationBudgetOptions inProcess) => new();

        public async Task<JobLoopOutcome> RunAsync(JobLoopContext context, CancellationToken ct)
        {
            var shown = (string)(await context.Tools.Single(t => t.Name == "get_pod_logs")
                .InvokeAsync(new AIFunctionArguments { ["pod"] = "shop-api" }))!;

            await context.Tools.Single(t => t.Name == "conclude").InvokeAsync(new AIFunctionArguments
            {
                ["summary"] = "null endpoints",
                ["confidence"] = 0.85,
                ["findings"] = JsonSerializer.SerializeToElement(new[]
                {
                    new FindingDraft
                    {
                        Category = "application", Hypothesis = "Endpoints.Primary dereferences a null list.",
                        Confidence = 0.85, Primary = true,
                        Evidence = [new EvidenceDraft
                        {
                            StepId = StepHeader.Match(shown).Groups[1].Value,
                            Excerpt = groundIt ? LogLine : "a line nobody printed",
                        }],
                    },
                }),
            });

            return new JobLoopOutcome
            {
                Termination = TerminationReason.Concluded,
                ModelId = "claude-test",
                CodeRefs = refs,
                Repository = "https://github.com/Flou21/hephaisto-fixture-dotnet",
                AnalysedRef = "583b1e5b75add2ba341319eef1ae438e8346c4b0",
            };
        }
    }

    private static InvestigationRunner Runner(IInvestigationJobLoop job)
    {
        var clock = new TestClock();
        var planning = new FakeChatClient((_, _) => FakeChatClient.Text(JsonSerializer.Serialize(new ActionPlanDraft
        {
            Summary = "a human must fix the code", NoActionRequired = true, Actions = [],
        })));

        return new InvestigationRunner(
            new FakeChatClientFactory(FreePricing, new FakeChatClient(), planning),
            new PromptComposer(Options.Create(new EnvironmentCardOptions())),
            [AIFunctionFactory.Create((string pod) => LogLine, "get_pod_logs", "logs")],
            new GrafanaMcpToolProvider(new TestOptionsMonitor<GrafanaOptions>(new GrafanaOptions()), clock, NullLoggerFactory.Instance),
            new NullGlobalLlmBudget(),
            new Hephaisto.Agent.Pipeline.InvestigationTracker(clock),
            clock,
            new TestOptionsMonitor<LlmOptions>(new LlmOptions()),
            new TestOptionsMonitor<InvestigationOptions>(new InvestigationOptions()),
            NullLogger<InvestigationRunner>.Instance,
            job);
    }

    private static Incident NewIncident() => new()
    {
        Title = "shop-api crash-loops",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        OpenedAt = DateTimeOffset.UnixEpoch,
        LastSignalAt = DateTimeOffset.UnixEpoch,
        Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Pod", Name = "shop-api-1", OwnerKind = "Deployment", OwnerName = "shop-api" },
    };

    [Fact]
    public async Task Confirmed_references_attach_to_the_grounded_finding_they_name()
    {
        var outcome = await Runner(new ConcludingJob([new InvestigateCodeRef(0, "src/Shop.Api/Startup/Endpoints.cs", 17, null, "no null guard")]))
            .RunAsync(NewIncident(), CancellationToken.None);

        var finding = outcome.Investigation.Findings.Should().ContainSingle().Subject;
        var code = finding.CodeRefs.Should().ContainSingle().Subject;
        code.Path.Should().Be("src/Shop.Api/Startup/Endpoints.cs");
        code.Line.Should().Be(17);
        code.Ref.Should().Be("583b1e5b75add2ba341319eef1ae438e8346c4b0");
        code.Repository.Should().Contain("hephaisto-fixture-dotnet");
        finding.Evidence.Should().OnlyContain(e => e.StepId != Guid.Empty, "evidence is still tool steps only");
    }

    [Fact]
    public async Task A_reference_goes_with_its_finding_when_grounding_drops_it()
    {
        var outcome = await Runner(new ConcludingJob([new InvestigateCodeRef(0, "src/A.cs", 3, null, null)], groundIt: false))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.Findings.Should().BeEmpty("a code reference is never evidence, so it cannot save a finding");
    }

    [Fact]
    public async Task A_reference_to_a_finding_that_does_not_exist_is_dropped()
    {
        var outcome = await Runner(new ConcludingJob([new InvestigateCodeRef(5, "src/A.cs", 3, null, null)]))
            .RunAsync(NewIncident(), CancellationToken.None);

        outcome.Investigation.Findings.Single().CodeRefs.Should().BeEmpty();
    }

    [Fact]
    public void A_code_fix_plan_starts_from_the_pointer()
    {
        var finding = new Finding
        {
            Hypothesis = "Endpoints.Primary dereferences a null list.",
            CodeRefs = [new CodeRef { Path = "src/Shop.Api/Startup/Endpoints.cs", Line = 17, Ref = "583b1e5b", Note = "no null guard" }],
        };

        var text = CodeFixRequestBuilder.WithCodeRefs(finding);

        text.Should().StartWith("Endpoints.Primary dereferences a null list.");
        text.Should().Contain("src/Shop.Api/Startup/Endpoints.cs:17 at 583b1e5b (no null guard)");
        CodeFixRequestBuilder.WithCodeRefs(new Finding { Hypothesis = "h" }).Should().Be("h");
    }
}
