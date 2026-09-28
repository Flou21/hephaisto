using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

using Hephaisto.Agent;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Web;

/// <summary>
/// The webhook answers after it has written, and refuses when it could not (#136).
/// </summary>
/// <remarks>
/// Until v0.10.0 it enqueued, answered 200, and dropped whatever failed to write afterwards -
/// Alertmanager had been told the alert arrived, so it never retried. The pager suite's P13 asserts
/// the same thing on an installed agent with its database stopped.
/// </remarks>
public sealed class AlertmanagerWebhookTests : IDisposable
{
    private readonly ServiceProvider meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly HephaistoMetrics metrics;
    private readonly RecordingSink sink = new();

    public AlertmanagerWebhookTests() => metrics = new HephaistoMetrics(meters.GetRequiredService<IMeterFactory>());

    public void Dispose()
    {
        metrics.Dispose();
        meters.Dispose();
    }

    [Fact]
    public async Task Every_alert_written_is_a_200_with_the_count()
    {
        var result = await ReceiveAsync(Alert("A"), Alert("B"));

        result.Result.Should().BeOfType<Ok<AlertIngestResult>>()
            .Which.Value!.Accepted.Should().Be(2);
        sink.Written.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_write_that_fails_is_a_503_and_stops_the_group()
    {
        sink.FailOn = "B";

        var result = await ReceiveAsync(Alert("A"), Alert("B"), Alert("C"));

        result.Result.Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        sink.Written.Select(s => s.Reason).Should().Equal(["A"],
            "the database being down is the likely cause, and C would fail the same way; Alertmanager re-sends the group");
    }

    [Fact]
    public async Task A_NUL_in_a_label_is_removed_rather_than_poisoning_the_group()
    {
        var alert = Alert("A");
        alert.Labels["note"] = "a\0b";

        var result = await ReceiveAsync(alert);

        result.Result.Should().BeOfType<Ok<AlertIngestResult>>();
        sink.Written.Single().Labels["note"].Should().Be("ab");
    }

    [Fact]
    public async Task The_watchdog_is_recorded_and_not_written()
    {
        var watchdog = new WatchdogMonitor(new Hephaisto.Tests.Investigations.TestClock());

        await AlertmanagerEndpoints.ReceiveAlertsAsync(
            Payload(Alert("Watchdog")), sink, watchdog, metrics, NullLoggerFactory.Instance, CancellationToken.None);

        sink.Written.Should().BeEmpty();
        watchdog.ReceiptCount.Should().Be(1);
    }

    private Task<Microsoft.AspNetCore.Http.HttpResults.Results<Ok<AlertIngestResult>, ProblemHttpResult>> ReceiveAsync(
        params AlertmanagerAlert[] alerts) =>
        AlertmanagerEndpoints.ReceiveAlertsAsync(
            Payload(alerts), sink, new WatchdogMonitor(new Hephaisto.Tests.Investigations.TestClock()), metrics, NullLoggerFactory.Instance, CancellationToken.None);

    private static AlertmanagerWebhook Payload(params AlertmanagerAlert[] alerts) =>
        new() { Receiver = "hephaisto", Status = "firing", Alerts = [.. alerts] };

    private static AlertmanagerAlert Alert(string name) => new()
    {
        Status = "firing",
        Labels = new Dictionary<string, string> { ["alertname"] = name, ["namespace"] = "shop", ["deployment"] = "api" },
        Annotations = [],
        StartsAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingSink : ISignalSink
    {
        public List<Signal> Written { get; } = [];

        public string? FailOn { get; set; }

        public ValueTask SubmitAsync(Signal signal, CancellationToken ct) => throw new NotSupportedException();

        public Task IngestAsync(Signal signal, CancellationToken ct)
        {
            if (signal.Reason == FailOn)
            {
                throw new InvalidOperationException("the database is not answering");
            }

            Written.Add(signal);
            return Task.CompletedTask;
        }
    }
}
