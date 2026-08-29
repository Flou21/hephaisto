using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;
using Hephaisto.Tests.Investigations;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Builds a real <see cref="InvestigationRunner"/> over a scripted provider, for the eval tests.
/// </summary>
/// <remarks>
/// Everything except the model and the tools is the production object: the real
/// <c>FunctionInvokingChatClient</c>, the real budget guard, the real <c>SafeToolDecorator</c>,
/// the real digester and the real grounding verifier. That is the point - the eval harness
/// substitutes where the bytes come from and nothing else, so a test that faked the loop would
/// not be testing the harness's actual claim.
/// </remarks>
internal static class EvalScaffolding
{
    internal const string LogLine = "FATAL: could not connect to mongo: connection refused";

    internal static readonly LlmPricing FreePricing = new(new Dictionary<string, ModelPrice>());

    private static readonly Regex StepIdInTranscript =
        new(@"\[step ([0-9a-fA-F-]{36})\]", RegexOptions.CultureInvariant);

    internal static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static Incident NewIncident() => new()
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

    internal static InvestigationRunner Runner(
        FakeChatClientFactory factory,
        IEnumerable<AIFunction> tools,
        IGrafanaToolProvider? grafana = null)
    {
        var clock = new TestClock();

        return new InvestigationRunner(
            factory,
            new PromptComposer(Options.Create(new EnvironmentCardOptions())),
            tools,
            grafana ?? new NoGrafanaTools(),
            new NullGlobalLlmBudget(),
            new Hephaisto.Agent.Pipeline.InvestigationTracker(clock),
            clock,
            new TestOptionsMonitor<LlmOptions>(new LlmOptions()),
            new TestOptionsMonitor<InvestigationOptions>(new InvestigationOptions()),
            NullLogger<InvestigationRunner>.Instance);
    }

    /// <summary>
    /// The plan phase, which always answers "nothing to do" - these tests are about phase 1.
    /// </summary>
    internal static FakeChatClient NoActionPlanner() => new((_, _) => FakeChatClient.Text(
        """{"summary":"Nothing to do.","noActionRequired":true,"actions":[]}"""));

    /// <summary>
    /// Builds the arguments the model would send to <c>conclude</c>, quoting a step id it read out
    /// of a tool result - exactly as the real model does.
    /// </summary>
    internal static Dictionary<string, object?> ConcludeArgs(
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

/// <summary>A provider with nothing to offer, for tests that only exercise Kubernetes tools.</summary>
internal sealed class NoGrafanaTools : IGrafanaToolProvider
{
    public Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AIFunction>>([]);
}

/// <summary>A stand-in for grafana-mcp that returns a fixed tool list.</summary>
internal sealed class StubGrafanaTools(params AIFunction[] tools) : IGrafanaToolProvider
{
    public Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AIFunction>>(tools);
}
