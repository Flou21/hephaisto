using System.Diagnostics.Metrics;
using Hephaisto.Agent;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// The incident metrics are <b>recorded by production code</b>, not merely created.
/// </summary>
/// <remarks>
/// <para>
/// This is the distinction the roadmap insists on, and it is not pedantry.
/// <c>hephaisto.incidents.closed</c>, <c>hephaisto.incident.duration</c> and
/// <c>hephaisto.human.feedback</c> were all constructed on the meter, named in the dashboard's
/// spec table and drawn by panels - while <c>grep</c> for their call sites returned nothing.
/// Every one of them would pass a test that resolves the instrument and asserts it exists.
/// </para>
/// <para>
/// So each test here drives the real <see cref="IncidentTriage"/> and listens on the real
/// meter. Nothing is asserted against a mocked <see cref="HephaistoMetrics"/>: a mock would
/// happily verify a call to a method that emits under a label no dashboard query matches,
/// which is the second half of the same failure.
/// </para>
/// </remarks>
public sealed class IncidentMetricsTests
{
    /// <summary>The ordinary path: a new problem opens an incident and moves the gauge up.</summary>
    [Fact]
    public async Task Opening_an_incident_records_both_the_counter_and_the_gauge()
    {
        using var harness = new TriageHarness();

        await harness.TriageAsync(NewSignal());

        harness.Values(HephaistoTelemetry.Metrics.IncidentsOpened).Should().Equal(1);
        harness.Values(HephaistoTelemetry.Metrics.IncidentsOpen).Should().Equal(1);

        // The labels the dashboard's variable and panels actually select on. An unlabelled
        // series is emitted but unreadable, which is indistinguishable from absent on a panel
        // filtered by kind.
        harness.Tags(HephaistoTelemetry.Metrics.IncidentsOpened).Single()
            .Should().Contain(new KeyValuePair<string, object?>("kind", nameof(SignalKind.ImagePullBackOff)))
            .And.Contain(new KeyValuePair<string, object?>("severity", nameof(Severity.Warning)));
    }

    /// <summary>
    /// MTTR is recorded, which is the whole point of the item.
    /// </summary>
    /// <remarks>
    /// A self-signal escalates during triage, so this is the shortest real path from "opened"
    /// to "reached an outcome" that does not need a language model.
    /// </remarks>
    [Fact]
    public async Task Reaching_an_outcome_records_the_closed_counter_and_the_mttr_histogram()
    {
        using var harness = new TriageHarness();

        // Self-signals are hard-coded to escalate rather than be investigated.
        await harness.TriageAsync(SelfSignal());

        harness.Values(HephaistoTelemetry.Metrics.IncidentsClosed).Should().Equal(1);

        harness.Values(HephaistoTelemetry.Metrics.IncidentDuration).Should().ContainSingle()
            .Which.Should().BeGreaterThanOrEqualTo(0, "MTTR is seconds since OpenedAt");

        harness.Tags(HephaistoTelemetry.Metrics.IncidentsClosed).Single()
            .Should().Contain(new KeyValuePair<string, object?>("outcome", nameof(IncidentState.Escalated)));
    }

    /// <summary>
    /// Escalated is an outcome but not a close, so the gauge stays up.
    /// </summary>
    /// <remarks>
    /// This is the asymmetry that makes <c>opened - closed != open</c>, and it is deliberate:
    /// <see cref="Incident.IsOpen"/> and <c>HephaistoDbContext.OpenStates</c> both count an
    /// escalated incident as open, because a human still has it. Were the gauge to decrement
    /// here it would disagree with <c>/api/status.openIncidents</c>, which is the number an
    /// operator cross-checks it against.
    /// </remarks>
    [Fact]
    public async Task An_escalation_reaches_an_outcome_without_decrementing_the_open_gauge()
    {
        using var harness = new TriageHarness();

        await harness.TriageAsync(SelfSignal());

        harness.Values(HephaistoTelemetry.Metrics.IncidentsClosed).Should().Equal(1);
        harness.Values(HephaistoTelemetry.Metrics.IncidentsOpen).Sum().Should().Be(
            1, "an escalated incident is still open - a human has it");
    }

