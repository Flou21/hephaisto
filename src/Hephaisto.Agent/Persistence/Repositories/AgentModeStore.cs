using Microsoft.EntityFrameworkCore;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence.Repositories;

/// <summary>
/// The runaway latch, and the database arm of the kill switch built from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This arm restrains; it does not configure.</b> The mode itself is set by the Helm
/// values and reaches the pod as the env var and the projected ConfigMap, so it moves through
/// review and lands in git like every other deployment decision. There is deliberately no
/// method here that sets the mode: an operator who could raise autonomy from a web form would
/// be a second, unreviewed source of truth for the most consequential setting in the system.
/// </para>
/// <para>
/// What the row still holds is the runaway latch, which only ever restricts, plus the actor
/// and timestamp of the last re-arm. It is the only arm readable inside the same transaction
/// that admits an action, which is why the latch lives here rather than in a file.
/// </para>
/// </remarks>
public interface IAgentModeStore
{
    /// <summary>The row, or a default instance when it is missing. For display.</summary>
    Task<AgentModeRow> GetAsync(CancellationToken ct);

    /// <summary>
    /// The row, or null when it does not exist - which is a different fact from a row that
    /// happens to be unlatched, and the kill switch treats it differently.
    /// </summary>
    Task<AgentModeRow?> GetRowOrDefaultAsync(CancellationToken ct);

    /// <summary>Trips the runaway latch. Idempotent: latching an already-latched agent
    /// keeps the original reason, because the first trip is the interesting one.</summary>
    Task LatchAsync(string reason, CancellationToken ct);

    /// <summary>
    /// Clears the latch. Takes an actor because clearing it is a human decision by
    /// construction - an agent that could re-arm itself has no backstop, just a delay.
    /// </summary>
    Task ReArmAsync(string actor, CancellationToken ct);
}

public sealed class AgentModeStore(HephaistoDbContext db, IClock clock) : IAgentModeStore
{
    public async Task<AgentModeRow> GetAsync(CancellationToken ct) =>
        await GetRowOrDefaultAsync(ct).ConfigureAwait(false)
        ?? new AgentModeRow { ChangedAt = clock.UtcNow };

    public Task<AgentModeRow?> GetRowOrDefaultAsync(CancellationToken ct) =>
        db.AgentModeRows.FirstOrDefaultAsync(m => m.Id == AgentModeRow.SingletonId, ct);

    public async Task LatchAsync(string reason, CancellationToken ct)
    {
        var row = await EnsureRowAsync(ct);

        if (row.RunawayLatched)
        {
            return;
        }

        row.RunawayLatched = true;
        row.LatchReason = reason;
        row.LatchedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task ReArmAsync(string actor, CancellationToken ct)
    {
        var row = await EnsureRowAsync(ct);

        row.RunawayLatched = false;
        row.LatchReason = null;
        row.LatchedAt = null;
        row.ChangedBy = actor;
        row.ChangedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task<AgentModeRow> EnsureRowAsync(CancellationToken ct)
    {
        var row = await db.AgentModeRows.FirstOrDefaultAsync(m => m.Id == AgentModeRow.SingletonId, ct);

        if (row is not null)
        {
            return row;
        }

        // The migration seeds this row; recreating it here covers a database restored
        // without seed data, and Observe is the only safe value to recreate it with.
        row = new AgentModeRow
        {
            Id = AgentModeRow.SingletonId,
            ChangedAt = clock.UtcNow,
            ChangedBy = "hephaisto/system",
        };

        db.AgentModeRows.Add(row);

        return row;
    }
}
