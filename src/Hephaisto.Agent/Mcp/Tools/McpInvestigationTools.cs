using System.ComponentModel;
using Hephaisto.Core.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>What the investigation found and how, what was done before, what people keep, code fixes.</summary>
[McpServerToolType]
public sealed class McpInvestigationTools(McpIncidentReader reader, IClock clock)
{
    private const string IdHelp = "The incident's id, at least its first 8 hex digits, or a console URL containing it.";

    [McpServerTool(Name = "get_incident_history", Title = "Alert history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the history of an alert name or of a workload: how often it fired or happened before, the count of incidents per day or week, how each incident ended (resolved, closed, expired, escalated), how long it lasted, what was done each time (actions, code fixes, note entries) and the previous incidents with their root cause. Answers has this happened before, and is this alert recurring or flapping.")]
    public async Task<string> GetIncidentHistoryAsync(
        [Description("The alertname, exactly.")] string? alertName = null,
        [Description("A workload name, exactly; used when no alertName is given.")] string? workload = null,
        [Description(IdHelp + " Stands for its alert name, or its workload when it has none.")] string? incidentId = null,
        [Description("How far back, in days, 1 to 365. Default 90.")] int? days = null,
        [Description("day (default) or week.")] string? bucket = null,
        [Description("At most this many incidents listed, newest first, 1 to 100. The counts cover all. Default 20.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        Guid? id = string.IsNullOrWhiteSpace(incidentId) ? null : await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.HistoryAsync(
            alertName, workload, id, Math.Clamp(days ?? 90, 1, 365), bucket, McpQuery.Limit(limit, 20, 100), cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "get_incident_findings", Title = "Findings and evidence", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the findings of an incident in full: each root cause hypothesis with its category and confidence, and the evidence excerpts (log lines, events, metrics) that ground it, with the investigation step each excerpt came from, and the summary of the plan. Use it after get_incident when the diagnosis and its evidence are needed.")]
    public async Task<string> GetIncidentFindingsAsync(
        [Description(IdHelp)] string incidentId,
        [Description("One investigation's id, when the incident had several. Default: the latest with findings.")] string? investigationId = null,
        CancellationToken cancellationToken = default)
    {
        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.FindingsAsync(id, OptionalGuid(investigationId, "investigationId"), cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "get_investigation", Title = "Investigation steps", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the investigation of an incident step by step: every model turn and tool call the agent made, the tool name and arguments, a digest of each result, duration, tokens and cost, how the investigation ended or why it failed, and a reference to the raw evidence blob of each step. Paged by steps. Use it to see how a root cause was found.")]
    public async Task<string> GetInvestigationAsync(
        [Description(IdHelp)] string incidentId,
        [Description("One investigation's id. Default: the latest.")] string? investigationId = null,
        [Description("Start after this step number: the nextAfterStep of the previous page. Default 0.")] int? afterStep = null,
        [Description("At most this many steps, 1 to 50. Default 15.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.InvestigationAsync(
            id, OptionalGuid(investigationId, "investigationId"), Math.Max(0, afterStep ?? 0), McpQuery.Limit(limit, 15, 50), cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "fetch_evidence_blob", Title = "Raw evidence", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Fetch the raw evidence behind an investigation step by blob id: the full untruncated tool result, such as pod logs, Kubernetes events or a metrics query result. Paged by offset, with an optional text filter that returns only the matching lines. Evidence blobs expire after 30 days.")]
    public async Task<string> FetchEvidenceBlobAsync(
        [Description("The blobId from get_incident_findings or get_investigation.")] string blobId,
        [Description("Start at this character: the nextOffset of the previous page. Default 0.")] int? offset = null,
        [Description("At most this many characters, 1 to 20000. Default 20000.")] int? length = null,
        [Description("Only the lines containing this text, case-insensitive.")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var id = OptionalGuid(blobId, "blobId") ?? throw new McpException("Name the blobId.");

        return McpAnswer.Of(await reader.BlobAsync(id, offset ?? 0, length ?? 20_000, filter, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "get_incident_actions", Title = "Proposed actions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the remediation actions proposed or executed for an incident: action type, target, risk, the policy verdict and its reasons, dry run or real, who approved it, outcome, error and the verification checks that ran afterwards. Read only: this server cannot approve, deny or execute an action.")]
    public async Task<string> GetIncidentActionsAsync(
        [Description(IdHelp)] string incidentId,
        CancellationToken cancellationToken = default)
    {
        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.ActionsAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "get_alert_note", Title = "Alert note", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the note that people keep for an alert name: what the alert means, what the operators learned about it, and the entries of what was done the last times it fired, with author and date. The runbook knowledge of the team for one alert. Paged entries.")]
    public async Task<string> GetAlertNoteAsync(
        [Description("The alertname, exactly.")] string alertName,
        [Description("At most this many entries, newest first, 1 to 100. Default 20.")] int? limit = null,
        [Description("The nextCursor of the previous page.")] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await reader.AlertNoteAsync(alertName, McpQuery.Limit(limit, 20, 100), cursor, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "list_code_fixes", Title = "Code fixes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List code fixes across incidents and GitHub issues, newest first: the attempts of the coder to plan and implement a fix in a repository, filtered by state (planning, plan ready, implementing, pull request opened, failed, denied), repository, workload, incident and time range. Returns state, repository, branch, summary, the incident or the issue it is for, pull request URL and cost. Paged. Answers which pull requests (PR) were opened and which plans wait for a person.")]
    public async Task<string> ListCodeFixesAsync(
        [Description("Eligible, Planning, PlanReady, Implementing, PrOpened, Failed, Denied, Expired or Cancelled; several separated by commas.")] string? state = null,
        [Description("Part of the repository URL.")] string? repository = null,
        [Description("The workload, exactly.")] string? workload = null,
        [Description(IdHelp + " Leaves out the attempts that are for an issue.")] string? incidentId = null,
        [Description("Created at or after. An ISO 8601 time, or a duration back from now: 24h, 7d.")] string? since = null,
        [Description("At most this many, 1 to 100. Default 20.")] int? limit = null,
        [Description("The nextCursor of the previous page.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        Guid? id = string.IsNullOrWhiteSpace(incidentId) ? null : await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.CodeFixesAsync(
            state, repository, workload, id, McpQuery.Time(since, clock.UtcNow, "since"), McpQuery.Limit(limit, 20, 100), cursor, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "get_code_fix", Title = "One code fix", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get one code fix attempt in full by attempt id or incident id, for an incident or for a GitHub issue: plan summary, root cause, files to change, steps, notes, what could not be verified, pull request URL, number and description, whether build and tests passed, deviations from the plan, costs, timestamps, who decided on it, through what, and why it failed. Read only: a plan is answered by a person, in the console or on the issue.")]
    public async Task<string> GetCodeFixAsync(
        [Description("One attempt's id, from list_code_fixes or get_work_item. The only way to an attempt that is for an issue.")] string? attemptId = null,
        [Description(IdHelp + " Returns its attempts, the latest in full, or why none was started.")] string? incidentId = null,
        CancellationToken cancellationToken = default)
    {
        if (OptionalGuid(attemptId, "attemptId") is { } attempt)
        {
            return McpAnswer.Of(await reader.CodeFixAsync(attempt, cancellationToken).ConfigureAwait(false));
        }

        if (string.IsNullOrWhiteSpace(incidentId))
        {
            throw new McpException("Name an attemptId or an incidentId.");
        }

        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.IncidentCodeFixesAsync(id, cancellationToken).ConfigureAwait(false));
    }

    private static Guid? OptionalGuid(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Guid.TryParse(value.Trim(), out var id)
                ? id
                : throw new McpException($"{name} '{McpQuery.Echo(value)}' is not an id.");
}
