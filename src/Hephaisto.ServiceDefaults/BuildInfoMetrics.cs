using System.Diagnostics.Metrics;

using Microsoft.Extensions.Hosting;

using Hephaisto.Core.Telemetry;

namespace Hephaisto.ServiceDefaults;

/// <summary>
/// Publishes <c>hephaisto_build_info{version,commit} 1</c> for as long as the process runs.
/// </summary>
/// <remarks>
/// A hosted service rather than a line in the composition root because the gauge has to be
/// created once and stay alive: an observable gauge whose Meter is collected stops reporting,
/// and a build-info series that disappears halfway through a retention window is worse than
/// none - it reads as "the deployment ended".
/// </remarks>
internal sealed class BuildInfoMetrics : IHostedService, IDisposable
{
    private readonly Meter meter;

    public BuildInfoMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        meter.CreateObservableGauge(
            HephaistoTelemetry.Metrics.BuildInfo,
            () => new Measurement<int>(
                1,
                new KeyValuePair<string, object?>("version", BuildInfo.Version),
                new KeyValuePair<string, object?>("commit", BuildInfo.ShortCommit)),
            unit: null,
            description: "Always 1. The labels carry the running version and commit, for joining against any other series.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => meter.Dispose();
}