    /// <summary>
    /// A suppression does decrement, and nets the gauge back to zero.
    /// </summary>
    /// <remarks>
    /// The flapping path opens an incident and suppresses it in the same call, and it used not
    /// to count the open at all. Left that way, the decrement added here would have run against
    /// an increment that never happened and driven the gauge negative - a bug that only shows up
    /// under exactly this fixture.
    /// </remarks>
    [Fact]
    public async Task A_suppressed_incident_is_counted_open_first_so_the_gauge_nets_to_zero()
    {
        using var harness = new TriageHarness();
        harness.RecentForWorkload = new IngestOptions().FlapThreshold;

        await harness.TriageAsync(NewSignal());

        harness.Values(HephaistoTelemetry.Metrics.IncidentsOpened).Should().Equal(1);
        harness.Values(HephaistoTelemetry.Metrics.IncidentsClosed).Should().Equal(1);

        harness.Values(HephaistoTelemetry.Metrics.IncidentsOpen).Should().Equal(
            [1, -1], "an increment then a decrement, never a bare decrement");

        harness.Values(HephaistoTelemetry.Metrics.IncidentsOpen).Sum().Should().Be(0);
    }

    /// <summary>
    /// The feedback verdicts are the vocabulary the dashboard divides by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "Feedback precision" panel reads
    /// <c>verdict="correct" / verdict=~"correct|incorrect|partial"</c>. The instrument used to
    /// emit <c>helpful</c> / <c>unhelpful</c>, so had the missing call site simply been added,
    /// the panel's denominator would have matched nothing and drawn a division by zero - a
    /// metric recorded and still unreadable.
    /// </para>
    /// <para>
    /// <c>unclear</c> is deliberately outside that denominator: a reviewer who rated usefulness
    /// without ruling on the root cause has supplied no evidence either way.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true, true, "correct")]
    [InlineData(true, false, "correct")]
    [InlineData(false, true, "partial")]
    [InlineData(false, false, "incorrect")]
    [InlineData(null, true, "unclear")]
    public void Feedback_is_emitted_under_the_verdict_vocabulary_the_dashboard_queries(
        bool? rootCauseCorrect,
        bool helpful,
        string expected)
    {
        using var meterFactory = new MetricsHarness.Factory();
        using var metrics = new HephaistoMetrics(meterFactory);
        using var recorder = new MetricsHarness(HephaistoTelemetry.Metrics.HumanFeedback);

        metrics.HumanFeedback(
            new HumanFeedback
            {
                Helpful = helpful,
                RootCauseCorrect = rootCauseCorrect,
                FalsePositive = false,
                SubmittedBy = "flo",
            },
            SignalKind.CrashLoopBackOff);

        recorder.Tags(HephaistoTelemetry.Metrics.HumanFeedback).Single()
            .Should().Contain(new KeyValuePair<string, object?>("verdict", expected));
    }

