using System.ComponentModel;
using Hephaisto.Core.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>
/// Work items (v0.14.0): the GitHub issues Hephaisto was handed, and what became of each.
/// </summary>
/// <remarks>
/// Two reads and no write. A plan for an issue is answered by a person - in a comment on the
/// issue, or in the console - and a tool here that could would be the door
/// <c>McpToolCatalogueTests</c> keeps shut for an incident's plan.
/// </remarks>
[McpServerToolType]
public sealed class McpWorkItemTools(McpIncidentReader reader, IClock clock)
{
    [McpServerTool(Name = "list_work_items", Title = "Work items", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the work items Hephaisto was handed, newest first: the GitHub issues assigned to its account, each with repository and issue number, title, state (taken, done, cancelled), the reason it ended, when it was taken and when it ended, and the code fix attempt planned for it. Filtered by state, repository and time range. Paged. Answers which issues Hephaisto is working on and what became of an issue.")]
    public async Task<string> ListWorkItemsAsync(
        [Description("Taken, Done or Cancelled; several separated by commas. Default: every state.")] string? state = null,
        [Description("Part of the repository's name, owner/repo.")] string? repository = null,
        [Description("Taken at or after. An ISO 8601 time, or a duration back from now: 24h, 7d.")] string? since = null,
        [Description("At most this many, 1 to 100. Default 20.")] int? limit = null,
        [Description("The nextCursor of the previous page.")] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await reader.WorkItemsAsync(
            state, repository, McpQuery.Time(since, clock.UtcNow, "since"), McpQuery.Limit(limit, 20, 100), cursor, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "get_work_item", Title = "One work item", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get one work item in full by its id, or by repository and issue number: the GitHub issue handed to Hephaisto with its title, author, labels, text and URL, what became of it - its state and why it ended - why no plan was started when none was, and its code fix attempt with plan summary and pull request. Read only: a plan is answered by a person, on the issue or in the console.")]
    public async Task<string> GetWorkItemAsync(
        [Description("The work item's id, from list_work_items or a code fix's workItemId.")] string? id = null,
        [Description("The repository, owner/repo, exactly; with number, instead of an id.")] string? repository = null,
        [Description("The issue's number in that repository.")] int? number = null,
        CancellationToken cancellationToken = default)
    {
        Guid? workItemId = string.IsNullOrWhiteSpace(id)
            ? null
            : Guid.TryParse(id.Trim(), out var parsed)
                ? parsed
                : throw new McpException($"id '{McpQuery.Echo(id)}' is not an id.");

        return McpAnswer.Of(await reader.WorkItemAsync(workItemId, repository, number, cancellationToken).ConfigureAwait(false));
    }
}
