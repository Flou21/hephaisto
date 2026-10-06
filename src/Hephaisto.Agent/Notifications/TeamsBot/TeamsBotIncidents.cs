using System.Globalization;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.Agent.Notifications.TeamsBot;

/// <summary>
/// Reads incidents in the shape the cards show them.
/// </summary>
/// <remarks>
/// Two queries per call and never one per incident: the incidents, then the code-fix attempts of
/// all of them at once. A board refreshed every few seconds is exactly where a lookup per row
/// turns into the agent's largest source of database load.
/// </remarks>
public sealed class TeamsBotIncidents(HephaistoDbContext db)
{
    /// <summary>
    /// How many open incidents are read before ordering. Severity is stored by name, so the
    /// database cannot order by it; the newest of these are read and ordered here.
    /// </summary>
    private const int ReadCeiling = 500;

    /// <summary>The open incidents, worst first and then newest first, and how many there are in all.</summary>
    public async Task<(IReadOnlyList<TeamsIncident> Listed, int Total)> OpenAsync(int max, CancellationToken ct)
    {
        var open = db.Incidents.AsNoTracking().Where(i => HephaistoDbContext.OpenStates.Contains(i.State));

        var total = await open.CountAsync(ct).ConfigureAwait(false);

        var newest = await Project(open.OrderByDescending(i => i.OpenedAt).Take(ReadCeiling))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var listed = newest
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.OpenedAt)
            .Take(Math.Max(0, max))
            .ToList();

        return (await WithDiagnosisAsync(
            await WithNotesAsync(await WithCodeFixAsync(listed, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false), total);
    }

    /// <summary>The named incidents, whatever state they are in. One that no longer exists is absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, TeamsIncident>> ByIdAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, TeamsIncident>();
        }

