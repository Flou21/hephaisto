using Watchtower.Core.Domain;

namespace Watchtower.Agent.Web;

/// <summary>
/// The seam between "something posted a webhook" and "the ingest pipeline owns it now".
/// </summary>
/// <remarks>
/// <para>
/// The webhook handler is the one place in the system that is on somebody else's retry
/// timer. Alertmanager re-POSTs a group every <c>repeat_interval</c> and treats any slow or
/// failed response as "did not arrive", so a handler that does fingerprinting, dedup, a
/// correlation lookup and an INSERT before replying turns one slow database into a
/// self-inflicted duplicate storm. Implementations must therefore <b>enqueue and return</b>;
/// anything that opens a connection belongs on the consumer side of the queue, not here.
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
