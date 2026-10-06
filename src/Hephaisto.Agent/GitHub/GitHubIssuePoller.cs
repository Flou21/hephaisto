using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Safety;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// Asks GitHub which issues are assigned to Hephaisto's account, and makes the work items say the
/// same: an assigned issue is taken, one that was closed or unassigned is cancelled (v0.14.0).
/// Then, for what is taken, what follows from it: a plan, and the issue being told
/// (<c>GitHubIssuePoller.Work.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Level-triggered, with no queue and no retry state.</b> A pass does not process events; it
/// states what should be true and makes it so. Nothing is remembered about what failed - the next
/// pass asks again - so a restart in the middle of one loses nothing, and a webhook, when there
/// is one, only has to wake this loop.
/// </para>
/// <para>
/// <b>A 304 skips the comparison, and that is only sound because of one rule:</b> the ETag of a
/// list is kept only when everything that list called for was done. A list whose work item could
/// not be written, or whose missing issue could not be read, is asked for again without a tag -
/// otherwise "unchanged" would be answered for a list this process never finished acting on, and
/// the failure would wait for somebody to touch an issue.
/// </para>
/// <para>
/// <b>Only listed repositories are ever asked about.</b> The list in
/// <see cref="GitHubOptions.Repositories"/> is the authorization, and an issue assigned to the
/// account anywhere else is not something this loop can come to know.
/// </para>
/// <para>
/// With the agent Off it asks nothing and takes nothing: Off means the agent does nothing, and a
/// work item taken while Off would be work started by a process somebody had stopped.
/// </para>
/// </remarks>
public sealed partial class GitHubIssuePoller(
    IServiceScopeFactory scopes,
    IOptions<GitHubOptions> options,
    IKillSwitch killSwitch,
    GitHubHealth health,
    GitHubMetrics metrics,
    IClock clock,
    ILogger<GitHubIssuePoller> logger) : BackgroundService
{
    public const string AuditTaken = "workitem.taken";
    public const string AuditCancelled = "workitem.cancelled";

    /// <summary>Labels that say what kind of issue this is, where the repository has no issue types. In this order.</summary>
    private static readonly string[] TypeLabels = ["bug", "enhancement", "feature"];

    // Touched by the one loop only.
    private readonly Dictionary<string, string> etags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> lastFailure = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> saidTruncated = new(StringComparer.OrdinalIgnoreCase);
    private string? resolvedBotLogin;
    private bool saidPaused;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted services after this one start only once this method first awaits.
        await Task.Yield();

        var o = options.Value;

        // Once, what this process will ask of GitHub - and never the token.
        logger.LogInformation(
            "GitHub issues are ON: polling {Repositories} at {ApiBaseUrl} every {Interval}{Proxy}; {Approvers} approver(s).",
            string.Join(", ", o.Repositories),
            SecretRedactor.Redact(o.ApiBaseUrl),
            o.PollInterval,
            string.IsNullOrWhiteSpace(o.ProxyUrl) ? string.Empty : " through the egress proxy",
            o.ApproverIds().Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The database being away, usually. Nothing to remember: the next pass is the retry.
                logger.LogError(ex, "GitHub poll pass failed; retrying next interval.");
                health.PassEnded();
            }

            var interval = options.Value.PollInterval;

            try
            {
                await Task.Delay(
                    interval >= GitHubOptions.MinimumPollInterval ? interval : GitHubOptions.MinimumPollInterval,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass over every listed repository. Public so tests can drive it without a timer.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        var o = options.Value;
        var mode = await killSwitch.ResolveAsync(ct).ConfigureAwait(false);

        if (mode.Effective == AgentMode.Off)
        {
            if (!saidPaused)
            {
                logger.LogInformation("Agent is Off ({DecidedBy}); not polling GitHub and taking no issue.", mode.DecidedBy);
                saidPaused = true;
            }

            health.PassEnded($"the agent is Off ({mode.DecidedBy})");
            return;
        }

        saidPaused = false;

        var repositories = o.Repositories
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var scope = scopes.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IGitHubClient>();

        if (await BotLoginAsync(client, repositories, ct).ConfigureAwait(false) is not { } bot)
        {
            health.PassEnded();
            return;
        }

        foreach (var repository in repositories)
        {
            try
            {
                var answered = await PollRepositoryAsync(client, repository, bot, ct).ConfigureAwait(false);

                // What comes after taking an issue (GitHubIssuePoller.Work.cs): here and not in
                // TakeAsync, after the list was compared OR found unchanged, so that it is asked
                // on every pass. A plan that could not start when its work item was created, a
                // comment GitHub refused, is then put right by the next pass like everything else.
                if (await WorkAsync(client, repository, bot, ct).ConfigureAwait(false) is { } undone)
                {
                    // The rule the tag is kept by: only when everything was done.
                    etags.Remove(repository);

                    // When GitHub did not answer the list either, that is the better sentence.
                    if (answered)
                    {
                        Failed(repository, undone);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One repository must not end the pass for the others.
                etags.Remove(repository);
                Failed(repository, $"the pass failed: {ex.GetType().Name}");
                logger.LogError(ex, "GitHub poll of {Repository} failed; retrying next interval.", repository);
            }
        }

        health.PassEnded();
    }

    /// <summary>
    /// The account issues are assigned to: the configured one, or whoever the token belongs to.
    /// Asked once per process - an account's login changing under a running agent is a restart.
    /// </summary>
    private async Task<string?> BotLoginAsync(IGitHubClient client, string[] repositories, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.BotLogin))
        {
            return options.Value.BotLogin.Trim();
        }

        if (resolvedBotLogin is not null)
        {
            return resolvedBotLogin;
        }

        var user = await client.GetAuthenticatedUserAsync(ct).ConfigureAwait(false);

        if (user is { Ok: true, Value: { Login.Length: > 0 } account })
        {
            logger.LogInformation("GitHub issues are taken for the account {Login} ({Id}).", account.Login, account.Id);
            return resolvedBotLogin = account.Login;
        }

        // Nothing can be asked about any repository without knowing whose issues to ask for.
        foreach (var repository in repositories)
        {
            metrics.Polled(user.Outcome);
            Failed(repository, $"could not ask whose token this is: {user.Describe()}");
        }

        return null;
    }

    /// <returns>Whether GitHub answered and everything the list called for was done.</returns>
    private async Task<bool> PollRepositoryAsync(IGitHubClient client, string repository, string bot, CancellationToken ct)
    {
        etags.TryGetValue(repository, out var etag);

        var list = await client.ListAssignedIssuesAsync(repository, bot, etag, ct).ConfigureAwait(false);

        metrics.Polled(list.Outcome);

        if (list.Outcome == GitHubOutcome.NotModified)
        {
            // Unchanged since a list that was acted on completely. Nothing to compare.
            Succeeded(repository, "unchanged");
            return true;
        }

        if (list is not { Ok: true, Value: { } page })
        {
            // The tag stays: it still describes the last list that was acted on completely.
            Failed(repository, list.Describe());
            return false;
        }

        if (page.HasMore && saidTruncated.Add(repository))
        {
            logger.LogWarning(
                "{Repository} has more than {PageSize} open issues assigned to {Bot}. Only the newest are looked at; "
                + "the others are taken as those are closed. A work item beyond the page is kept, not cancelled.",
                repository, GitHubClient.PageSize, bot);
        }

        List<(Guid Id, int Number)> taken;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            taken = (await db.WorkItems.AsNoTracking()
                    .Where(w => w.Source == WorkItem.GitHubSource && w.Repository == repository && w.State == WorkItemState.Taken)
                    .Select(w => new { w.Id, w.Number })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ConvertAll(w => (w.Id, w.Number));
        }

        var complete = true;
        string? firstProblem = null;

        // Assigned and open, and not yet a work item: take it.
        foreach (var issue in page.Issues.Where(i => taken.TrueForAll(t => t.Number != i.Number)))
        {
            try
            {
                await TakeAsync(repository, issue, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                complete = false;
                firstProblem ??= $"{repository}#{issue.Number} could not be recorded: {ex.GetType().Name}";
                logger.LogError(ex, "Could not record {Repository}#{Number} as a work item; retrying next interval.", repository, issue.Number);
            }
        }

        // A work item whose issue is no longer in the list: ask what became of it.
        foreach (var (id, number) in taken.Where(t => page.Issues.All(i => i.Number != t.Number)))
        {
            var issue = await client.GetIssueAsync(repository, number, ct).ConfigureAwait(false);

            var (reason, code) = issue switch
            {
                { Outcome: GitHubOutcome.NotFound } => ("GitHub no longer has the issue", GitHubMetrics.ReasonGone),
                { Ok: true, Value.IsOpen: false } => ("the issue was closed", GitHubMetrics.ReasonClosed),
                { Ok: true, Value: { } found } when !found.IsAssignedTo(bot) => ($"{bot} is no longer an assignee", GitHubMetrics.ReasonUnassigned),
                _ => (null, string.Empty),
            };

            if (reason is not null)
            {
                await CancelAsync(id, reason, code, ct).ConfigureAwait(false);
            }
            else if (!issue.Ok)
            {
                complete = false;
                firstProblem ??= $"{repository}#{number} could not be read: {issue.Describe()}";
            }

            // Open and still assigned, and not in the list: beyond the first page. It stays taken.
        }

        if (complete && list.ETag is { Length: > 0 } tag)
        {
            etags[repository] = tag;
        }
        else
        {
            etags.Remove(repository);
        }

        if (complete)
        {
            Succeeded(repository, $"{page.Issues.Count} assigned");
        }
        else
        {
            Failed(repository, firstProblem ?? "the pass did not finish");
        }

        return complete;
    }

    /// <summary>
    /// Records an assigned issue as a work item, with the audit row in the same statement batch.
    /// Twice is once: the partial unique index lets one <c>Taken</c> row per issue through, and
    /// the other insert is somebody having done this already.
    /// </summary>
    private async Task TakeAsync(string repository, GitHubIssue issue, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var item = new WorkItem
        {
            Source = WorkItem.GitHubSource,
            Repository = repository,
            Number = issue.Number,
            NodeId = Cap(issue.NodeId, 128),
            Url = Cap(issue.Url, 512),
            Title = issue.Title,
            Type = TypeOf(issue),
            AuthorLogin = Cap(issue.Author.Login, 64),
            AuthorId = issue.Author.Id,
            Body = issue.Body ?? string.Empty,
            Labels = [.. issue.Labels],
            State = WorkItemState.Taken,
            TakenAt = now,
            UpdatedAt = now,
        };

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        db.WorkItems.Add(item);
        db.AuditEvents.Add(Audit(AuditTaken, item, $"took {repository}#{issue.Number} as work: {Cap(issue.Title, 120)}", now));

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogDebug("{Repository}#{Number} is already a work item; not taking it twice.", repository, issue.Number);
            return;
        }

        metrics.Taken(item.Source);
        logger.LogInformation("Took {Repository}#{Number} as work item {WorkItemId}.", repository, issue.Number, item.Id);
    }

    private async Task CancelAsync(Guid id, string reason, string code, CancellationToken ct)
    {
        var now = clock.UtcNow;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        // Read again, tracked: between the pass's first read and now it may have been ended by
        // something else, and a cancel of what is not taken is nothing.
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct).ConfigureAwait(false);

        if (item is not { State: WorkItemState.Taken })
        {
            return;
        }

        item.State = WorkItemState.Cancelled;
        item.StateReason = reason;
        item.ClosedAt = now;
        item.UpdatedAt = now;

        db.AuditEvents.Add(Audit(AuditCancelled, item, $"cancelled {item.Repository}#{item.Number}: {reason}", now, reason));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        metrics.Closed(WorkItemState.Cancelled, code);
        logger.LogInformation(
            "Cancelled work item {WorkItemId} ({Repository}#{Number}): {Reason}.", item.Id, item.Repository, item.Number, reason);
    }

    private void Succeeded(string repository, string detail)
    {
        if (lastFailure.Remove(repository))
        {
            logger.LogInformation("GitHub answers for {Repository} again.", repository);
        }

        health.Succeeded(repository, detail, clock.UtcNow);
    }

    /// <summary>
    /// Records a failed poll, and logs it when it is a different failure from the last one: a
    /// GitHub outage is one line, not one per interval for as long as it lasts.
    /// </summary>
    private void Failed(string repository, string detail)
    {
        // "rate limited until 12:00:05" and "... 12:01:05" are one failure.
        var kind = detail.Split(" until ", 2)[0];

        if (!lastFailure.TryGetValue(repository, out var last) || last != kind)
        {
            lastFailure[repository] = kind;
            logger.LogWarning("GitHub poll of {Repository} failed: {Detail}. Retrying every interval.", repository, detail);
        }

        health.Failed(repository, detail, clock.UtcNow);
    }

    private static string? TypeOf(GitHubIssue issue) =>
        !string.IsNullOrWhiteSpace(issue.Type)
            ? Cap(issue.Type.Trim(), 64)
            : TypeLabels.FirstOrDefault(t => issue.Labels.Any(l => string.Equals(l.Trim(), t, StringComparison.OrdinalIgnoreCase)));

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max];

    private static AuditEvent Audit(string type, WorkItem item, string summary, DateTimeOffset at, string? reason = null) => new()
    {
        At = at,
        Type = type,
        Actor = IncidentStateMachine.SystemActor,
        Summary = summary,
        Detail = JsonSerializer.Serialize(new
        {
            work_item_id = item.Id,
            source = item.Source,
            repository = item.Repository,
            number = item.Number,
            url = item.Url,
            reason,
        }),
        TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
    };
}
