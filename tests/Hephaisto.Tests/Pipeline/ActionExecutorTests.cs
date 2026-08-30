using System.Diagnostics.Metrics;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Tests.TestData;
using NSubstitute;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// What the executor does before it reaches the cluster, and what it refuses to do at all.
/// </summary>
/// <remarks>
/// Every test here points the Kubernetes client at a port nothing listens on. That is the
/// point: the properties worth pinning are the ones that must hold <i>without</i> a successful
/// API call - that unsupported types never reach the API, that an unreadable target stops the
/// action before admission, and that admission is never asked about something this build
/// cannot perform. Executing for real is the e2e harness's job, against a real cluster.
/// </remarks>
public sealed class ActionExecutorTests : IDisposable
{
    private readonly TestMeterFactory meterFactory = new();
    private readonly HephaistoMetrics metrics;
    private readonly IActionRepository actions = Substitute.For<IActionRepository>();

    public ActionExecutorTests() => metrics = new HephaistoMetrics(meterFactory);

    public void Dispose()
    {
        metrics.Dispose();
        meterFactory.Dispose();
    }

    private ActionExecutor Executor()
    {
        var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" });

        return new ActionExecutor(
            new KubernetesApi(client),
            actions,
            new PolicyStub(Given.Options()),
            metrics,
            Given.Clock(),
            NullLogger<ActionExecutor>.Instance);
    }

    private static AgentAction Action(ActionType type) => new()
    {
        IncidentId = Given.IncidentId,
        Type = type,
        Target = Given.Target(),
        Risk = RiskTier.Low,
        State = ActionState.Approved,
    };

    [Theory]
    [InlineData(ActionType.CordonNode)]
    [InlineData(ActionType.DrainNode)]
    [InlineData(ActionType.SilenceAlert)]
    [InlineData(ActionType.PatchResources)]
    [InlineData(ActionType.RollbackDeployment)]
    [InlineData(ActionType.DeletePvc)]
    [InlineData(ActionType.DeleteWorkload)]
    public async Task An_action_this_build_cannot_perform_is_refused_before_anything_is_attempted(ActionType type)
    {
        var action = Action(type);

        var result = await Executor().ExecuteAsync(action, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ActionExecutionOutcome.Unsupported);
        action.State.Should().Be(ActionState.Failed);
        action.Outcome.Should().Be(ActionExecutor.Outcomes.Unsupported);
    }

    [Fact]
    public async Task An_unsupported_action_is_never_put_to_admission()
    {
        // CordonNode's ClusterRole ships deliberately unbound, so reaching admission - and
        // then the API - would spend a budget slot and return a 403 that reads like a
        // misconfiguration rather than a deliberate absence.
        await Executor().ExecuteAsync(Action(ActionType.CordonNode), TestContext.Current.CancellationToken);

        await actions.DidNotReceive().TryAdmitActionAsync(
            Arg.Any<AgentAction>(), Arg.Any<PolicyOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_target_that_cannot_be_read_stops_the_action_before_admission()
    {
        // Order is the safety property. Without a PreState there is nothing to verify against
        // and nothing to describe afterwards, so the action must not be admitted - admission
        // commits a budget slot and an audit row saying the agent decided to go ahead.
        var action = Action(ActionType.RestartPod);

        var result = await Executor().ExecuteAsync(action, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ActionExecutionOutcome.NoPreState);
        action.State.Should().Be(ActionState.Failed);

        await actions.DidNotReceive().TryAdmitActionAsync(
            Arg.Any<AgentAction>(), Arg.Any<PolicyOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_outcome_vocabulary_is_closed()
    {
        // AgentAction.Outcome reaches a Prometheus label through ActionExecuted. Anything
        // derived from an API message would put unbounded cardinality on a counter - the same
        // mistake backlog #12 records on hephaisto.grounding.rejected. The detail belongs in
        // Error and on the span, never in the label.
        var vocabulary = typeof(ActionExecutor.Outcomes)
            .GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        vocabulary.Should().BeEquivalentTo(["applied", "dry_run", "failed", "unsupported"]);
    }

    [Fact]
    public async Task The_refusing_executor_does_nothing_and_says_so()
    {
        // What a host gets when the real executor was never registered. It must not resolve to
        // something that half works.
        var sut = new RefusingActionExecutor(NullLogger<RefusingActionExecutor>.Instance);

        var result = await sut.ExecuteAsync(Action(ActionType.RestartPod), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ActionExecutionOutcome.Unsupported);
        result.Changed.Should().BeFalse();
    }

    [Fact]
    public void A_dry_run_execution_never_counts_as_a_change()
    {
        // Changed is what the e2e's containment assertion reads. A dry run reaches the API
        // server, is validated, and is discarded - it must never look like a mutation.
        new ActionExecutionResult { Outcome = ActionExecutionOutcome.Executed, DryRun = true }
            .Changed.Should().BeFalse();

        new ActionExecutionResult { Outcome = ActionExecutionOutcome.Executed, DryRun = false }
            .Changed.Should().BeTrue();
    }

    private sealed class PolicyStub(PolicyOptions value) : IOptionsMonitor<PolicyOptions>
    {
        public PolicyOptions CurrentValue => value;

        public PolicyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<PolicyOptions, string?> listener) => null;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in meters)
            {
                meter.Dispose();
            }

            meters.Clear();
        }
    }
}
