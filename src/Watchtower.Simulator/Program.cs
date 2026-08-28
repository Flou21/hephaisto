// Dev-only fault generator. Posts synthetic Alertmanager payloads and Kubernetes-shaped
// events at the agent's webhook so the ingest, dedup, correlation and investigation path
// can be exercised on a laptop with no cluster attached.
//
// This is NOT the chaos suite: infra/chaos/ breaks a real cluster in ten documented ways
// and is what the agent is actually graded against. The simulator only exercises the
// plumbing above the cluster boundary.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SimulatorWorker>();

await builder.Build().RunAsync();

internal sealed class SimulatorWorker(
    IConfiguration configuration,
    ILogger<SimulatorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var target = configuration["services:watchtower:http:0"]
                     ?? configuration["Watchtower:BaseUrl"];

        if (string.IsNullOrWhiteSpace(target))
        {
            logger.LogWarning("No watchtower endpoint discovered; simulator idle.");
            return;
        }

        logger.LogInformation("Simulator targeting {Target}. Scenarios are triggered on demand.", target);

        // Deliberately passive by default: a simulator that fires on its own turns every
        // `dotnet run` into a wall of fake incidents and trains you to ignore the UI.
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
    }
}
