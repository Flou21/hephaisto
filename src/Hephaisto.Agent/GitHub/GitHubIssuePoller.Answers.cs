using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// The fourth statement of a pass (v0.14.0): <b>a plan that waits has been asked whether
/// somebody answered it.</b> For every taken work item whose attempt is <c>PlanReady</c> and
/// whose plan is on the issue, the comments written after the plan are read for a command -
/// <c>/approve</c> or <c>/reject</c> (<see cref="IssueCommands"/>) - and the first one from an
/// approver decides, through the door the console's button knocks on
/// (<see cref="CodeFixCoordinator.DecideForWorkItemAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may answer is a number.</b> A comment counts only when its author's account NUMBER is
/// in <see cref="GitHubOptions.Approvers"/>. The login is what is recorded and shown; it never
/// decides, because a login can be given up and registered by somebody else. With nobody
/// listed nothing is read at all: a comment cannot be an answer, and the plan says so.
/// </para>
/// <para>
/// <b>What is approved is the attempt, by its id</b> - the plan stored in Postgres. The comment
/// carries one word and, for a rejection, a reason. Nothing else of its text goes anywhere.
/// </para>
/// <para>
/// <b>Once, also across a restart.</b> Three things make that true, none of them a queue:
/// </para>
/// <list type="bullet">
/// <item>the decision is the attempt's own state. An approval moves it out of <c>PlanReady</c>
/// under a row lock before a Job exists, and an attempt that is not <c>PlanReady</c> is not
/// asked about here - so a process that died after deciding has nothing left to repeat;</item>
/// <item>the newest comment that was looked at is kept on the attempt
/// (<see cref="CodeFixAttempt.CommandCommentId"/>). GitHub's comment ids only grow, so a pass
/// reads what is beyond it and nothing twice - and a comment that was looked at is never read
/// for another meaning, whatever it is edited into afterwards. A command counts by the text it
/// has when it is first seen;</item>
/// <item>the answers Hephaisto wrote are kept by key (<see cref="CodeFixAttempt.CommandAnswers"/>),
/// and each carries a marker. A process that wrote one and died before recording it finds the
/// marker in the comments it reads next, under its own account, and does not write a second.</item>
/// </list>
/// <para>
/// <b>It answers rarely.</b> Somebody who is not an approver is told so ONCE per attempt, whoever
/// they are and however many follow; a refusal of the door's - the mode is below Pr, the
/// emergency stop, a plan that needs a second repository - once per attempt and cause; and
/// never beyond <see cref="IssueComments.MaxPerWorkItem"/> comments on one work item. A refusal
/// does not use the plan up: when its cause is gone, a NEW <c>/approve</c> is acted on.
/// </para>
/// <para>
/// <b>Not hung on the list's 304.</b> An edited comment does not change the list of assigned
/// issues, and a comment may arrive in the second a list was last read. So the comments of every
/// waiting plan are asked for on every pass - with the tag of the last answer, which makes an
/// unchanged issue a 304 that costs nothing, and from the time of the newest comment already
/// seen, which keeps the answer short. Both are memory only: a restart reads once in full, and
/// what was handled is in the database.
/// </para>
/// </remarks>
public sealed partial class GitHubIssuePoller
{
    public const string AuditCommand = "workitem.command";

    private sealed record Waiting(
        Guid WorkItemId,
        int Number,
        Guid AttemptId,
        long PlanCommentId,
        long? Cursor,
        string? Answers,
        DateTimeOffset PlanReadyAt,
        bool HasStatusComment);

    /// <summary>How the comments of a waiting plan were last read: from when, and the answer's tag. Touched by the one loop.</summary>
    private readonly Dictionary<Guid, (string Repository, DateTimeOffset Since, string? ETag)> commandReads = [];

    /// <returns>Null when every waiting plan of the repository was asked about; otherwise the first thing that could not be done.</returns>
    private async Task<string?> AnswersAsync(IGitHubClient client, string repository, string bot, CancellationToken ct)
    {
        var approvers = options.Value.ApproverIds();

        // Nobody may answer on an issue: nothing is asked of GitHub, and no stranger is told
        // about approvers there are none of. The console's door is the only one.
        if (approvers.Count == 0)
        {
            return null;
        }

        List<Waiting> waiting;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            waiting = await db.CodeFixAttempts.AsNoTracking()
                .Where(a => a.State == CodeFixState.PlanReady
                    && a.PlanCommentId != null
                    && a.WorkItem != null
                    && a.WorkItem.Source == WorkItem.GitHubSource
                    && a.WorkItem.Repository == repository
                    && a.WorkItem.State == WorkItemState.Taken)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new Waiting(
                    a.WorkItemId!.Value,
                    a.WorkItem!.Number,
                    a.Id,
                    a.PlanCommentId!.Value,
                    a.CommandCommentId,
                    a.CommandAnswers,
                    a.PlanReadyAt ?? a.CreatedAt,
                    a.WorkItem.StatusCommentId != null))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        // A plan that stopped waiting is not read for any more: its tag is forgotten with it.
        foreach (var gone in commandReads.Where(r => r.Value.Repository == repository && waiting.TrueForAll(w => w.AttemptId != r.Key)).Select(r => r.Key).ToList())
        {
            commandReads.Remove(gone);
        }

