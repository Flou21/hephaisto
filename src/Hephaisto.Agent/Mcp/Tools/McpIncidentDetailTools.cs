using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>One incident in depth: its signals, its timeline, who was told.</summary>
[McpServerToolType]
public sealed class McpIncidentDetailTools(McpIncidentReader reader)
{
    private const string IdHelp = "The incident's id, at least its first 8 hex digits, or a console URL containing it.";

    [McpServerTool(Name = "get_incident_signals", Title = "Incident signals", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the signals and alerts of one incident with their labels, annotations, severity, status (firing or resolved), first and last seen time and count. The raw alert details behind an incident: alertname, description, summary, runbook link and every label that Alertmanager or the Kubernetes watcher sent. Paged.")]
    public async Task<string> GetIncidentSignalsAsync(
        [Description(IdHelp)] string incidentId,
        [Description("Firing or Resolved, to see only those.")] string? status = null,
        [Description("At most this many signals, 1 to 100. Default 25.")] int? limit = null,
        [Description("The nextCursor of the previous page.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.SignalsAsync(id, status, McpQuery.Limit(limit, 25, 100), cursor, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "get_incident_timeline", Title = "Incident timeline", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the timeline and audit trail of an incident in order: every state transition with its reason, and every audit event with who did it, when, and whether it came from the console, from Teams or from an agent through MCP. Answers who acknowledged, assigned, closed or reopened an incident, and what happened when. Paged.")]
    public async Task<string> GetIncidentTimelineAsync(
        [Description(IdHelp)] string incidentId,
        [Description("At most this many entries, 1 to 200. Default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.TimelineAsync(id, McpQuery.Limit(limit, 50, 200), cursor, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "list_incident_notifications", Title = "Incident notifications", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the notifications sent for an incident: who was told, notified or paged and when, through which channel (Teams, webhook), for which event, to which recipients and routes, at which escalation step, and whether the delivery succeeded, is pending or failed with an error.")]
    public async Task<string> ListIncidentNotificationsAsync(
        [Description(IdHelp)] string incidentId,
        [Description("At most this many deliveries, 1 to 200. Default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var id = await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.NotificationsAsync(id, McpQuery.Limit(limit, 50, 200), cursor, cancellationToken).ConfigureAwait(false));
    }
}
