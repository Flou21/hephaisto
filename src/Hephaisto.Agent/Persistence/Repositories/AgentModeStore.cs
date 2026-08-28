using Microsoft.EntityFrameworkCore;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence.Repositories;

/// <summary>
/// The database arm of the kill switch. The env var and ConfigMap arms live elsewhere and
/// the most restrictive of the three wins; this one exists because it is the only arm a
/// human can flip without a deploy, and the only one that can be read inside the same
/// transaction that admits an action.
/// </summary>
public interface IAgentModeStore
{
    /// <summary>Returns <see cref="AgentMode.Observe"/> when the row is missing - an
    /// unreadable kill switch reads as the restrictive value, never the permissive one.</summary>
    Task<AgentMode> GetModeAsync(CancellationToken ct);

    Task<AgentModeRow> GetAsync(CancellationToken ct);

    Task SetModeAsync(AgentMode mode, string actor, CancellationToken ct);

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
    public async Task<AgentMode> GetModeAsync(CancellationToken ct)
    {
        var row = await db.AgentModeRows
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == AgentModeRow.SingletonId, ct);

        if (row is null || row.RunawayLatched)
        {
            return AgentMode.Observe;
        }

        return row.Mode;
    }

    public async Task<AgentModeRow> GetAsync(CancellationToken ct) =>
        await db.AgentModeRows.FirstOrDefaultAsync(m => m.Id == AgentModeRow.SingletonId, ct)
        ?? new AgentModeRow { Mode = AgentMode.Observe, ChangedAt = clock.UtcNow };

    public async Task SetModeAsync(AgentMode mode, string actor, CancellationToken ct)
    {
        var row = await EnsureRowAsync(ct);

        row.Mode = mode;
        row.ChangedBy = actor;
        row.ChangedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
    }

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
            Mode = AgentMode.Observe,
            ChangedAt = clock.UtcNow,
            ChangedBy = "hephaisto/system",
        };

        db.AgentModeRows.Add(row);

        return row;
    }
}
