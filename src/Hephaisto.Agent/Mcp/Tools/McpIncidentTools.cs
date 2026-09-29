using System.ComponentModel;
using System.Security.Claims;
using Hephaisto.Core.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>Finding incidents: list and search, count, one incident, the values a filter can take.</summary>
[McpServerToolType]
public sealed class McpIncidentTools(McpIncidentReader reader, IClock clock)
{
    private const string StateHelp = "open (what an on-call person looks at), closed (ended: resolved, closed or expired), any (the default), or one exact state: Investigating, AwaitingApproval, Escalated, Resolved, Closed, Expired.";
    private const string AssignedHelp = "me (the person this token identifies; refused for a shared token), nobody (unassigned), or a person's name as the console shows it.";
    private const string TimeHelp = "An ISO 8601 time, or a duration back from now: 30m, 24h, 7d, 2w.";

    [McpServerTool(Name = "search_incidents", Title = "Search incidents", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Search and list incidents, newest first. Answers what the latest open incidents are, what the latest closed incidents are, what my incidents are (assigned to a person), and which incidents match a free text query. Filter by state (open, closed, escalated, resolved), severity (critical, warning, info), cluster, namespace, alert name, workload, kind, assignee, acknowledged or unacknowledged, and a time range. Returns one compact row per incident: id, title, severity, state, alert name, namespace, workload, who it is assigned to, when it opened and ended. Paged with a cursor. Start here for any question about which incidents exist. Through a shared gateway token, me means nobody: for my incidents, ask the person for their name and pass it as assignedTo.")]
    public async Task<string> SearchIncidentsAsync(
        ClaimsPrincipal user,
        [Description(StateHelp)] string? state = null,
        [Description("critical, warning or info; several separated by commas.")] string? severity = null,
        [Description("The cluster label, exactly.")] string? cluster = null,
        [Description("The Kubernetes namespace, exactly.")] string? @namespace = null,
        [Description("The alertname, exactly, or a prefix ending in *.")] string? alertName = null,
        [Description("A Deployment, StatefulSet, DaemonSet, Job or pod name, exactly.")] string? workload = null,
        [Description("The kind of problem: CrashLoopBackOff, OomKilled, ImagePullBackOff, Unschedulable, ConfigError, HighErrorRate, HighLatency, TargetDown and the others lookup_incident_filters lists.")] string? kind = null,
        [Description(AssignedHelp)] string? assignedTo = null,
        [Description("true for acknowledged incidents only, false for unacknowledged only.")] bool? acknowledged = null,
        [Description("Opened at or after. " + TimeHelp)] string? openedAfter = null,
        [Description("Opened before. " + TimeHelp)] string? openedBefore = null,
        [Description("Free text, matched against what each incident was about; ranks by relevance instead of time.")] string? text = null,
        [Description("At most this many rows, 1 to 100. Default 20.")] int? limit = null,
        [Description("The nextCursor of the previous page, to page on. Only with the same filters.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Filter(user, state, severity, cluster, @namespace, alertName, workload, kind, assignedTo, acknowledged, openedAfter, openedBefore);
        var page = await reader.SearchAsync(filter, text, McpQuery.Limit(limit, 20, 100), cursor, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(page);
    }

    [McpServerTool(Name = "get_incident", Title = "Get an incident", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get one incident by id, short id or console URL: the overview to read first when an incident just happened and somebody asks to check it. Returns title, severity, state, workload, alert name and labels, who acknowledged it and who it is assigned to, a summary of its signals, the primary finding of the investigation with the root cause hypothesis and confidence, proposed actions, code fixes, notifications sent, and which tool to call next for the details.")]
    public async Task<string> GetIncidentAsync(
        [Description("The incident's id, at least its first 8 hex digits, or a console URL containing it.")] string id,
        CancellationToken cancellationToken = default)
    {
        var incident = await reader.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(await reader.OverviewAsync(incident, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "count_incidents", Title = "Count incidents", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Count incidents grouped by severity, state, namespace, alert name, assignee, kind, cluster, workload or day, with the latest incidents of each group as examples. Answers how many incidents are open, what the latest open incidents grouped by severity are, which alerts fire most and which namespaces are noisiest. Takes the filters of search_incidents: state (open, closed), severity, cluster, namespace, alert name, workload, assigned to and time range.")]
    public async Task<string> CountIncidentsAsync(
        ClaimsPrincipal user,
        [Description("severity (default), state, namespace, alertName, assignee, kind, cluster, workload, day or week.")] string? groupBy = null,
        [Description(StateHelp)] string? state = null,
        [Description("critical, warning or info; several separated by commas.")] string? severity = null,
        [Description("The cluster label, exactly.")] string? cluster = null,
        [Description("The Kubernetes namespace, exactly.")] string? @namespace = null,
        [Description("The alertname, exactly, or a prefix ending in *.")] string? alertName = null,
        [Description("A workload name, exactly.")] string? workload = null,
        [Description("The kind of problem.")] string? kind = null,
        [Description(AssignedHelp)] string? assignedTo = null,
        [Description("Opened at or after. " + TimeHelp)] string? openedAfter = null,
        [Description("Opened before. " + TimeHelp)] string? openedBefore = null,
        [Description("How many of the newest incidents to show per group, 0 to 10. Default 3.")] int? examples = null,
        [Description("At most this many groups, 1 to 50. Default 20.")] int? maxGroups = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Filter(user, state, severity, cluster, @namespace, alertName, workload, kind, assignedTo, null, openedAfter, openedBefore);
        var count = await reader.CountAsync(
            filter,
            groupBy ?? "severity",
            Math.Clamp(examples ?? 3, 0, 10),
            McpQuery.Limit(maxGroups, 20, 50),
            cancellationToken).ConfigureAwait(false);

        return McpAnswer.Of(count);
    }

    [McpServerTool(Name = "lookup_incident_filters", Title = "Look up filter values", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Look up the values that exist for a filter: the list of namespaces, alert names, clusters, workloads, assignees, kinds and code fix repositories seen in incidents, each with its count of open and total incidents. Use it to find the exact spelling of an alert name or a namespace before a search.")]
    public async Task<string> LookupIncidentFiltersAsync(
        [Description("namespace, alertName, cluster, workload, assignee, kind or repository. Leave out for all of them.")] string? field = null,
        [Description("At most this many values per field, 1 to 200. Default 50.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        McpAnswer.Of(await reader.FiltersAsync(field, McpQuery.Limit(limit, 50, 200), cancellationToken).ConfigureAwait(false));

    private McpIncidentFilter Filter(
        ClaimsPrincipal user,
        string? state,
        string? severity,
        string? cluster,
        string? @namespace,
        string? alertName,
        string? workload,
        string? kind,
        string? assignedTo,
        bool? acknowledged,
        string? openedAfter,
        string? openedBefore)
    {
        var now = clock.UtcNow;

        return new McpIncidentFilter
        {
            State = state,
            Severity = severity,
            Cluster = cluster,
            Namespace = @namespace,
            AlertName = alertName,
            Workload = workload,
            Kind = kind,
            AssignedTo = ResolveAssignee(user, assignedTo),
            Acknowledged = acknowledged,
            OpenedAfter = McpQuery.Time(openedAfter, now, "openedAfter"),
            OpenedBefore = McpQuery.Time(openedBefore, now, "openedBefore"),
        };
    }

    /// <summary>
    /// <c>me</c> is the caller's person, or refused: a shared token is sent for many people, and
    /// an empty "your incidents" would be reported to whoever asked as "you have none".
    /// </summary>
    internal static string? ResolveAssignee(ClaimsPrincipal user, string? assignedTo)
    {
        if (string.IsNullOrWhiteSpace(assignedTo))
        {
            return null;
        }

        if (!string.Equals(assignedTo.Trim(), "me", StringComparison.OrdinalIgnoreCase))
        {
            return assignedTo.Trim();
        }

        var caller = McpCaller.From(user) ?? throw new McpException("This request did not come through the MCP endpoint's authentication.");

        return caller.Me ?? throw new McpException(
            $"assignedTo 'me' cannot be answered: this is a shared token ({caller.Actor}), sent by a gateway for many "
            + "people, so it does not know who you are. Ask with the person's name as the console shows it, "
            + "for example assignedTo: \"jane\" - lookup_incident_filters lists the assignees.");
    }
}