        string? problem = null;

        foreach (var plan in waiting)
        {
            try
            {
                problem ??= await AnswerAsync(client, repository, bot, plan, approvers, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                commandReads.Remove(plan.AttemptId);
                problem ??= $"{repository}#{plan.Number}: an answer to its plan could not be acted on: {ex.GetType().Name}";
                logger.LogError(ex, "Could not act on the answers to the plan of {Repository}#{Number}; retrying next interval.", repository, plan.Number);
            }
        }

        return problem;
    }

    /// <summary>One waiting plan: the comments beyond the last one looked at, in order, until one decides.</summary>
    private async Task<string?> AnswerAsync(
        IGitHubClient client, string repository, string bot, Waiting plan, IReadOnlySet<long> approvers, CancellationToken ct)
    {
        var read = commandReads.TryGetValue(plan.AttemptId, out var known)
            ? known
            : (Repository: repository, Since: plan.PlanReadyAt - ClockSlack, ETag: null);

        var list = await client.ListCommentsAsync(repository, plan.Number, read.Since, read.ETag, ct).ConfigureAwait(false);

        if (list.Outcome == GitHubOutcome.NotModified)
        {
            // Unchanged since a list that was acted on completely.
            return null;
        }

        if (list is not { Ok: true, Value: { } comments })
        {
            commandReads.Remove(plan.AttemptId);
            return $"{repository}#{plan.Number}: its comments could not be read for an answer: {list.Describe()}";
        }

        // Never before the plan's own comment: an /approve written before there was a plan
        // approved nothing.
        var cursor = Math.Max(plan.Cursor ?? 0, plan.PlanCommentId);
        var fresh = comments.Where(c => c.Id > cursor).OrderBy(c => c.Id).ToList();

        var given = new List<string>((plan.Answers ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var recorded = given.Count;

        // An answer that was written and never recorded: found by its marker, under the
        // account's own name - a stranger's comment with the same marker is a stranger's.
        foreach (var key in fresh
            .Where(c => string.Equals(c.Author.Login, bot, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => IssueComments.AnswerKeysIn(plan.AttemptId, c.Body))
            .Where(key => !given.Contains(key, StringComparer.Ordinal)))
        {
            given.Add(key);
        }

        // The status comment, the plan, and every answer so far.
        var written = (plan.HasStatusComment ? 1 : 0) + 1 + given.Count;
        var handled = cursor;
        string? problem = null;

        // Writes the one answer of a key, unless it was given or the ceiling is reached - both
        // of which are "said". False only when GitHub refused the write.
        async Task<bool> SayOnceAsync(GitHubComment to, IssueCommand command, string key, string outcome, Func<string> text)
        {
            if (given.Contains(key, StringComparer.Ordinal))
            {
                return true;
            }

            if (written >= IssueComments.MaxPerWorkItem)
            {
                logger.LogWarning(
                    "{Repository}#{Number}: not answering comment {CommentId} ({Answer}); Hephaisto has written {Written} comments for this work item, which is its ceiling.",
                    repository, plan.Number, to.Id, key, written);
                return true;
            }

            var created = await client.CreateCommentAsync(repository, plan.Number, text(), ct).ConfigureAwait(false);

            if (!created.Ok)
            {
                problem = $"{repository}#{plan.Number}: an answer could not be written on the issue: {created.Describe()}";
                return false;
            }

            given.Add(key);
            written++;

            await AuditCommandAsync(repository, plan, to, command, outcome, key, ct).ConfigureAwait(false);
            return true;
        }

        foreach (var comment in fresh)
        {
            if (IssueCommands.Read(comment, bot) is not { } command)
            {
                handled = comment.Id;
                continue;
            }

            var login = Cap(comment.Author.Login, 64);

            if (!approvers.Contains(comment.Author.Id))
            {
                if (!await SayOnceAsync(comment, command, IssueComments.NotApproverKey, "not_approver", () => IssueComments.NotApprover(plan.AttemptId, login)).ConfigureAwait(false))
                {
                    // Not past this comment: the next pass answers it.
                    break;
                }

                // Counted where the comment is put behind the cursor, so once: a comment that
                // could not be answered is read again, and is not counted until it was.
                handled = comment.Id;
                metrics.Command(command.Kind == IssueCommandKind.Approve, GitHubMetrics.CommandNotApprover);
                continue;
            }

            var approve = command.Kind == IssueCommandKind.Approve;
            CodeFixDecisionResult decision;

            await using (var scope = scopes.CreateAsyncScope())
            {
                decision = await scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>()
                    .DecideForWorkItemAsync(
                        plan.WorkItemId, plan.AttemptId, approve, $"github:{login}", ApprovalSource.GitHub, authenticated: true, command.Reason, ct)
                    .ConfigureAwait(false);
            }

            if (decision.Outcome == CodeFixDecisionOutcome.Done)
            {
                handled = comment.Id;
                metrics.Command(approve, GitHubMetrics.CommandAccepted);

                await AuditCommandAsync(repository, plan, comment, command, approve ? "approved" : "rejected", null, ct).ConfigureAwait(false);

                logger.LogInformation(
                    "{Login} ({AccountId}) {Verb} the plan of {Repository}#{Number} in comment {CommentId}: {Message}.",
                    login, comment.Author.Id, approve ? "approved" : "rejected", repository, plan.Number, comment.Id, decision.Message);

                // The attempt is no longer waiting. What follows on the issue answers nothing.
                break;
            }

            var key = IssueComments.AnswerKey(decision.Refusal, decision.Mode);

            if (!await SayOnceAsync(comment, command, key, "refused", () => IssueComments.Refused(plan.AttemptId, login, command.Kind, decision.Refusal, decision.Mode)).ConfigureAwait(false))
            {
                break;
            }

            handled = comment.Id;
            metrics.Command(approve, GitHubMetrics.CommandRefused(key));

            logger.LogInformation(
                "{Login} ({AccountId}) answered the plan of {Repository}#{Number} in comment {CommentId} and was refused: {Message}.",
                login, comment.Author.Id, repository, plan.Number, comment.Id, decision.Message);

            if (decision.Refusal is CodeFixRefusal.NotWaiting or CodeFixRefusal.SubjectTakenBack or CodeFixRefusal.NotFound)
            {
                // Somebody else answered it, or it is gone: nothing further on the issue can decide it.
                break;
            }
        }

        if (handled != (plan.Cursor ?? 0) || given.Count != recorded)
        {
            var answers = given.Count == 0 ? null : string.Join(",", given);

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            // By statement, as the comment ids are: the row is the coordinator's and the
            // watcher's too.
            await db.CodeFixAttempts.Where(a => a.Id == plan.AttemptId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CommandCommentId, handled).SetProperty(a => a.CommandAnswers, answers), ct)
                .ConfigureAwait(false);
        }

        if (problem is not null)
        {
            commandReads.Remove(plan.AttemptId);
            return problem;
        }

        // From the newest comment seen, by GITHUB's clock, less a second: its times are whole
        // seconds, and a comment written in the second of the last one must not be missed
        // whichever way `since` rounds. A tag belongs to one question, so it is kept only when
        // the next question is the same one.
        var since = comments.Count == 0 ? read.Since : Max(read.Since, comments.Max(c => c.CreatedAt).AddSeconds(-1));

        commandReads[plan.AttemptId] = (repository, since, since == read.Since ? list.ETag : null);

        return null;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    /// <summary>
    /// One row per command that led to something - a decision, or an answer Hephaisto wrote - with
    /// what the door's own row does not hold: the account's number, and which comment it was.
    /// Not for a command that was passed over in silence: a flood is one row, like one answer.
    /// </summary>
    private async Task AuditCommandAsync(
        string repository, Waiting plan, GitHubComment comment, IssueCommand command, string outcome, string? answer, CancellationToken ct)
    {
        var word = command.Kind == IssueCommandKind.Approve ? IssueCommands.Approve : IssueCommands.Reject;
        var login = Cap(comment.Author.Login, 64);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        db.AuditEvents.Add(new AuditEvent
        {
            At = clock.UtcNow,
            Type = AuditCommand,
            Actor = IncidentStateMachine.SystemActor,
            Summary = outcome switch
            {
                "approved" => $"{login} ({comment.Author.Id}) approved the plan of {repository}#{plan.Number} with {word}",
                "rejected" => $"{login} ({comment.Author.Id}) rejected the plan of {repository}#{plan.Number} with {word}",
                "not_approver" => $"{word} on {repository}#{plan.Number} by {login} ({comment.Author.Id}) did not count: not an approver",
                _ => $"{word} on {repository}#{plan.Number} by {login} ({comment.Author.Id}) was refused: {answer}",
            },
            Detail = JsonSerializer.Serialize(new
            {
                work_item_id = plan.WorkItemId,
                attempt_id = plan.AttemptId,
                source = WorkItem.GitHubSource,
                repository,
                number = plan.Number,
                comment_id = comment.Id,
                login,
                account_id = comment.Author.Id,
                command = word,
                outcome,
                answer,
            }),
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
