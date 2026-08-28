using System.Threading.Channels;

namespace Watchtower.Agent.Pipeline;

/// <summary>
/// Bounded work queue between triage and the investigation loop.
/// </summary>
/// <remarks>
/// <para>
/// Bounded, and bounded small. An investigation costs real money and takes minutes, so the
/// queue is not a buffer to be drained - it is a backlog that means something is wrong. If it
/// fills, the correct response is to stop queueing and let triage escalate, not to accumulate
/// forty pending investigations that will each spend a dollar rediscovering the same node
/// failure.
/// </para>
/// <para>
/// <see cref="ChannelFullMode.Wait"/> rather than drop-oldest, unlike the signal channel: a
/// signal is one observation of many and losing the oldest is survivable, whereas an incident
/// that reached triage is already deduplicated and dropping it loses the only record that it
/// was ever going to be looked at.
/// </para>
/// </remarks>
public sealed class InvestigationQueue
{
    private readonly Channel<Guid> channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(capacity: 32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    public int Depth => channel.Reader.Count;

    /// <summary>Returns false when the queue is saturated, so the caller escalates instead of blocking triage.</summary>
    public bool TryEnqueue(Guid incidentId) => channel.Writer.TryWrite(incidentId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}
