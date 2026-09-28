using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Web;

/// <summary>
/// The seam between "something arrived" and "the ingest pipeline owns it now".
/// </summary>
/// <remarks>
/// <para>
/// Two doors, for two kinds of producer. <see cref="IngestAsync"/> is for the Alertmanager
/// webhook: it returns once the signal is <b>committed</b> and throws when it could not be, so the
/// webhook answers only after the write and answers 503 when there was none - which makes
/// Alertmanager's own retry the queue (#136). Until v0.10.0 the webhook used the other door,
/// answered 200 before anything was written, and a database outage dropped every alert that
/// arrived during it, told to Alertmanager as delivered.
/// </para>
/// <para>
/// <see cref="SubmitAsync"/> enqueues and returns, for the Kubernetes watcher, which has no one to
/// answer and no retry of its own. Both doors pass through one gate, so a webhook and the watcher
/// never triage at the same moment. That is what makes a retried alert safe: it arrives, finds its
/// own incident, and is absorbed.
/// </para>
/// <para>
/// <see cref="Signal.Fingerprint"/> is deliberately left empty by the webhook. Computing it
/// needs the cluster name, which is ingest configuration rather than anything the payload
/// carries - see <c>SignalFingerprinter.Compute</c>.
/// </para>
/// </remarks>
public interface ISignalSink
{
    ValueTask SubmitAsync(Signal signal, CancellationToken ct);

    /// <summary>Triage the signal and commit the result, or throw.</summary>
    Task IngestAsync(Signal signal, CancellationToken ct);
}

/// <summary>
/// The placeholder registration, so the webhook route is exercisable before the ingest
/// pipeline exists.
/// </summary>
/// <remarks>
/// It logs rather than throwing or returning 503. A webhook that rejects what it cannot yet
/// process teaches Alertmanager to retry forever, and the retry queue is not a useful place
/// to discover that a stream is unfinished - the log line is.
/// </remarks>
internal sealed class LoggingSignalSink(ILogger<LoggingSignalSink> logger) : ISignalSink
{
    public Task IngestAsync(Signal signal, CancellationToken ct) => SubmitAsync(signal, ct).AsTask();

    public ValueTask SubmitAsync(Signal signal, CancellationToken ct)
    {
        logger.LogInformation(
            "Signal dropped: no ISignalSink implementation is registered. {Source} {Kind} on {Target} ({Reason})",
            signal.Source,
            signal.Kind,
            signal.Target,
            signal.Reason);

        return ValueTask.CompletedTask;
    }
}
