using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Everything between "a signal arrived" and "an incident exists and is queued". Implements
/// <see cref="ISignalSink"/> for producers and drains the queue on its own background loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dedup, correlation and flap detection are deterministic C#, never the LLM.</b> They run
/// on the hot path for every signal including the thousand identical ones a node restart
/// produces, so they have to be cheap and, more importantly, they have to give the same answer
/// every time. An incident that is a duplicate on Tuesday and novel on Wednesday makes the
/// whole audit trail untrustworthy.
/// </para>
/// <para>
/// The channel is bounded with drop-oldest rather than unbounded. A kubelet restart emits
/// hundreds of events in seconds; an unbounded channel converts that into memory pressure in
/// the one pod whose job is to notice memory pressure. Dropping is counted, so the loss is
/// visible rather than silent.
/// </para>
/// </remarks>
public sealed class SignalIngestPipeline : BackgroundService, ISignalSink
{
    private readonly Channel<Signal> channel = Channel.CreateBounded<Signal>(
        new BoundedChannelOptions(capacity: 1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly IServiceScopeFactory scopeFactory;
    private readonly InvestigationQueue investigationQueue;
    private readonly IClock clock;
    private readonly IOptionsMonitor<IngestOptions> options;
    private readonly ILogger<SignalIngestPipeline> logger;
    private readonly HephaistoMetrics metrics;

    public SignalIngestPipeline(
        IServiceScopeFactory scopeFactory,
        InvestigationQueue investigationQueue,
        IClock clock,
        IOptionsMonitor<IngestOptions> options,
        HephaistoMetrics metrics,
        ILogger<SignalIngestPipeline> logger)
    {
        this.scopeFactory = scopeFactory;
        this.investigationQueue = investigationQueue;
        this.clock = clock;
        this.options = options;
        this.metrics = metrics;
        this.logger = logger;
    }

    /// <summary>
    /// Enqueue and return. Callers include the Alertmanager webhook, which is on somebody
    /// else's retry timer - see <see cref="ISignalSink"/>.
    /// </summary>
    public ValueTask SubmitAsync(Signal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (!channel.Writer.TryWrite(signal))
        {
            metrics.SignalDropped(signal.Source, "channel-full");
            logger.LogWarning("Signal channel saturated; dropped {Kind} on {Target}.", signal.Kind, signal.Target);
        }

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var signal in channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await IngestAsync(signal, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One malformed signal must never take the ingest loop down with it. The pod
                // restarting here would lose the whole in-memory backlog, turning a parse bug
                // into a monitoring outage.
                logger.LogError(ex, "Failed to ingest {Kind} signal on {Target}.", signal.Kind, signal.Target);
                metrics.SignalDropped(signal.Source, "ingest-error");
            }
        }
    }

    private async Task IngestAsync(Signal signal, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var now = clock.UtcNow;

        if (signal.FirstSeen == default) signal.FirstSeen = now;
        if (signal.LastSeen == default) signal.LastSeen = now;

        // The webhook cannot compute this: the fingerprint includes the cluster name, which is
        // ingest configuration rather than anything the payload carries.
        if (string.IsNullOrEmpty(signal.Fingerprint))
            signal.Fingerprint = SignalFingerprinter.Compute(signal, opts.ClusterName);

        metrics.SignalReceived(signal.Source, signal.Kind);

        await using var scope = scopeFactory.CreateAsyncScope();
        var triage = scope.ServiceProvider.GetRequiredService<IncidentTriage>();

        var result = await triage.TriageAsync(signal, ct).ConfigureAwait(false);

        if (result.Outcome is TriageOutcome.Investigate)
        {
            if (!investigationQueue.TryEnqueue(result.IncidentId))
            {
                // Saturated queue means a storm, and a storm is a cluster-level event. Escalating
                // in bulk is both cheaper and more accurate than forty separate investigations
                // each independently rediscovering the same node failure.
                logger.LogWarning(
                    "Investigation queue saturated; escalating incident {IncidentId} instead of queueing.",
                    result.IncidentId);

                await triage.EscalateAsync(result.IncidentId, EscalationReason.StormCircuitBreaker, ct)
                    .ConfigureAwait(false);
            }
        }

        logger.LogDebug(
            "Signal {Kind} on {Target} -> {Outcome} (incident {IncidentId}).",
            signal.Kind, signal.Target, result.Outcome, result.IncidentId);
    }
}
