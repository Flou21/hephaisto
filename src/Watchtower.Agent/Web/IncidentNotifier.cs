using System.Collections.Concurrent;
using System.Threading.Channels;

using Watchtower.Core.Domain;

namespace Watchtower.Agent.Web;

/// <summary>What moved. Coarse on purpose - a subscriber re-reads, it does not apply a patch.</summary>
/// <remarks>
/// Named <c>Live</c> rather than matching <see cref="Watchtower.Core.Domain.IncidentEvent"/>
/// because that type already means something else and is persisted: it is one row per state
/// transition, written inside the state machine. This is a transient in-process nudge that
/// is never stored and may be dropped. Two types with one name, one of them an audit record
/// and one of them droppable, is a mistake waiting to be made in a code review.
/// </remarks>
public enum IncidentLiveEventKind
{
    Opened = 0,
    StateChanged = 1,
    SignalAdded = 2,
    InvestigationStarted = 3,
    InvestigationProgressed = 4,
    InvestigationCompleted = 5,
    PlanReady = 6,
    FeedbackSubmitted = 7,
}

public sealed record IncidentLiveEvent
{
    public required Guid IncidentId { get; init; }

    public required IncidentLiveEventKind Kind { get; init; }

    /// <summary>The state after the transition, when this event is one.</summary>
    public IncidentState? State { get; init; }

    /// <summary>One short line for the UI's activity strip. Never the mechanism of the update.</summary>
    public string? Detail { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// In-process fan-out from the pipeline to whichever Blazor circuits are watching.
/// </summary>
/// <remarks>
/// <para>
/// Not SignalR groups, not a database poll, not Redis. The agent is a single pod by
/// construction - the whole action-admission transaction depends on it - so "notify the
/// other replicas" is a problem that cannot arise, and the circuit that would carry a
/// SignalR broadcast is already open underneath the component. A <see cref="Channel{T}"/>
/// per subscriber is the entire mechanism.
/// </para>
/// <para>
/// <b>Publishing never blocks and never throws.</b> The producers are the ingest loop and
/// the investigation loop; a UI subscriber that has stopped reading - a laptop lid closed
/// mid-incident - must not be able to apply backpressure to either. Each subscriber gets a
/// small bounded channel with <see cref="BoundedChannelFullMode.DropOldest"/>, so a stalled
/// reader loses intermediate events and still receives the most recent one. That is
/// acceptable precisely because the payload is a nudge: the subscriber re-reads from
/// Postgres and lands on current truth regardless of what it missed.
/// </para>
/// </remarks>
public interface IIncidentNotifier
{
    /// <summary>Fire-and-forget. Safe to call from a hot loop and from any thread.</summary>
    void Publish(IncidentLiveEvent liveEvent);

    /// <summary>
    /// Enumerates until <paramref name="ct"/> fires. Cancelling is the only way to
    /// unsubscribe, which is why every caller is a component's disposal token.
    /// </summary>
    IAsyncEnumerable<IncidentLiveEvent> SubscribeAsync(CancellationToken ct);
}

internal sealed class IncidentNotifier(ILogger<IncidentNotifier> logger) : IIncidentNotifier
{
    /// <summary>
    /// Deep enough to absorb one investigation's burst of step events, shallow enough that a
    /// dead subscriber holds a bounded amount of memory rather than an unbounded backlog.
    /// </summary>
    private const int SubscriberCapacity = 64;

    private readonly ConcurrentDictionary<Guid, Channel<IncidentLiveEvent>> _subscribers = new();

    public void Publish(IncidentLiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);

        foreach (var (id, channel) in _subscribers)
        {
            // TryWrite, never WriteAsync: DropOldest means this only fails once the channel
            // is completed, i.e. the subscriber is already gone and simply has not been
            // reaped yet. Removing it here keeps the dictionary from growing across a day of
            // page refreshes.
            if (!channel.Writer.TryWrite(liveEvent))
            {
                _subscribers.TryRemove(id, out _);
            }
        }
    }

    public async IAsyncEnumerable<IncidentLiveEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.CreateVersion7();

        var channel = Channel.CreateBounded<IncidentLiveEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;
        logger.LogDebug("Incident subscriber {SubscriberId} attached ({Count} total)", id, _subscribers.Count);

        try
        {
            await foreach (var liveEvent in channel.Reader.ReadAllAsync(ct))
            {
                yield return liveEvent;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            logger.LogDebug("Incident subscriber {SubscriberId} detached", id);
        }
    }
}
