using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications.TeamsBot;

/// <summary>
/// Keeps what the bot posted in step with the incidents it shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>A comparison on a timer, not a reaction to events.</b> Each tick renders the board and every
/// live alert from the database, and edits the ones whose content would differ. There is no queue
/// of pending edits and so nothing to replay in order: a failed edit is simply still different on
/// the next tick, and an edit that a newer state overtook is never sent at all.
/// </para>
/// <para>
/// It also covers the changes that are not notifications. Closing an incident, assigning it and
/// acknowledging it announce nothing, and all three have to change the board.
/// </para>
/// <para>
/// The same shape as <c>NotificationDispatcher</c>: prime once, then a <c>PeriodicTimer</c>, a
/// scope per tick, and a catch-all so the loop outlives a bad tick.
/// </para>
/// </remarks>
public sealed class TeamsBoardReconciler(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<TeamsBoardReconciler> logger) : BackgroundService
{
    /// <summary>Alerts compared per tick. Bounded so one backlog cannot monopolise a scope.</summary>
    private const int AlertsPerTick = 100;

    /// <summary>One tick's allowance, so an endpoint that never answers cannot hold the loop.</summary>
    private static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await TickAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.CurrentValue.TeamsBot.RefreshInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One comparison of everything. Internal so a test can drive it without a timer.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TickTimeout);

            await using var scope = scopes.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
            var client = scope.ServiceProvider.GetRequiredService<ITeamsBotClient>();
            var incidents = scope.ServiceProvider.GetRequiredService<TeamsBotIncidents>();

            var board = await BoardAsync(db, client, incidents, timeout.Token).ConfigureAwait(false);

            await AlertsAsync(db, client, incidents, board, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The Teams board could not be compared this time; it will be tried again.");
        }
    }

    private async Task<TeamsBotMessage?> BoardAsync(
        HephaistoDbContext db,
        ITeamsBotClient client,
        TeamsBotIncidents incidents,
        CancellationToken ct)
    {
        var o = options.CurrentValue;
        var channel = o.TeamsBot.ChannelId ?? string.Empty;
        var links = new TeamsCardLinks { BaseUrl = o.BaseUrl, GrafanaUrl = o.GrafanaUrl };

        var (listed, total) = await incidents.OpenAsync(o.TeamsBot.BoardMaxIncidents, ct).ConfigureAwait(false);

        // Without the timestamp, so the clock moving is not a change.
        var hash = TeamsBotCards.Hash(TeamsBotCards.BoardWithin(listed, total, links, updatedAt: null));

        var board = await db.TeamsBotMessages
            .Where(m => m.Kind == TeamsBotMessageKind.Board
                && m.State == TeamsBotMessageState.Live
                && m.Recipient == channel)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (board is not null && board.ContentHash == hash)
        {
            return board;
        }

        var now = clock.UtcNow;
        var card = TeamsBotCards.BoardWithin(listed, total, links, now);

        if (board is null)
        {
            var posted = await client.PostToChannelAsync(card, ct).ConfigureAwait(false);

            if (!posted.Ok)
            {
                logger.LogWarning(
                    "The Teams board could not be posted ({Status}): {Detail}",
                    posted.Status,
                    posted.Detail);

                return null;
            }

            board = new TeamsBotMessage
            {
                Kind = TeamsBotMessageKind.Board,
                State = TeamsBotMessageState.Live,
                Recipient = channel,
                ConversationId = posted.Value.ConversationId,
                ActivityId = posted.Value.ActivityId,
                ContentHash = hash,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.TeamsBotMessages.Add(board);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation("Posted the Teams board with {Listed} of {Total} open incidents.", listed.Count, total);

            return board;
        }

        var edited = await client.UpdateAsync(board.ConversationId, board.ActivityId, card, ct).ConfigureAwait(false);

        if (edited.Ok)
        {
            board.ContentHash = hash;
            board.UpdatedAt = now;
            board.LastError = null;
        }
        else if (edited.Gone)
        {
            // Somebody removed it by hand. It cannot be edited back into existence, so this row
            // is over and the next tick posts a new board.
            board.State = TeamsBotMessageState.Final;
            board.LastError = Short(edited.Detail);

            logger.LogWarning("The Teams board is gone from the channel; a new one will be posted.");
        }
        else
        {
            board.LastError = Short(edited.Detail);

            logger.LogWarning(
                "The Teams board could not be edited ({Status}): {Detail}",
                edited.Status,
                edited.Detail);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return board.State == TeamsBotMessageState.Live ? board : null;
    }

    private async Task AlertsAsync(
        HephaistoDbContext db,
        ITeamsBotClient client,
        TeamsBotIncidents incidents,
        TeamsBotMessage? board,
        CancellationToken ct)
    {
        var alerts = await db.TeamsBotMessages
            .Where(m => m.Kind == TeamsBotMessageKind.Alert
                && (m.State == TeamsBotMessageState.Live || m.State == TeamsBotMessageState.Superseded))
            .OrderBy(m => m.UpdatedAt)
            .Take(AlertsPerTick)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (alerts.Count == 0)
        {
            return;
        }

        var o = options.CurrentValue;

        var links = new TeamsCardLinks
        {
            BaseUrl = o.BaseUrl,
            GrafanaUrl = o.GrafanaUrl,
            BoardUrl = TeamsBotLinks.Board(o.TeamsBot, board?.ActivityId),
        };

        var ids = alerts.Where(a => a.IncidentId is not null).Select(a => a.IncidentId!.Value).Distinct().ToList();
        var known = await incidents.ByIdAsync(ids, ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        foreach (var alert in alerts)
        {
            if (alert.IncidentId is not { } id || !known.TryGetValue(id, out var incident))
            {
                // Nothing left to compare it with. It stays as it was last edited.
                alert.State = TeamsBotMessageState.Final;
                alert.UpdatedAt = now;

                continue;
            }

            var superseded = alert.State == TeamsBotMessageState.Superseded;

            var card = superseded
                ? TeamsBotCards.Superseded(incident, links)
                : TeamsBotCards.Alert(incident, links);

            var hash = TeamsBotCards.Hash(card);

            if (hash != alert.ContentHash)
            {
                var edited = await client.UpdateAsync(alert.ConversationId, alert.ActivityId, card, ct).ConfigureAwait(false);

                if (edited.Ok)
                {
                    alert.ContentHash = hash;
                    alert.LastError = null;
                }
                else if (edited.ToDelivery().Disposition == DeliveryDisposition.Permanent)
                {
                    // Refused in a way that waiting cannot change - the chat is gone, the app was
                    // removed for this person. Trying every tick for ever is how a reconciler
                    // becomes a source of load rather than of truth.
                    alert.State = TeamsBotMessageState.Final;
                    alert.LastError = Short(edited.Detail);
                    alert.UpdatedAt = now;

                    logger.LogWarning(
                        "An alert for incident {IncidentId} can no longer be edited ({Status}) and is left as it is.",
                        id,
                        edited.Status);

                    continue;
                }
                else
                {
                    alert.LastError = Short(edited.Detail);
                    alert.UpdatedAt = now;

                    continue;
                }
            }

            if (superseded || !incident.IsOpen)
            {
                // It says how things ended. There is nothing further to keep in step.
                alert.State = TeamsBotMessageState.Final;
            }

            alert.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string? Short(string? text) =>
        text is { Length: > HephaistoDbContext.MaxErrorLength } ? text[..HephaistoDbContext.MaxErrorLength] : text;
}
