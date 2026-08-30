using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Hephaisto.Agent;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;
using Hephaisto.Tests.TestData;
using NSubstitute;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// The action metrics are RECORDED, not merely created, and their labels are closed.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is the lesson of backlog #5: a guard test asserting "an instrument exists"
/// passes on exactly the bug it is supposed to catch, because every one of these instruments
/// already existed and had no call site. So these drive the real code and listen on the real
/// meter, and were verified by deleting the call and watching them go red.
/// </para>
/// <para>
/// The cardinality assertions matter as much. These are counters on a path that runs for every
/// proposed action, and a label value containing a timestamp or a pod age is an unbounded
/// series - which is what hephaisto.policy.decisions was doing until v0.2.0.
/// </para>
/// </remarks>
public sealed class ActionMetricsTests : IDisposable
{
    private readonly Factory factory = new();
    private readonly HephaistoMetrics metrics;

    public ActionMetricsTests() => metrics = new HephaistoMetrics(factory);

    public void Dispose()
    {
        metrics.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task A_rollback_records_that_it_happened()
    {
        using var recorded = new Recorder(HephaistoTelemetry.Metrics.ActionsRolledBack);

        var executor = Substitute.For<IActionExecutor>();
        executor
            .ExecuteAsync(Arg.Any<AgentAction>(), Arg.Any<CancellationToken>())
            .Returns(new ActionExecutionResult { Outcome = ActionExecutionOutcome.Executed });

        var sut = new ActionRollback(executor, metrics, Given.Clock(), NullLogger<ActionRollback>.Instance);

        await sut.TryRevertAsync(
            new AgentAction
            {
                IncidentId = Given.IncidentId,
                Type = ActionType.ScaleWorkload,
                Target = Given.Target(),
                Risk = RiskTier.Medium,
                PreState = """{"replicas":3}""",
            },
            TestContext.Current.CancellationToken);

        recorded.Tags.Should().ContainSingle()
            .Which.Should().ContainKey("type").WhoseValue.Should().Be("ScaleWorkload");
    }

    [Fact]
    public void A_verification_result_records_its_outcome_and_attempt()
    {
        using var recorded = new Recorder(HephaistoTelemetry.Metrics.VerificationResult);

        metrics.VerificationResult(VerificationOutcome.Failed, 3);

        var tags = recorded.Tags.Should().ContainSingle().Subject;
        tags["result"].Should().Be("Failed");
        tags["attempt"].Should().Be("3");
    }

    [Fact]
    public void An_executed_action_records_a_closed_outcome_vocabulary()
    {
        using var recorded = new Recorder(HephaistoTelemetry.Metrics.ActionsExecuted);

        metrics.ActionExecuted(ActionType.RestartPod, AgentMode.Auto, ActionExecutor.Outcomes.Applied);

        var tags = recorded.Tags.Should().ContainSingle().Subject;
        tags["type"].Should().Be("RestartPod");
        tags["mode"].Should().Be("Auto");
        tags["outcome"].Should().Be("applied");
    }

    [Fact]
    public void A_policy_decision_carries_no_free_text()
    {
        // The regression. This label used to be the verdict's first reason, and those reasons
        // are prose for a human: "workload is quarantined until 2026-08-30T12:34:56.789Z",
        // "pod is 45s old, younger than the 120s minimum". A timestamp in a label value is an
        // unbounded series on a counter that fires for every proposed action.
        using var recorded = new Recorder(HephaistoTelemetry.Metrics.PolicyDecisions);

        metrics.PolicyDecision(PolicyDecision.RequireApproval, ActionType.RestartPod, downgraded: true);

        var tags = recorded.Tags.Should().ContainSingle().Subject;

        tags.Should().NotContainKey("reason");
        tags["decision"].Should().Be("RequireApproval");
        tags["action_type"].Should().Be("RestartPod");
        tags["downgraded"].Should().Be("true");

        // Every value has to come from a closed set: an enum name, a type name, or a bool.
        foreach (var value in tags.Values)
        {
            value.Should().NotMatchRegex(@"\d{4}-\d{2}-\d{2}", "a label must never carry a timestamp");
        }
    }

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<Dictionary<string, string?>> tags = [];
        private readonly Lock gate = new();

        public Recorder(string instrumentName)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HephaistoTelemetry.MeterName && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>((_, _, measured, _) =>
            {
                var captured = new Dictionary<string, string?>(StringComparer.Ordinal);

                foreach (var tag in measured)
                {
                    captured[tag.Key] = tag.Value?.ToString();
                }

                lock (gate)
                {
                    tags.Add(captured);
                }
            });

            listener.Start();
        }

        public IReadOnlyList<Dictionary<string, string?>> Tags
        {
            get
            {
                lock (gate)
                {
                    return [.. tags];
                }
            }
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed class Factory : IMeterFactory
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
