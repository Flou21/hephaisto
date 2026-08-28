using System.Diagnostics.Metrics;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Safety;

/// <summary>
/// Polls the kill switch, logs every change, and publishes the effective mode as a metric.
/// </summary>
/// <remarks>
/// <para>
/// This service does not gate anything - every decision re-reads the switch itself, because a
/// gate that depends on a poller inherits the poller's latency and its failure modes. What
/// this exists for is <b>visibility</b>: a mode change is the single most consequential event
/// in the system and it must not be inferable only from behaviour.
/// </para>
/// <para>
/// The gauge matters more than the log line. <c>hephaisto.mode</c> is what lets you alert on
/// "the agent is in Auto" or "the agent dropped to Observe on its own", which is the question
/// an on-call engineer actually has. It is published from here rather than from the resolver
/// because only a poller can keep it fresh while nothing is happening.
/// </para>
/// </remarks>
public sealed class SwitchWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IKillSwitch killSwitch;
    private readonly ModeSnapshot snapshot;
    private readonly ILogger<SwitchWatcher> logger;
    private readonly Meter meter;

    public SwitchWatcher(
        IKillSwitch killSwitch,
        ModeSnapshot snapshot,
        IMeterFactory meterFactory,
        ILogger<SwitchWatcher> logger)
    {
        this.killSwitch = killSwitch;
        this.snapshot = snapshot;
        this.logger = logger;

        meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        meter.CreateObservableGauge(
            HephaistoTelemetry.Metrics.Mode,
            () => new Measurement<int>((int)snapshot.Effective, new KeyValuePair<string, object?>("mode", snapshot.Effective.ToString())),
            unit: null,
            description: "Effective agent mode: 0 Off, 1 Observe, 2 DryRun, 3 Auto. The most restrictive of the env, ConfigMap and database arms.");
    }

    public override void Dispose()
    {
        meter.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolve once before the first delay so the gauge and the startup log are correct
        // immediately, rather than reporting the Observe default for the first interval.
        await PollAsync(stoppingToken).ConfigureAwait(false);

        logger.LogInformation("Kill switch armed: {Explanation}", snapshot.Current?.Explain());

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            var resolved = await killSwitch.ResolveAsync(ct).ConfigureAwait(false);
            var previous = snapshot.Current;

            snapshot.Current = resolved;

            if (previous is null || previous.Effective == resolved.Effective)
            {
                return;
            }

            // Log a drop and a raise at different levels. Dropping to a more restrictive
            // mode is usually a human doing the right thing; being raised toward Auto is
            // the event worth waking up for, so it must not be buried at Information.
            if (resolved.Effective < previous.Effective)
            {
                logger.LogWarning(
                    "Agent mode dropped {Previous} -> {Effective}: {Explanation}",
                    previous.Effective, resolved.Effective, resolved.Explain());
            }
            else
            {
                logger.LogWarning(
                    "Agent mode RAISED {Previous} -> {Effective}: {Explanation}",
                    previous.Effective, resolved.Effective, resolved.Explain());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let the watcher die. It is the only thing publishing the mode gauge, and
            // a missing gauge reads on a dashboard as "no data", not as "something failed".
            logger.LogError(ex, "Kill switch poll failed; keeping the previous snapshot");
        }
    }
}

/// <summary>
/// The last resolved mode, shared between the poller that computes it and the gauge that
/// reports it. Defaults to Observe so the gauge never claims Auto before the first poll.
/// </summary>
public sealed class ModeSnapshot
{
    private volatile ModeResolution? current;

    public ModeResolution? Current
    {
        get => current;
        set => current = value;
    }

    public AgentMode Effective => current?.Effective ?? AgentMode.Observe;
}
