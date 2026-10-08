using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Mcp.Answers;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace Hephaisto.Agent.Mcp;

// Work items (v0.14.0): the GitHub issues Hephaisto was handed, and what became of each. Reads
// only, like everything in this class - a plan is answered by a person, on the issue or in the
// console. An issue's title, its text and its labels are somebody else's words and go out in the
// envelope; its reference (owner/repo#12) is a configured repository and a number.
public sealed partial class McpIncidentReader
{
    private const int IssueBodyChars = 4_000;

    public async Task<WorkItemPage> WorkItemsAsync(
        string? state,
        string? repository,
        DateTimeOffset? since,
        int limit,
        string? cursor,
        CancellationToken ct)
    {
        var query = db.WorkItems.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(state))
        {
            var states = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<WorkItemState>(s, true, out var p) && Enum.IsDefined(p) && !int.TryParse(s, out _)
                    ? p
                    : throw new McpException($"state '{McpQuery.Echo(s)}' is not one of: {string.Join(", ", Enum.GetNames<WorkItemState>())}."))
                .ToList();
            query = query.Where(w => states.Contains(w.State));
        }

        if (!string.IsNullOrWhiteSpace(repository))
        {
            var r = repository.Trim();
            query = query.Where(w => w.Repository.Contains(r));
        }

        if (since is { } s2)
        {
            query = query.Where(w => w.TakenAt >= s2);
        }

        var key = $"workitems|{state}|{repository}|{since?.UtcTicks}";

        if (McpQuery.ReadKeyset(cursor, key) is { } after)
        {
            query = query.Where(w => EF.Functions.LessThan(
                ValueTuple.Create(w.TakenAt, w.Id),
                ValueTuple.Create(after.At, after.Id)));
        }

        var rows = await query
            .OrderByDescending(w => w.TakenAt)
            .ThenByDescending(w => w.Id)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var page = rows.Take(limit).ToList();
        var attempts = await AttemptsOfAsync([.. page.Select(w => w.Id)], ct).ConfigureAwait(false);

        return new WorkItemPage
        {
            WorkItems = [.. page.Select(w => WorkItemRowOf(w, attempts.GetValueOrDefault(w.Id)?.FirstOrDefault()))],
            Enabled = github.CurrentValue.Enabled,
            NextCursor = rows.Count > limit ? McpQuery.Cursor(key, page[^1].TakenAt, page[^1].Id) : null,
        };
    }

    /// <summary>By its id, or by the repository and the issue's number: the one that is taken, else the newest.</summary>
    public async Task<WorkItemDetail> WorkItemAsync(Guid? id, string? repository, int? number, CancellationToken ct)
    {
        WorkItem? item;

        if (id is { } workItemId)
        {
            item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct).ConfigureAwait(false)
                ?? throw new McpException($"No work item {workItemId}.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(repository) || number is not { } n)
            {
                throw new McpException("Name the work item's id, or a repository (owner/repo) and the issue's number.");
            }

            var r = repository.Trim();

            // An issue handed over again after a cancel is a new work item: the one Hephaisto
            // holds now comes first, then the latest that ended.
            item = await db.WorkItems.AsNoTracking()
                .Where(w => w.Repository == r && w.Number == n)
                .OrderBy(w => w.State == WorkItemState.Taken ? 0 : 1)
                .ThenByDescending(w => w.TakenAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? throw new McpException(
                    $"No work item for {McpQuery.Echo(r)}#{n}. An issue is a work item once it is assigned to Hephaisto's account "
                    + "in a repository this install lists; list_work_items shows what was taken.");
        }

        var attempts = (await AttemptsOfAsync([item.Id], ct).ConfigureAwait(false)).GetValueOrDefault(item.Id) ?? [];

        List<string> next = [.. attempts.Select(a => $"get_code_fix {{\"attemptId\":\"{a.Id}\"}} - the plan in full, the pull request and who decided")];

        if (attempts.Count == 0)
        {
            next.Add("list_code_fixes {\"state\":\"PlanReady\"} - the plans that wait for a person");
        }

        return new WorkItemDetail
        {
            WorkItem = WorkItemRowOf(item, attempts.FirstOrDefault()),
            Type = McpText.NameOrNull(item.Type),
            Author = McpText.Name(item.AuthorLogin),
            Labels = [.. item.Labels.Select(l => McpText.Name(l))],
            Body = McpText.UntrustedOrNull(item.Body, IssueBodyChars),
            DeclineReason = McpText.UntrustedOrNull(item.DeclineReason, 1_000),
            StillAssigned = item.StillAssigned,
            CodeFixes = [.. attempts.Select(CodeFixRowOf)],
            Note = "Read only. A plan is approved or denied by a person - in a comment on the issue, or in the console - never here.",
            Next = next,
        };
    }

    /// <summary>The attempts of several work items in one query, newest first within each.</summary>
    private async Task<Dictionary<Guid, List<CodeFixAttemptView>>> AttemptsOfAsync(List<Guid> workItemIds, CancellationToken ct)
    {
        if (workItemIds.Count == 0)
        {
            return [];
        }

        var attempts = await db.CodeFixAttempts.AsNoTracking()
            .Include(a => a.WorkItem)
            .Where(a => a.WorkItemId != null && workItemIds.Contains(a.WorkItemId.Value))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return attempts
            .GroupBy(a => a.WorkItemId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(CodeFixQueries.View).ToList());
    }

    private static WorkItemRow WorkItemRowOf(WorkItem w, CodeFixAttemptView? attempt) => new()
    {
        Id = w.Id,
        Issue = $"{w.Repository}#{w.Number}",
        Repository = McpText.Name(w.Repository),
        Number = w.Number,
        Url = McpText.NameOrNull(w.Url),
        Title = McpText.Untrusted(w.Title, 300),
        State = w.State,
        StateReason = McpText.UntrustedOrNull(w.StateReason, 500),
        TakenAt = w.TakenAt,
        ClosedAt = w.ClosedAt,
        CodeFix = attempt is null
            ? null
            : new CodeFixSummary { Id = attempt.Id, State = attempt.State, PullRequestUrl = McpText.NameOrNull(attempt.PrUrl) },
    };
}