        var found = await Project(db.Incidents.AsNoTracking().Where(i => ids.Contains(i.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (await WithDiagnosisAsync(
                await WithNotesAsync(await WithCodeFixAsync(found, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
                ct).ConfigureAwait(false))
            .ToDictionary(i => i.Id);
    }

    private static IQueryable<TeamsIncident> Project(IQueryable<Incident> incidents) =>
        incidents.Select(i => new TeamsIncident
        {
            Id = i.Id,
            Title = i.Title,
            Kind = i.Kind,
            Severity = i.Severity,
            State = i.State,
            EscalationReason = i.EscalationReason,
            Target = i.Target.Name == string.Empty
                ? string.Empty
                : i.Target.Namespace + "/" + i.Target.Kind + "/" + i.Target.Name,
            OpenedAt = i.OpenedAt,
            AssignedTo = i.AssignedTo,
            AcknowledgedBy = i.AcknowledgedBy,
            ClosedBy = i.ClosedBy,
            AcknowledgedClaimedBy = i.AcknowledgedClaimedBy,
            ClosedClaimedBy = i.ClosedClaimedBy,
            Summary = i.Resolution,

            // The alert that opened it: the oldest Alertmanager signal, whose reason is the
            // alertname. A subquery in the same statement, not a lookup per incident.
            AlertName = i.Signals
                .Where(s => s.Source == SignalSource.Alertmanager)
                .OrderBy(s => s.FirstSeen)
                .Select(s => s.Reason)
                .FirstOrDefault(),
        });

    /// <summary>
    /// The start of each incident's alert note (#145), in one query for all of them.
    /// </summary>
    private async Task<IReadOnlyList<TeamsIncident>> WithNotesAsync(
        IReadOnlyList<TeamsIncident> incidents,
        CancellationToken ct)
    {
        var names = incidents
            .Select(i => i.AlertName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return incidents;
        }

        var bodies = await db.AlertNotes.AsNoTracking()
            .Where(n => names.Contains(n.AlertName) && n.Body != string.Empty)
            .Select(n => new { n.AlertName, n.Body })
            .ToDictionaryAsync(n => n.AlertName, n => n.Body, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        return [.. incidents.Select(i => i.AlertName is { } name && bodies.TryGetValue(name, out var body)
            ? i with { NoteExcerpt = AlertNote.Excerpt(body) }
            : i)];
    }

    /// <summary>
    /// Each incident's newest investigation and what it found, in two queries for all of them: the
    /// investigations, then the primary findings of the newest ones with their first citations.
    /// </summary>
    private async Task<IReadOnlyList<TeamsIncident>> WithDiagnosisAsync(
        IReadOnlyList<TeamsIncident> incidents,
        CancellationToken ct)
    {
        if (incidents.Count == 0)
        {
            return incidents;
        }

        var ids = incidents.Select(i => i.Id).ToList();

        var investigations = await db.Investigations.AsNoTracking()
            .Where(v => ids.Contains(v.IncidentId) && v.CompletedAt != null)
            .Select(v => new
            {
                v.Id,
                v.IncidentId,
                v.StartedAt,
                v.TerminationReason,
                v.Executor,
                v.ModelId,
                PlanSummary = v.Plan == null ? null : v.Plan.Summary,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var newest = investigations
            .GroupBy(v => v.IncidentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.StartedAt).First());

        if (newest.Count == 0)
        {
            return incidents;
        }

        // Every completed investigation's primary finding, not only the newest's: the newest is
        // what a card shows, and whether ANY of them found something is what decides if the card
        // offers another attempt - the console's rule, which reads all of them.
        var investigationIds = investigations.Select(v => v.Id).ToList();

        var primaries = await db.Findings.AsNoTracking()
            .Where(f => investigationIds.Contains(f.InvestigationId) && f.IsPrimary)
            .Select(f => new
            {
                f.InvestigationId,
                f.Hypothesis,
                f.Category,
                f.Confidence,
                f.CodeRefs,
                Evidence = f.Evidence.OrderBy(e => e.Id).Select(e => e.Excerpt).Take(5).ToList(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byInvestigation = primaries
            .GroupBy(f => f.InvestigationId)
            .ToDictionary(g => g.Key, g => g.First());

        var diagnosed = investigations
            .Where(v => byInvestigation.ContainsKey(v.Id))
            .Select(v => v.IncidentId)
            .ToHashSet();

        return [.. incidents.Select(i =>
        {
            if (!newest.TryGetValue(i.Id, out var v))
            {
                return i;
            }

            byInvestigation.TryGetValue(v.Id, out var f);

            return i with
            {
                Diagnosed = diagnosed.Contains(i.Id),
                Diagnosis = new TeamsDiagnosis
                {
                    Hypothesis = f?.Hypothesis,
                    Category = f?.Category,
                    Confidence = f?.Confidence,
                    Summary = v.PlanSummary,
                    Termination = v.TerminationReason,
                    Executor = v.Executor ?? Core.Investigations.InvestigationExecutors.InProcess,
                    ModelId = v.ModelId,
                    Evidence = f?.Evidence ?? [],
                    CodeRefs = f?.CodeRefs ?? [],
                },
            };
        })];
    }

    private async Task<IReadOnlyList<TeamsIncident>> WithCodeFixAsync(
        List<TeamsIncident> incidents,
        CancellationToken ct)
    {
        if (incidents.Count == 0)
        {
            return incidents;
        }

        var ids = incidents.Select(i => i.Id).ToList();

        var attempts = await db.CodeFixAttempts.AsNoTracking()
            .Where(a => ids.Contains(a.IncidentId))
            .Select(a => new { a.IncidentId, a.State, a.PrUrl, a.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var newest = attempts
            .GroupBy(a => a.IncidentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());

        return [.. incidents.Select(i => newest.TryGetValue(i.Id, out var a)
            ? i with { CodeFix = a.State, PullRequestUrl = a.PrUrl }
            : i)];
    }
}

/// <summary>The links into Teams itself, which only Teams' own ids can build.</summary>
public static class TeamsBotLinks
{
    /// <summary>
    /// What an alert card is drawn with: its addresses, and which buttons the configuration
    /// allows. One place, because an alert is rendered in three - when it is posted, when it is
    /// compared, and when a click answers with it - and the three must draw the same card or the
    /// comparison edits it back.
    /// </summary>
    public static TeamsCardLinks Alert(NotificationOptions options, string? boardActivityId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var actions = options.TeamsBot.Actions;

        return new TeamsCardLinks
        {
            BaseUrl = options.BaseUrl,
            GrafanaUrl = options.GrafanaUrl,
            BoardUrl = Board(options.TeamsBot, boardActivityId),
            Actions = actions.Enabled,
            Closing = actions.Enabled && actions.Approvers.Count > 0,
        };
    }

    /// <summary>
    /// A link that opens the board, or null when the team is not configured.
    /// </summary>
    /// <remarks>
    /// The group id is what Teams resolves the message against, so without it there is no link
    /// worth offering - a button that opens Teams on the wrong team is worse than no button.
    /// </remarks>
    public static string? Board(TeamsBotOptions options, string? activityId)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.TeamId)
            || string.IsNullOrWhiteSpace(options.ChannelId)
            || string.IsNullOrWhiteSpace(activityId))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(options.ChannelId)}/{Uri.EscapeDataString(activityId)}"
                + $"?tenantId={Uri.EscapeDataString(options.TenantId ?? string.Empty)}"
                + $"&groupId={Uri.EscapeDataString(options.TeamId)}"
                + $"&parentMessageId={Uri.EscapeDataString(activityId)}");
    }
}
