using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>One work item, as the API shows it.</summary>
/// <param name="Body">
/// The issue's text as it was when the work item was taken - somebody else's words, verbatim.
/// Whatever renders it treats it as that.
/// </param>
public sealed record WorkItemView(
    Guid Id,
    string Source,
    string Repository,
    int Number,
    string Url,
    string Title,
    string? Type,
    string AuthorLogin,
    WorkItemState State,
    string? StateReason,
    DateTimeOffset TakenAt,
    DateTimeOffset? ClosedAt,
    string Body);

/// <summary>Reads of <c>work_items</c> for the API. Never tracked, never written.</summary>
public sealed class WorkItemQueries(HephaistoDbContext db)
{
    public const int MaxLimit = 500;

    /// <summary>Newest first. <paramref name="state"/> null is every state.</summary>
    public async Task<IReadOnlyList<WorkItemView>> ListAsync(WorkItemState? state, int limit, CancellationToken ct)
    {
        var query = db.WorkItems.AsNoTracking();

        if (state is { } s)
        {
            query = query.Where(w => w.State == s);
        }

        var rows = await query
            .OrderByDescending(w => w.TakenAt)
            .ThenByDescending(w => w.Id)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(View);
    }

    public async Task<WorkItemView?> GetAsync(Guid id, CancellationToken ct) =>
        await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct).ConfigureAwait(false) is { } item
            ? View(item)
            : null;

    public static WorkItemView View(WorkItem w) => new(
        w.Id, w.Source, w.Repository, w.Number, w.Url, w.Title, w.Type, w.AuthorLogin,
        w.State, w.StateReason, w.TakenAt, w.ClosedAt, w.Body);
}
