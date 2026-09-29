using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Mcp.Answers;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Web;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Safety;
using Hephaisto.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// Everything the MCP tools may read, and nothing they may not.
/// </summary>
/// <remarks>
/// <para>
/// The tools depend on this and on <see cref="McpIncidentActions"/>, never on
/// <see cref="IncidentQueries"/> or <see cref="CodeFixCoordinator"/> directly: those also approve
/// actions, decide code-fix plans and re-arm the mode, and a tool that could reach them is one
/// careless line from being a door that must stay shut. A unit test holds the tools to these two.
/// </para>
/// <para>
/// Its own queries rather than the console's: an MCP answer is a different projection - every
/// string enveloped or checked as a name, pages by cursor - and the console's are tuned for the
/// pages that call them. Scoped: one context per request, which is one per tool call.
/// </para>
/// </remarks>
public sealed partial class McpIncidentReader(
    HephaistoDbContext db,
    IncidentQueries incidents,
    CodeFixQueries codeFixes,
    ConnectionHealthCache connections,
    Pipeline.InvestigationTracker tracker,
    IOptionsMonitor<NotificationOptions> notifications)
{
    /// <summary>How many rows a count reads at most. Past this it says so.</summary>
    public const int CountWindow = 5_000;

    private const int DescriptionChars = 4_000;
    private const int AnnotationChars = 4_000;

    // -------------------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------------------

    /// <summary>The agent's status as <c>/api/status</c> has it, with the connections and the build.</summary>
    public async Task<McpStatus> StatusAsync(CancellationToken ct)
    {
        var status = await incidents.GetStatusAsync(ct).ConfigureAwait(false);
        var counts = await codeFixes.CountsAsync(ct).ConfigureAwait(false);

        return new McpStatus(
            status,
            counts,
            [.. connections.Current.Select(c => new McpConnection(c.Name, c.State, c.Detail, c.CheckedAt))],
            BuildInfo.Version,
            BuildInfo.Commit);
    }

    // -------------------------------------------------------------------------------------
    // Lists
    // -------------------------------------------------------------------------------------

    /// <summary>Incidents newest first, paged by cursor; or, with text, ranked by the search index.</summary>
    public async Task<IncidentPage> SearchAsync(McpIncidentFilter filter, string? text, int limit, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var key = filter.Key(text);

        if (!string.IsNullOrWhiteSpace(text))
        {
            return await TextSearchAsync(filter, text, limit, ct).ConfigureAwait(false);
        }

        var query = filter.Apply(db.Incidents.AsNoTracking());

        if (McpQuery.ReadKeyset(cursor, key) is { } after)
        {
            query = query.Where(i => EF.Functions.LessThan(
                ValueTuple.Create(i.OpenedAt, i.Id),
                ValueTuple.Create(after.At, after.Id)));
        }

        var rows = await query
            .OrderByDescending(i => i.OpenedAt)
            .ThenByDescending(i => i.Id)
            .Take(limit + 1)
            .Select(RowProjection)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var page = rows.Take(limit).ToList();

        return new IncidentPage
        {
            Incidents = [.. page.Select(Row)],
            NextCursor = rows.Count > limit ? McpQuery.Cursor(key, page[^1].OpenedAt, page[^1].Id) : null,
        };
    }

    private async Task<IncidentPage> TextSearchAsync(McpIncidentFilter filter, string text, int limit, CancellationToken ct)
    {
        var result = await incidents.SearchAsync(text, new SearchFilter(), Math.Min(100, limit * 3), ct).ConfigureAwait(false);
        var ranked = result.Hits.Select(h => h.IncidentId).Distinct().ToList();

        var rows = await filter.Apply(db.Incidents.AsNoTracking())
            .Where(i => ranked.Contains(i.Id))
            .Select(RowProjection)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byId = rows.ToDictionary(r => r.Id);

        return new IncidentPage
        {
            Incidents = [.. ranked.Where(byId.ContainsKey).Take(limit).Select(id => Row(byId[id]))],
            Note = "Ranked by relevance to the text (full text, similarity and trigrams over each incident's digest), "
                + "not by time, and not paged. Leave text out to list newest first.",
        };
    }

    /// <summary>Counts grouped by one dimension, each group with its newest incidents.</summary>
    public async Task<IncidentCount> CountAsync(McpIncidentFilter filter, string groupBy, int examples, int maxGroups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var dimension = (groupBy ?? "severity").Trim().ToLowerInvariant();

        Func<RowData, string> keyOf = dimension switch
        {
            "severity" => r => r.Severity.ToString(),
            "state" => r => r.State.ToString(),
            "namespace" => r => r.Namespace,
            "alertname" or "alert" or "alert_name" => r => r.AlertName ?? "(no alert name)",
            "assignee" or "assignedto" => r => r.AssignedTo ?? "(nobody)",
            "kind" => r => r.Kind.ToString(),
            "cluster" => r => string.IsNullOrEmpty(r.Cluster) ? "(unknown)" : r.Cluster,
            "workload" => r => r.OwnerName ?? r.TargetName,
            "day" => r => r.OpenedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "week" => r => $"{ISOWeek.GetYear(r.OpenedAt.UtcDateTime)}-W{ISOWeek.GetWeekOfYear(r.OpenedAt.UtcDateTime):00}",
            _ => throw new McpException(
                $"groupBy '{McpQuery.Echo(groupBy)}' is not one of: severity, state, namespace, alertName, assignee, kind, cluster, workload, day, week."),
        };

        var query = filter.Apply(db.Incidents.AsNoTracking());
        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var rows = await query
            .OrderByDescending(i => i.OpenedAt)
            .ThenByDescending(i => i.Id)
            .Take(CountWindow)
            .Select(RowProjection)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var groups = rows.GroupBy(keyOf).Select(g => (Key: g.Key, Rows: g.ToList()));

        groups = dimension switch
        {
            "severity" => groups.OrderByDescending(g => Enum.Parse<Severity>(g.Key)),
            "day" or "week" => groups.OrderByDescending(g => g.Key, StringComparer.Ordinal),
            _ => groups.OrderByDescending(g => g.Rows.Count).ThenBy(g => g.Key, StringComparer.Ordinal),
        };

        var all = groups.ToList();

        return new IncidentCount
        {
            Total = total,
            GroupBy = dimension,
            Groups = [.. all.Take(maxGroups).Select(g => new IncidentCountGroup
            {
                Key = McpText.Name(g.Key),
                Count = g.Rows.Count,
                Open = g.Rows.Count(r => HephaistoDbContext.OpenStates.Contains(r.State)),
                Examples = [.. g.Rows.Take(examples).Select(Row)],
            })],
            Note = (total > CountWindow
                    ? $"Counted the newest {CountWindow} of {total} incidents; narrow the filter or the time range for exact counts. "
                    : string.Empty)
                + (all.Count > maxGroups ? $"{all.Count - maxGroups} smaller group(s) left out." : string.Empty) is { Length: > 0 } note
                ? note.Trim()
                : null,
        };
    }

    // -------------------------------------------------------------------------------------
    // One incident
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The incident an id, a hex prefix of one, or a console URL names. An unknown or ambiguous
    /// reference is an error the model can read - never a guess.
    /// </summary>
    public async Task<Guid> ResolveAsync(string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new McpException("Name the incident: its id, the first 8 or more hex digits of it, or its console URL.");
        }

        var r = reference.Trim();

        if (GuidIn().Match(r) is { Success: true } m && Guid.TryParse(m.Value, out var full))
        {
            return await db.Incidents.AsNoTracking().AnyAsync(i => i.Id == full, ct).ConfigureAwait(false)
                ? full
                : throw new McpException($"No incident has the id {full}.");
        }

        var hex = r.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        if (!HexPrefix().IsMatch(hex))
        {
            throw new McpException($"'{McpQuery.Echo(reference)}' is not an incident id, a prefix of one (8 to 32 hex digits) or a console URL.");
        }

        var prefix = Hyphenate(hex);

        var candidates = await db.Incidents.AsNoTracking()
            .Where(i => i.Id.ToString().StartsWith(prefix))
            .OrderByDescending(i => i.OpenedAt)
            .Take(6)
            .Select(i => new { i.Id, i.Title, i.OpenedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return candidates.Count switch
        {
            0 => throw new McpException($"No incident id starts with {prefix}."),
            1 => candidates[0].Id,
            _ => throw new McpException(
                $"{(candidates.Count > 5 ? "More than five" : candidates.Count.ToString(CultureInfo.InvariantCulture))} incidents have ids starting with {prefix} "
                + "(ids share their first digits when they opened within the same minute). Use the full id. Candidates: "
                + string.Join("; ", candidates.Take(5).Select(c =>
                    $"{c.Id} opened {c.OpenedAt:yyyy-MM-dd HH:mm:ss}Z {UntrustedText.Wrap(c.Title, 120)}"))),
        };
    }

    public async Task<IncidentOverview> OverviewAsync(Guid id, CancellationToken ct)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false)
            ?? throw new McpException($"No incident has the id {id}.");

        var signals = await db.Signals.AsNoTracking()
            .Where(s => s.IncidentId == id)
            .GroupBy(s => s.IncidentId)
            .Select(g => new
            {
                Count = g.Count(),
                Firing = g.Count(s => s.Status == SignalStatus.Firing),
                Resolved = g.Count(s => s.Status == SignalStatus.Resolved),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var first = await db.Signals.AsNoTracking()
            .Where(s => s.IncidentId == id)
            .OrderBy(s => s.FirstSeen)
            .Select(s => new { s.Annotations, s.Message })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var investigations = await db.Investigations.AsNoTracking()
            .Where(v => v.IncidentId == id)
            .OrderByDescending(v => v.StartedAt)
            .Select(v => new
            {
                v.Id,
                v.StartedAt,
                v.CompletedAt,
                v.StepsUsed,
                v.CostUsd,
                Summary = v.Plan != null ? v.Plan.Summary : null,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var primary = await db.Findings.AsNoTracking()
            .Where(f => f.Investigation!.IncidentId == id && f.IsPrimary)
            .OrderByDescending(f => f.Investigation!.StartedAt)
            .Select(f => new { f.Hypothesis, f.Category, f.Confidence, Evidence = f.Evidence.Count })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var actions = await db.AgentActions.AsNoTracking()
            .Where(a => a.IncidentId == id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fixes = await db.CodeFixAttempts.AsNoTracking()
            .Where(a => a.IncidentId == id)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.Id, a.State, a.PrUrl })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var deliveries = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.IncidentId == id)
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latest = investigations.FirstOrDefault();
        var target = incident.Target;
        var arg = $"{{\"incidentId\":\"{id}\"}}";

        List<string> next =
        [
            $"get_incident_timeline {arg} - who did what, when",
            $"get_incident_signals {arg} - every alert with its labels and annotations",
        ];

        if (primary is not null)
        {
            next.Add($"get_incident_findings {arg} - the finding with the evidence behind it");
        }

        if (latest is not null)
        {
            next.Add($"get_investigation {arg} - every step the agent took");
        }

        next.Add($"get_incident_history {arg} - how often this alert fired before, and how each ended");

        if (actions.Count > 0)
        {
            next.Add($"get_incident_actions {arg} - the proposed actions and the policy verdicts");
        }

        next.Add($"get_code_fix {arg} - code fixes, or why none was started");
        next.Add($"list_incident_notifications {arg} - who was told");

        return new IncidentOverview
        {
            Incident = new IncidentDetail
            {
                Id = incident.Id,
                ShortId = ShortId(incident.Id),
                Title = McpText.Untrusted(incident.Title, 500),
                Severity = incident.Severity,
                State = incident.State,
                Kind = incident.Kind,
                EscalationReason = incident.EscalationReason == EscalationReason.None ? null : incident.EscalationReason,
                AlertName = McpText.NameOrNull(incident.AlertName),
                Cluster = McpText.NameOrNull(target.Cluster),
                Namespace = McpText.Name(target.Namespace),
                Workload = McpText.Name(target.OwnerName ?? target.Name),
                WorkloadKind = McpText.NameOrNull(target.OwnerKind ?? target.Kind),
                Owner = target.OwnerName is null ? null : McpText.Name($"{target.Kind}/{target.Name}"),
                Labels = McpMap.From(incident.Labels),
                Description = McpText.UntrustedOrNull(Description(first?.Annotations, first?.Message), DescriptionChars),
                OpenedAt = incident.OpenedAt,
                LastSignalAt = incident.LastSignalAt,
                EndedAt = incident.ClosedAt ?? incident.ResolvedAt,
                ReopenedAt = incident.ReopenedAt,
                AcknowledgedBy = McpText.NameOrNull(incident.AcknowledgedBy),
                AcknowledgedAt = incident.AcknowledgedAt,
                AssignedTo = McpText.NameOrNull(incident.AssignedTo),
                AssignedBy = McpText.NameOrNull(incident.AssignedBy),
                ClosedBy = McpText.NameOrNull(incident.ClosedBy),
                Resolution = McpText.UntrustedOrNull(incident.Resolution, 1_000),
                Signals = new SignalSummary
                {
                    Count = signals?.Count ?? 0,
                    Firing = signals?.Firing ?? 0,
                    Resolved = signals?.Resolved ?? 0,
                },
                Investigation = latest is null
                    ? null
                    : new InvestigationSummary
                    {
                        Id = latest.Id,
                        Investigations = investigations.Count,
                        StartedAt = latest.StartedAt,
                        EndedAt = latest.CompletedAt,
                        Steps = latest.StepsUsed,
                        CostUsd = latest.CostUsd,
                        Summary = McpText.UntrustedOrNull(latest.Summary, 1_500),
                        Running = tracker.For(id) is not null,
                    },
                PrimaryFinding = primary is null
                    ? null
                    : new FindingSummary
                    {
                        Hypothesis = McpText.Untrusted(primary.Hypothesis, 1_500),
                        Category = McpText.Name(primary.Category),
                        Confidence = primary.Confidence,
                        Evidence = primary.Evidence,
                    },
                Actions = [.. actions.Select(a => new ActionSummary
                {
                    Id = a.Id,
                    Type = a.Type,
                    Target = McpText.Name($"{a.Target.Namespace}/{a.Target.Kind}/{a.Target.Name}"),
                    State = a.State,
                    Decision = a.Decision,
                    Risk = a.Risk,
                })],
                CodeFixes = [.. fixes.Select(f => new CodeFixSummary { Id = f.Id, State = f.State, PullRequestUrl = McpText.NameOrNull(f.PrUrl) })],
                Notifications = new NotificationSummary
                {
                    Delivered = deliveries.Where(d => d.Status == DeliveryStatus.Delivered).Sum(d => d.Count),
                    Failed = deliveries.Where(d => d.Status == DeliveryStatus.Failed).Sum(d => d.Count),
                    Pending = deliveries.Where(d => d.Status == DeliveryStatus.Pending).Sum(d => d.Count),
                    Suppressed = deliveries.Where(d => d.Status == DeliveryStatus.Suppressed).Sum(d => d.Count),
                },
                Url = NotificationLinks.Incident(notifications.CurrentValue.BaseUrl, id),
            },
            Next = next,
        };
    }

    public async Task<SignalPage> SignalsAsync(Guid id, string? status, int limit, string? cursor, CancellationToken ct)
    {
        var key = $"signals:{id:N}:{status}";
        var query = db.Signals.AsNoTracking().Where(s => s.IncidentId == id);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SignalStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new McpException($"status '{McpQuery.Echo(status)}' is not one of: {string.Join(", ", Enum.GetNames<SignalStatus>())}.");
            }

            query = query.Where(s => s.Status == parsed);
        }

        var offset = McpQuery.ReadOffset(cursor, key);

        var rows = await query
            .OrderBy(s => s.FirstSeen)
            .ThenBy(s => s.Id)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new SignalPage
        {
            Signals = [.. rows.Take(limit).Select(s => new SignalRow
            {
                Id = s.Id,
                Name = McpText.Name(s.Reason),
                Source = s.Source,
                Status = s.Status,
                Severity = s.Severity,
                FirstSeen = s.FirstSeen,
                LastSeen = s.LastSeen,
                Count = s.Count,
                Message = McpText.UntrustedOrNull(s.Message, 2_000),
                Labels = McpMap.From(s.Labels),
                Annotations = McpMap.From(s.Annotations, AnnotationChars),
            })],
            NextCursor = rows.Count > limit ? McpQuery.Cursor(key, offset + limit) : null,
        };
    }

    public async Task<TimelinePage> TimelineAsync(Guid id, int limit, string? cursor, CancellationToken ct)
    {
        var key = $"timeline:{id:N}";

        var transitions = await db.IncidentEvents.AsNoTracking()
            .Where(e => e.IncidentId == id)
            .Select(e => new { e.At, e.From, e.To, e.Reason })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var audit = await db.AuditEvents.AsNoTracking()
            .Where(a => a.IncidentId == id)
            .Select(a => new { a.At, a.Type, a.Actor, a.Summary, a.Detail })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var entries = transitions
            .Select(t => new TimelineEntry
            {
                At = t.At,
                Kind = "transition",
                From = t.From,
                To = t.To,
                Reason = McpText.UntrustedOrNull(t.Reason, 1_000),
            })
            .Concat(audit.Select(a => new TimelineEntry
            {
                At = a.At,
                Kind = "audit",
                Action = ServerWord(a.Type),
                Actor = McpText.NameOrNull(a.Actor),
                Reason = McpText.UntrustedOrNull(a.Summary, 1_000),
                Origin = Origin(a.Detail),
            }))
            .OrderBy(e => e.At)
            .ThenBy(e => e.Kind == "transition" ? 0 : 1)
            .ToList();

        var offset = McpQuery.ReadOffset(cursor, key);
        var page = entries.Skip(offset).Take(limit).ToList();

        return new TimelinePage
        {
            Entries = page,
            NextCursor = entries.Count > offset + limit ? McpQuery.Cursor(key, offset + limit) : null,
        };
    }

    public async Task<NotificationPage> NotificationsAsync(Guid id, int limit, string? cursor, CancellationToken ct)
    {
        var key = $"notifications:{id:N}";
        var offset = McpQuery.ReadOffset(cursor, key);

        var rows = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.IncidentId == id)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new NotificationPage
        {
            Notifications = [.. rows.Take(limit).Select(d => new NotificationRow
            {
                Id = d.Id,
                At = d.CreatedAt,
                Event = d.Event,
                Channel = McpText.Name(d.Channel),
                Recipients = [.. d.Recipients.Select(McpText.Name)],
                Routes = [.. d.Routes.Select(McpText.Name)],
                Step = d.Step,
                Status = d.Status,
                Attempts = d.AttemptCount,
                DeliveredAt = d.DeliveredAt,
                Error = McpText.UntrustedOrNull(d.LastError, 500),
            })],
            NextCursor = rows.Count > limit ? McpQuery.Cursor(key, offset + limit) : null,
        };
    }

    /// <summary>The values that exist for each filter, most incidents first.</summary>
    public async Task<FilterValues> FiltersAsync(string? field, int limit, CancellationToken ct)
    {
        var want = (field ?? "all").Trim().ToLowerInvariant();
        var known = new[] { "all", "namespace", "alertname", "cluster", "workload", "assignee", "kind", "repository" };

        if (!known.Contains(want))
        {
            throw new McpException($"field '{McpQuery.Echo(field)}' is not one of: namespace, alertName, cluster, workload, assignee, kind, repository (or leave it out for all).");
        }

        bool Wants(string f) => want == "all" || want == f;
        var live = db.Incidents.AsNoTracking().Where(i => i.State != IncidentState.Suppressed);
        var open = HephaistoDbContext.OpenStates;

        async Task<IReadOnlyList<FilterValue>> Group(IQueryable<FilterPair> source) =>
            [.. (await source.ToListAsync(ct).ConfigureAwait(false))
                .Where(x => !string.IsNullOrEmpty(x.Value))
                .GroupBy(x => x.Value!)
                .Select(g => new { g.Key, Total = g.Count(), Open = g.Count(x => x.Open) })
                .OrderByDescending(g => g.Total)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(limit)
                .Select(g => new FilterValue { Value = McpText.Name(g.Key), Open = g.Open, Total = g.Total })];

        return new FilterValues
        {
            Namespaces = Wants("namespace") ? await Group(live.Select(i => new FilterPair((string?)i.Target.Namespace, open.Contains(i.State)))).ConfigureAwait(false) : null,
            AlertNames = Wants("alertname") ? await Group(live.Select(i => new FilterPair(i.AlertName, open.Contains(i.State)))).ConfigureAwait(false) : null,
            Clusters = Wants("cluster") ? await Group(live.Select(i => new FilterPair((string?)i.Target.Cluster, open.Contains(i.State)))).ConfigureAwait(false) : null,
            Workloads = Wants("workload") ? await Group(live.Select(i => new FilterPair((string?)(i.Target.OwnerName ?? i.Target.Name), open.Contains(i.State)))).ConfigureAwait(false) : null,
            Assignees = Wants("assignee") ? await Group(live.Select(i => new FilterPair(i.AssignedTo, open.Contains(i.State)))).ConfigureAwait(false) : null,
            Kinds = Wants("kind") ? await Group(live.Select(i => new FilterPair((string?)i.Kind.ToString(), open.Contains(i.State)))).ConfigureAwait(false) : null,
            Repositories = Wants("repository")
                ? await Group(db.CodeFixAttempts.AsNoTracking().Select(a => new FilterPair((string?)a.RepositoryUrl,
                    a.State == CodeFixState.Eligible || a.State == CodeFixState.Planning || a.State == CodeFixState.PlanReady || a.State == CodeFixState.Implementing))).ConfigureAwait(false)
                : null,
        };
    }

    // -------------------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------------------

    internal sealed record FilterPair(string? Value, bool Open);

    internal sealed record RowData(
        Guid Id,
        string Title,
        Severity Severity,
        IncidentState State,
        SignalKind Kind,
        string? AlertName,
        string Cluster,
        string Namespace,
        string TargetName,
        string? OwnerName,
        string? AssignedTo,
        string? AcknowledgedBy,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ResolvedAt,
        DateTimeOffset? ClosedAt);

    internal static readonly System.Linq.Expressions.Expression<Func<Incident, RowData>> RowProjection = i => new RowData(
        i.Id,
        i.Title,
        i.Severity,
        i.State,
        i.Kind,
        i.AlertName,
        i.Target.Cluster,
        i.Target.Namespace,
        i.Target.Name,
        i.Target.OwnerName,
        i.AssignedTo,
        i.AcknowledgedBy,
        i.OpenedAt,
        i.ResolvedAt,
        i.ClosedAt);

    internal IncidentRow Row(RowData r) => new()
    {
        Id = r.Id,
        ShortId = ShortId(r.Id),
        Title = McpText.Untrusted(r.Title, 300),
        Severity = r.Severity,
        State = r.State,
        Kind = r.Kind,
        AlertName = McpText.NameOrNull(r.AlertName),
        Cluster = McpText.NameOrNull(r.Cluster),
        Namespace = McpText.Name(r.Namespace),
        Workload = McpText.Name(r.OwnerName ?? r.TargetName),
        AssignedTo = McpText.NameOrNull(r.AssignedTo),
        AcknowledgedBy = McpText.NameOrNull(r.AcknowledgedBy),
        OpenedAt = r.OpenedAt,
        EndedAt = r.ClosedAt ?? r.ResolvedAt,
        Url = NotificationLinks.Incident(notifications.CurrentValue.BaseUrl, r.Id),
    };

    internal static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>The alert's own words about itself: description, then summary, then the message.</summary>
    internal static string? Description(IReadOnlyDictionary<string, string>? annotations, string? message)
    {
        if (annotations is not null)
        {
            foreach (var key in new[] { "description", "summary", "message" })
            {
                if (annotations.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    /// <summary>The <c>origin</c> an MCP write recorded in the audit row's detail, if it did.</summary>
    internal static AuditOriginView? Origin(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detail);

            if (!doc.RootElement.TryGetProperty("origin", out var o) || o.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? S(string name) => o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            return new AuditOriginView
            {
                Source = ServerWord(S("source")) ?? "unknown",
                Token = McpText.NameOrNull(S("token")),
                TokenKind = McpText.NameOrNull(S("tokenKind")),
                Role = McpText.NameOrNull(S("role")),
                Client = McpText.UntrustedOrNull(S("client"), 200),
                ClaimedBy = McpText.NameOrNull(S("claimedBy")),
                ClaimVerified = o.TryGetProperty("claimVerified", out var v) && v.ValueKind == JsonValueKind.True,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A word Hephaisto itself writes (an audit type, an origin source); anything else is not passed on.</summary>
    private static string? ServerWord(string? value) =>
        value is not null && ServerWordPattern().IsMatch(value) ? value : null;

    private static string Hyphenate(string hex)
    {
        var parts = new[] { 8, 4, 4, 4, 12 };
        var sb = new System.Text.StringBuilder();
        var at = 0;

        foreach (var len in parts)
        {
            if (at >= hex.Length)
            {
                break;
            }

            if (at > 0)
            {
                sb.Append('-');
            }

            var take = Math.Min(len, hex.Length - at);
            sb.Append(hex, at, take);
            at += take;

            if (take < len)
            {
                break;
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidIn();

    [GeneratedRegex("^[0-9a-f]{8,32}$")]
    private static partial Regex HexPrefix();

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$")]
    private static partial Regex ServerWordPattern();
}

public sealed record McpConnection(string Name, ConnectionState State, string Detail, DateTimeOffset CheckedAt);

public sealed record McpStatus(
    AgentStatusView Status,
    CodeFixCounts CodeFixes,
    IReadOnlyList<McpConnection> Connections,
    string Version,
    string Commit);
