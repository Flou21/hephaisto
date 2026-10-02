using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>
/// The changes an agent may make (#157, F4; the bulk close is #161). Listed only to a token that may write, the two
/// that need it only to an approver; <see cref="McpIncidentActions"/> checks the same again.
/// </summary>
[McpServerToolType]
public sealed class McpWriteTools(McpIncidentActions actions, McpIncidentReader reader, Hephaisto.Core.Abstractions.IClock clock)
{
    private const string IdHelp = "The incident's id, at least its first 8 hex digits, or a console URL containing it.";
    private const string OnBehalfOfHelp = "The person who asked for this, as the console names them. Required to acknowledge through a shared token; recorded as a claim, never as the actor. A person's own token acts as that person and needs none.";

    [McpServerTool(Name = "acknowledge_incident", Title = "Acknowledge", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Acknowledge an incident (ack): record that a person has seen it and is looking at it. This stops the escalation to the next person, so call it only when a person asked for it. Changes no state. Written to the audit trail as this token; the person named in onBehalfOf is recorded as claimed and unverified.")]
    public async Task<string> AcknowledgeIncidentAsync(
        ClaimsPrincipal user,
        [Description(IdHelp)] string id,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        var incident = await reader.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await actions.AcknowledgeAsync(Caller(user), incident, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "assign_incident", Title = "Assign", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Assign an incident to a person, reassign it to somebody else, or unassign it back to the pool. The assignee is always named explicitly; me works only for a token that identifies a person. Does not acknowledge for them. Written to the audit trail as this token.")]
    public async Task<string> AssignIncidentAsync(
        ClaimsPrincipal user,
        [Description(IdHelp)] string id,
        [Description("The person, as the console names them; me for this token's own person; nobody to put it back in the pool.")] string assignee,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        var incident = await reader.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await actions.AssignAsync(Caller(user), incident, assignee, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "close_incident", Title = "Close", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Authorize(Policy = McpExtensions.ApprovePolicy)]
    [Description("Close an incident: a person decided it needs no more attention. Needs the approver role. Give the reason and the resolution. A closed incident reopens by itself when its alert fires again. Written to the audit trail as this token.")]
    public async Task<string> CloseIncidentAsync(
        ClaimsPrincipal user,
        [Description(IdHelp)] string id,
        [Description("Why it needs no more attention, and what was done. Shown to people as written.")] string reason,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        var incident = await reader.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await actions.CloseAsync(Caller(user), incident, reason, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "add_alert_note_entry", Title = "Add a note entry", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Add an entry to the note of an alert name: one line of what was done this time, optionally linked to the incident. Append only; the note body itself is edited in the console. Written to the audit trail as this token and marked as relayed by an agent.")]
    public async Task<string> AddAlertNoteEntryAsync(
        ClaimsPrincipal user,
        [Description("The alertname, exactly.")] string alertName,
        [Description("One line of what was done this time.")] string text,
        [Description(IdHelp + " Optional: links the entry to the incident it was about.")] string? incidentId = null,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        Guid? incident = string.IsNullOrWhiteSpace(incidentId) ? null : await reader.ResolveAsync(incidentId, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await actions.AddNoteEntryAsync(Caller(user), alertName, text, incident, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "submit_incident_feedback", Title = "Feedback on the diagnosis", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Authorize(Policy = McpExtensions.WritePolicy)]
    [Description("Submit feedback on the diagnosis of an incident: whether it was helpful, whether the root cause was correct or wrong, whether the incident was a false positive, and a comment. Written to the audit trail as this token.")]
    public async Task<string> SubmitIncidentFeedbackAsync(
        ClaimsPrincipal user,
        [Description(IdHelp)] string id,
        [Description("Was the investigation useful overall.")] bool helpful,
        [Description("Was the root cause right; leave out when unsure.")] bool? rootCauseCorrect = null,
        [Description("The incident should never have opened.")] bool falsePositive = false,
        [Description("What was right or wrong about it.")] string? comment = null,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        var incident = await reader.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await actions.FeedbackAsync(
            Caller(user), incident, helpful, rootCauseCorrect, falsePositive, comment, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "reinvestigate_incident", Title = "Re-investigate", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Authorize(Policy = McpExtensions.ApprovePolicy)]
    [Description("Re-investigate an incident (retry): put it back on the investigation queue for another attempt. This spends the model budget (tokens and cost). Needs the approver role. Refused while an investigation is running, while the kill switch is off or when the queue is full.")]
    public async Task<string> ReinvestigateIncidentAsync(
        ClaimsPrincipal user,
        [Description(IdHelp)] string id,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        var incident = await reader.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await actions.ReinvestigateAsync(Caller(user), incident, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "close_incidents", Title = "Close many", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Authorize(Policy = McpExtensions.ApprovePolicy)]
    [Description("Close many incidents at once (bulk close): every open incident that matches the filters of search_incidents - namespace, alert name, workload, kind, severity, cluster, assignee, acknowledged, time range. Needs the approver role. Call it first without expect: nothing closes, and it returns how many would close and the oldest of them (a dry run). Then call it again with the same filters, a reason and expect set to that count; it closes them only if exactly that many still match. One audit trail entry per incident, written as this token. To close one incident, use close_incident.")]
    public async Task<string> CloseIncidentsAsync(
        ClaimsPrincipal user,
        [Description("Why these need no more attention. Shown to people as written, on every one of them. Required with expect.")] string? reason = null,
        [Description("The count the dry run returned. Leave it out for the dry run; with it, the incidents close if exactly this many still match.")] int? expect = null,
        [Description("open (the default), or one open state exactly: Investigating, AwaitingApproval, Escalated.")] string? state = null,
        [Description("critical, warning or info; several separated by commas.")] string? severity = null,
        [Description("The cluster label, exactly.")] string? cluster = null,
        [Description("The Kubernetes namespace, exactly.")] string? @namespace = null,
        [Description("The alertname, exactly, or a prefix ending in *.")] string? alertName = null,
        [Description("A Deployment, StatefulSet, DaemonSet, Job or pod name, exactly.")] string? workload = null,
        [Description("The kind of problem, as lookup_incident_filters lists them.")] string? kind = null,
        [Description("nobody (unassigned), or a person's name as the console shows it; me for a token that identifies a person.")] string? assignedTo = null,
        [Description("true for acknowledged incidents only, false for unacknowledged only.")] bool? acknowledged = null,
        [Description("Opened at or after: an ISO 8601 time, or a duration back from now such as 24h or 7d.")] string? openedAfter = null,
        [Description("Opened before: an ISO 8601 time, or a duration back from now such as 24h or 7d.")] string? openedBefore = null,
        [Description(OnBehalfOfHelp)] string? onBehalfOf = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var filter = new McpIncidentFilter
        {
            State = state,
            Severity = severity,
            Cluster = cluster,
            Namespace = @namespace,
            AlertName = alertName,
            Workload = workload,
            Kind = kind,
            AssignedTo = McpIncidentTools.ResolveAssignee(user, assignedTo),
            Acknowledged = acknowledged,
            OpenedAfter = McpQuery.Time(openedAfter, now, "openedAfter"),
            OpenedBefore = McpQuery.Time(openedBefore, now, "openedBefore"),
        };

        return McpAnswer.Of(await actions.CloseManyAsync(Caller(user), filter, reason, expect, onBehalfOf, cancellationToken).ConfigureAwait(false));
    }

    private static McpCaller Caller(ClaimsPrincipal user) =>
        McpCaller.From(user) ?? throw new McpException("This request did not come through the MCP endpoint's authentication.");
}