    /// <summary>
    /// The false-positive rate has a label to compute it from.
    /// </summary>
    /// <remarks>
    /// One of the four numbers the v0.1.0 exit criterion is defined by, and the only one that
    /// cannot be self-assessed. Without this label the counter records that feedback happened
    /// but not the single fact it was collected for.
    /// </remarks>
    [Fact]
    public void Feedback_carries_the_false_positive_flag_the_exit_criterion_needs()
    {
        using var meterFactory = new MetricsHarness.Factory();
        using var metrics = new HephaistoMetrics(meterFactory);
        using var recorder = new MetricsHarness(HephaistoTelemetry.Metrics.HumanFeedback);

        metrics.HumanFeedback(
            new HumanFeedback { Helpful = false, FalsePositive = true, SubmittedBy = "flo" },
            SignalKind.Unschedulable);

        recorder.Tags(HephaistoTelemetry.Metrics.HumanFeedback).Single()
            .Should().Contain(new KeyValuePair<string, object?>("false_positive", "true"))
            .And.Contain(new KeyValuePair<string, object?>("kind", nameof(SignalKind.Unschedulable)));
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A signal about Hephaisto's own namespace, which triage hard-codes to escalate. The
    /// shortest real path from "opened" to "reached an outcome" with no language model in it.
    /// </summary>
    private static Signal SelfSignal()
    {
        var signal = NewSignal();
        signal.Target = new TargetRef { Namespace = "hephaisto", Kind = "Pod", Name = "hephaisto-0" };

        return signal;
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

    /// <summary>
    /// A real <see cref="IncidentTriage"/> over substituted repositories, wired to a real meter.
    /// </summary>
    private sealed class TriageHarness : IDisposable
    {
        private readonly MetricsHarness.Factory meterFactory = new();
        private readonly HephaistoMetrics metrics;
        private readonly MetricsHarness recorder;
        private readonly IncidentTriage triage;

        public TriageHarness()
        {
            metrics = new HephaistoMetrics(meterFactory);

            recorder = new MetricsHarness(
                HephaistoTelemetry.Metrics.IncidentsOpened,
                HephaistoTelemetry.Metrics.IncidentsClosed,
                HephaistoTelemetry.Metrics.IncidentsOpen,
                HephaistoTelemetry.Metrics.IncidentDuration);

            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(_ => DateTimeOffset.UtcNow);

            var options = Substitute.For<IOptionsMonitor<IngestOptions>>();
            options.CurrentValue.Returns(new IngestOptions());

            var incidents = Substitute.For<IIncidentRepository>();
            incidents.FindByFingerprintAsync(default!, default, default)
                .ReturnsForAnyArgs(Task.FromResult<Incident?>(null));
            incidents.FindByCorrelationKeyAsync(default!, default)
                .ReturnsForAnyArgs(Task.FromResult<Incident?>(null));
            incidents.CountRecentForWorkloadAsync(default!, default, default)
                .ReturnsForAnyArgs(_ => Task.FromResult(RecentForWorkload));

            triage = new IncidentTriage(
                incidents,
                Substitute.For<IAuditRepository>(),
                new IncidentStateMachine(clock),
                clock,
                options,
                metrics,
                new NullGrafanaAnnotator(),
                NullLogger<IncidentTriage>.Instance);
        }

        /// <summary>Drives the flap branch when raised to <c>IngestOptions.FlapThreshold</c>.</summary>
        public int RecentForWorkload { get; set; }

        public Task TriageAsync(Signal signal) =>
            triage.TriageAsync(signal, TestContext.Current.CancellationToken);

        public IReadOnlyList<double> Values(string instrument) => recorder.Values(instrument);

        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Tags(string instrument) =>
            recorder.Tags(instrument);

        public void Dispose()
        {
            recorder.Dispose();
            metrics.Dispose();
            meterFactory.Dispose();
        }
    }

    /// <summary>
    /// Captures values and tags for named instruments off the real meter.
    /// </summary>
    /// <remarks>
    /// Deliberately keyed by instrument name rather than by a mock, so a test fails if the
    /// production code stops calling <i>or</i> starts emitting under a name the dashboard does
    /// not query.
    /// </remarks>
    private sealed class MetricsHarness : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly Lock gate = new();

        private readonly Dictionary<string, List<double>> values = [];
        private readonly Dictionary<string, List<IReadOnlyList<KeyValuePair<string, object?>>>> tags = [];

        public MetricsHarness(params string[] instruments)
        {
            var wanted = new HashSet<string>(instruments, StringComparer.Ordinal);

            listener.InstrumentPublished = (instrument, l) =>
            {
                if (wanted.Contains(instrument.Name))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>(
                (instrument, value, t, _) => Record(instrument.Name, value, t));

            listener.SetMeasurementEventCallback<double>(
                (instrument, value, t, _) => Record(instrument.Name, value, t));

            listener.Start();
        }

        public IReadOnlyList<double> Values(string instrument)
        {
            lock (gate)
            {
                return values.TryGetValue(instrument, out var v) ? [.. v] : [];
            }
        }

        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Tags(string instrument)
        {
            lock (gate)
            {
                return tags.TryGetValue(instrument, out var t) ? [.. t] : [];
            }
        }

        public void Dispose() => listener.Dispose();

        private void Record(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> t)
        {
            var copy = t.ToArray();

            lock (gate)
            {
                (values.TryGetValue(name, out var v) ? v : values[name] = []).Add(value);
                (tags.TryGetValue(name, out var g) ? g : tags[name] = []).Add(copy);
            }
        }

        /// <summary>Minimal <see cref="IMeterFactory"/> so a real HephaistoMetrics can be built.</summary>
        public sealed class Factory : IMeterFactory
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
    }
}
