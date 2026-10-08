using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Hephaisto.Agent;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// One metric name has one instrument (backlog #15).
/// </summary>
/// <remarks>
/// <para>
/// Four names were registered twice on the <c>Hephaisto</c> meter, by two classes that each
/// looked right alone: <c>investigation.steps</c> as a histogram and as a counter,
/// <c>investigation.duration</c> in seconds and in milliseconds, and
/// <c>investigation.terminations</c> and <c>grounding.rejected</c> as two counters that each
/// counted the same event. The watcher registered <c>signals.received</c> a second time and
/// counted every signal the pipeline was about to count again.
/// </para>
/// <para>
/// Nothing fails when that happens. The exporter publishes both, and the conflict exists only
/// in the series a dashboard reads. So the first test reads the source rather than a meter:
/// it is the registration that has to be unique, and a listener sees two instruments with one
/// name only when both classes happen to have been constructed.
/// </para>
/// </remarks>
public sealed partial class OneRegistrationPerMetricTests
{
    [Fact]
    public void Every_metric_name_is_registered_in_exactly_one_place()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var registrations = new List<(string Name, string Where)>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file);

            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            foreach (Match match in Registration().Matches(File.ReadAllText(file)))
            {
                registrations.Add((match.Groups["name"].Value, relative));
            }
        }

        // The scan found the registrations at all: a pattern that matches nothing passes for ever.
        registrations.Select(r => r.Name).Should().Contain(nameof(HephaistoTelemetry.Metrics.InvestigationSteps));
        registrations.Count.Should().BeGreaterThan(30);

        var twice = registrations
            .GroupBy(r => r.Name)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(r => r.Where))}")
            .ToArray();

        twice.Should().BeEmpty(
            "a name registered twice is two instruments under one name, and they disagree only in the exported series");
    }

    /// <summary>
    /// The registration that stayed is the one the dashboard reads: seconds, a histogram of steps,
    /// and the <c>kind</c> every investigation panel filters by.
    /// </summary>
    [Fact]
    public void An_investigation_is_recorded_once_in_the_shape_the_dashboard_queries()
    {
        using var meterFactory = new TestMeterFactory();
        using var metrics = new HephaistoMetrics(meterFactory);

        var seen = new List<(string Name, string? Unit, Type Instrument, double Value, Dictionary<string, object?> Tags)>();
        var gate = new Lock();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meterFactory.Meter)
                && instrument.Name.StartsWith("hephaisto.investigation.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            var copy = new Dictionary<string, object?>();

            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (gate)
            {
                seen.Add((instrument.Name, instrument.Unit, instrument.GetType().GetGenericTypeDefinition(),
                    Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), copy));
            }
        }

        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.Start();

        metrics.InvestigationCompleted(
            SignalKind.CrashLoopBackOff,
            TimeSpan.FromSeconds(90),
            steps: 7,
            TerminationReason.Concluded,
            executor: "Job");

        seen.Should().HaveCount(3, "one measurement per instrument, and no second instrument under any of the names");

        var duration = seen.Single(s => s.Name == HephaistoTelemetry.Metrics.InvestigationDuration);
        duration.Unit.Should().Be("s");
        duration.Value.Should().Be(90);
        duration.Tags.Should().Contain("kind", "CrashLoopBackOff");
        duration.Tags.Should().Contain("termination_reason", "Concluded");

        var steps = seen.Single(s => s.Name == HephaistoTelemetry.Metrics.InvestigationSteps);
        steps.Instrument.Should().Be(typeof(Histogram<>));
        steps.Value.Should().Be(7);
        steps.Tags.Should().Contain("kind", "CrashLoopBackOff");
        steps.Tags.Should().Contain("termination_reason", "Concluded");

        var termination = seen.Single(s => s.Name == HephaistoTelemetry.Metrics.InvestigationTerminations);
        termination.Value.Should().Be(1);
        termination.Tags.Should().Contain("kind", "CrashLoopBackOff");
        termination.Tags.Should().Contain("reason", "Concluded");
        termination.Tags.Should().Contain("executor", "Job");
    }

    // Create<Kind>(<T>)?( HephaistoTelemetry.Metrics.<Name> - across a line break, which is how
    // the longer registrations are written.
    [GeneratedRegex(@"\.Create(?:Observable)?(?:Counter|Histogram|UpDownCounter|Gauge)(?:<[^>(]+>)?\(\s*HephaistoTelemetry\.Metrics\.(?<name>\w+)")]
    private static partial Regex Registration();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Meter { get; private set; } = null!;

        public Meter Create(MeterOptions options)
        {
            Meter = new Meter(options);
            return Meter;
        }

        public void Dispose() => Meter?.Dispose();
    }
}
