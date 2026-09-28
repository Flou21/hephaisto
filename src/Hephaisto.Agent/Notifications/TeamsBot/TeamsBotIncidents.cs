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

        return (await WithCodeFixAsync(listed, ct).ConfigureAwait(false), total);
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

        return (await WithCodeFixAsync(found, ct).ConfigureAwait(false)).ToDictionary(i => i.Id);
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
            Summary = i.Resolution,
        });

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
