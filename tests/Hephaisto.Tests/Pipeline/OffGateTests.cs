using System.Diagnostics.Metrics;
using Hephaisto.Agent;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// <c>AgentMode.Off</c> is documented as "ingest nothing, investigate nothing. Full stop."
/// These tests exist because it did neither.
/// </summary>
/// <remarks>
/// <para>
/// On a live cluster, with <c>/api/status</c> reporting <c>effectiveMode: Off</c>, an injected
/// ImagePullBackOff was ingested, opened as an incident and escalated - open incidents went
/// 13 to 14 while the agent reported itself off. The bug was not subtle; it was untested. The
/// ingest and investigation pipelines had no coverage at all, which is exactly the layer the
/// contract lived in.
/// </para>
/// <para>
/// Each test below asserts on the FIRST thing past the gate rather than on some downstream
/// effect, so a failure names the gate that broke rather than the symptom.
/// </para>
/// </remarks>
public sealed class OffGateTests
{
    // ---------------------------------------------------------------------------------------
    // Ingest
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The test that would have caught the reported bug. Triage is what opens an incident, and
    /// it is only ever reached through a scope, so "no scope was created" is a precise way of
    /// saying "this signal never became an incident".
    /// </summary>
    [Fact]
    public async Task Off_drops_a_signal_before_it_can_become_an_incident()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var killSwitch = new KillSwitchStub(AgentMode.Off);
        using var meterFactory = new TestMeterFactory();
        using var metrics = new HephaistoMetrics(meterFactory);
        using var recorded = new CounterRecorder(HephaistoTelemetry.Metrics.SignalsDropped);

        var pipeline = BuildPipeline(scopeFactory, killSwitch, metrics);

        await pipeline.StartAsync(TestContext.Current.CancellationToken);
        await pipeline.SubmitAsync(NewSignal(), TestContext.Current.CancellationToken);
        await killSwitch.Asked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await pipeline.StopAsync(TestContext.Current.CancellationToken);

        scopeFactory.DidNotReceive().CreateScope();
        recorded.Reasons.Should().Contain("mode-off");
    }

    /// <summary>
    /// The other half, and the one that keeps the fix honest: a gate that never opens is not a
    /// kill switch, it is an outage. Observe must still ingest.
    /// </summary>
    [Fact]
    public async Task Observe_still_lets_a_signal_through_to_triage()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(Substitute.For<IServiceProvider>());
        scopeFactory.CreateScope().Returns(scope);

        var killSwitch = new KillSwitchStub(AgentMode.Observe);
        using var meterFactory = new TestMeterFactory();
        using var metrics = new HephaistoMetrics(meterFactory);

        var pipeline = BuildPipeline(scopeFactory, killSwitch, metrics);

        await pipeline.StartAsync(TestContext.Current.CancellationToken);
        await pipeline.SubmitAsync(NewSignal(), TestContext.Current.CancellationToken);
        await killSwitch.Asked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await pipeline.StopAsync(TestContext.Current.CancellationToken);

        // Resolving triage out of the scope is the very next thing the pipeline does. That it
        // got as far as asking for a scope is the assertion; what the stubbed provider then
        // fails to hand back is not this test's business.
        scopeFactory.Received().CreateScope();
    }

    // ---------------------------------------------------------------------------------------
    // Investigate
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The window an operator actually cares about: they hit the switch precisely because
    /// something is already queued. The ingest gate cannot help there - only this one can.
    /// </summary>
    [Fact]
    public async Task Off_does_not_dispatch_an_investigation_that_was_already_queued()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var queue = new InvestigationQueue();
        var killSwitch = new KillSwitchStub(AgentMode.Off);

        var worker = new InvestigationWorker(
            queue,
            scopeFactory,
            killSwitch,
            NullLogger<InvestigationWorker>.Instance);

        queue.TryEnqueue(Guid.CreateVersion7()).Should().BeTrue();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await killSwitch.Asked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        // No scope means no IIncidentInvestigator was ever resolved, which means no LLM
        // conversation was ever opened - which is the part that costs money.
        scopeFactory.DidNotReceive().CreateScope();
    }

    /// <summary>
    /// A restart is the normal way to apply a config change, so "switched Off, then restarted"
    /// is a routine sequence - and it used to come back up and start sixteen investigations.
    /// </summary>
    [Fact]
    public async Task Off_does_not_resurrect_stranded_incidents_on_start()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        var queue = new InvestigationQueue();
        var killSwitch = new KillSwitchStub(AgentMode.Off);

        var sweep = new StrandedIncidentRequeue(
            scopes,
            queue,
            killSwitch,
            NullLogger<StrandedIncidentRequeue>.Instance);

        await sweep.StartAsync(TestContext.Current.CancellationToken);
        await killSwitch.Asked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await sweep.StopAsync(TestContext.Current.CancellationToken);

        // It does not even reach the database: the gate is before the query, so an Off agent
        // does no work at all on this path.
        scopes.DidNotReceive().CreateScope();
        queue.Depth.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static SignalIngestPipeline BuildPipeline(
        IServiceScopeFactory scopeFactory,
        KillSwitchStub killSwitch,
        HephaistoMetrics metrics)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var options = Substitute.For<IOptionsMonitor<IngestOptions>>();
        options.CurrentValue.Returns(new IngestOptions());

        return new SignalIngestPipeline(
            scopeFactory,
            new InvestigationQueue(),
            clock,
            options,
            killSwitch,
            metrics,
            NullLogger<SignalIngestPipeline>.Instance);
    }

    private static Signal NewSignal() => new()
    {
        Source = SignalSource.KubernetesWatch,
        Kind = SignalKind.ImagePullBackOff,
        Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Pod", Name = "broken" },
        Severity = Severity.Warning,
        Reason = "ImagePullBackOff",
        Message = "back-off pulling image",
    };

    /// <summary>Minimal <see cref="IMeterFactory"/> so a real HephaistoMetrics can be built.</summary>
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
        }
    }

    /// <summary>
    /// Captures the <c>reason</c> tag of every measurement on one counter. Asserting on the
    /// real instrument rather than on a mock means the metric an operator will actually query
    /// is the thing under test.
    /// </summary>
    private sealed class CounterRecorder : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<string> reasons = [];
        private readonly Lock gate = new();

        public CounterRecorder(string instrumentName)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HephaistoTelemetry.MeterName && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value is string reason)
                    {
                        lock (gate)
                        {
                            reasons.Add(reason);
                        }
                    }
                }
            });

            listener.Start();
        }

        public IReadOnlyList<string> Reasons
        {
            get
            {
                lock (gate)
                {
                    return [.. reasons];
                }
            }
        }

        public void Dispose() => listener.Dispose();
    }
}
