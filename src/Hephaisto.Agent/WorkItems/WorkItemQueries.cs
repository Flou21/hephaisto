using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>One work item, as the API shows it.</summary>
/// <param name="Body">
/// The issue's text as it was when the work item was taken - somebody else's words, verbatim.
/// Whatever renders it treats it as that.
/// </param>
/// <param name="StatusCommentId">The one comment Hephaisto edits on the issue; null until it is written.</param>
/// <param name="DeclineReason">
/// Why no plan was started when it was last asked, while there is no attempt. Asked again on
/// every pass; null once an attempt exists.
/// </param>
/// <param name="StillAssigned">
/// True for a work item that ended - its pull request was merged or closed - while its issue
/// stayed open and assigned. Such an issue is not taken again until Hephaisto was unassigned
/// (or the issue closed) and assigned again.
/// </param>
/// <param name="Attempts">
/// What was tried for it, newest first - on <c>GET /api/workitems/{id}</c>. Null in a list, whose
/// rows do not carry them: <c>GET /api/codefixes</c> rows name their <c>workItemId</c>.
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
    string Body,
    long? StatusCommentId = null,
    string? DeclineReason = null,
    bool StillAssigned = false,
    IReadOnlyList<CodeFixAttemptView>? Attempts = null);

/// <summary>
/// What became of a work item's one attempt, for a row of the console's list: enough to say
/// where it stands and to link its page and its pull request, and nothing a model wrote.
/// </summary>
public sealed record WorkItemAttemptRef(Guid Id, CodeFixState State, string? PrUrl, int? PrNumber);

/// <summary>One row of the console's work-item list: the work item and, when it has one, its attempt.</summary>
public sealed record WorkItemWithAttempt(WorkItemView Item, WorkItemAttemptRef? Attempt);

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

    /// <summary>
    /// The list with each work item's attempt beside it, for the console: one query for the
    /// rows and one for all their attempts, never one per row.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemWithAttempt>> RowsAsync(WorkItemState? state, int limit, CancellationToken ct)
    {
        var items = await ListAsync(state, limit, ct).ConfigureAwait(false);
        var ids = items.Select(i => i.Id).ToList();

        var attempts = await db.CodeFixAttempts.AsNoTracking()
            .Where(a => a.WorkItemId != null && ids.Contains(a.WorkItemId.Value))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { WorkItemId = a.WorkItemId!.Value, a.Id, a.State, a.PrUrl, a.PrNumber })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Newest first, so the first of a work item is its latest - there is one today.
        var latest = attempts
            .GroupBy(a => a.WorkItemId)
            .ToDictionary(g => g.Key, g => g.First());

        return [.. items.Select(i => new WorkItemWithAttempt(
            i,
            latest.TryGetValue(i.Id, out var a) ? new WorkItemAttemptRef(a.Id, a.State, a.PrUrl, a.PrNumber) : null))];
    }

    public async Task<WorkItemView?> GetAsync(Guid id, CancellationToken ct) =>
        await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct).ConfigureAwait(false) is { } item
            ? View(item)
            : null;

    public static WorkItemView View(WorkItem w) => new(
        w.Id, w.Source, w.Repository, w.Number, w.Url, w.Title, w.Type, w.AuthorLogin,
        w.State, w.StateReason, w.TakenAt, w.ClosedAt, w.Body, w.StatusCommentId, w.DeclineReason, w.StillAssigned);
}
