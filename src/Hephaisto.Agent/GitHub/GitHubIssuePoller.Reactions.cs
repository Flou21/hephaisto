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
/// The other way an approver answers a plan (#298): <b>a reaction on the plan's comment.</b> 🚀
/// approves it and 👎 rejects it (<see cref="IssueCommands.ReadReaction"/>), through the door
/// <c>/approve</c> and <c>/reject</c> knock on, with its refusals, and heard from the same
/// accounts by number. Read after the comments of the same pass, for a plan that is still
/// waiting then.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> GitHub's comment editor suggests none of the three commands - its <c>/</c> menu
/// is GitHub's own - and a mistyped one is passed over in silence. A reaction is one click, and
/// it cannot be mistyped.
/// </para>
/// <para>
/// <b>Bound to one plan.</b> A reaction is on a comment, and a plan comment is one attempt's.
/// So it answers that plan and no other: a 🚀 on an earlier plan's comment approves nothing
/// once a new plan was written, because only the newest attempt's comment is asked about.
/// </para>
/// <para>
/// <b>Hephaisto sets both itself.</b> A reaction that is already on a comment is shown below it
/// as something to click; one that is not is two menus away. So 🚀 and 👎 are set as its own
/// account - only 👎 on a plan that cannot be approved here - by the statement that writes the
/// plan comment, so that they are there when the plan is first read, and again by every pass
/// that reads the reactions and finds one missing: what the first could not do, and what
/// somebody with the token took off. Its own are never an answer, and nothing is kept about
/// having set them.
/// </para>
/// <para>
/// <b>Each reaction once.</b> The newest one looked at is kept on the attempt
/// (<see cref="CodeFixAttempt.CommandReactionId"/>); GitHub's reaction ids only grow. A refused
/// one is answered once per attempt and cause, by the keys the commands use
/// (<see cref="CodeFixAttempt.CommandAnswers"/>), and does not decide by itself when the cause
/// is gone: a reaction is a click, not a standing order. Taking it off and setting it again is
/// a new reaction. Somebody who is not an approver is told so once per attempt, whether they
/// wrote or reacted.
/// </para>
/// <para>
/// <b>What it costs.</b> One request per waiting plan and pass, with the tag of the last answer:
/// unchanged reactions are a 304. One page is read - <see cref="GitHubClient.PageSize"/>
/// reactions, oldest first; a comment with more is said in the log, and <c>/approve</c> still
/// works on it.
/// </para>
/// </remarks>
public sealed partial class GitHubIssuePoller
{
    /// <summary>The tag of the last reactions list of an attempt's plan comment that was acted on completely. Touched by the one loop.</summary>
    private readonly Dictionary<Guid, (string Repository, string? ETag)> reactionReads = [];

    /// <summary>One work item's newest attempt: the reactions on its plan comment beyond the last one looked at, in order, until one decides.</summary>
    private async Task<string?> ReactionsAsync(
        IGitHubClient client, string repository, string bot, Waiting plan, IReadOnlySet<long> approvers, CancellationToken ct)
    {
        if (plan.State != CodeFixState.PlanReady || plan.PlanCommentId is not { } planComment)
        {
            reactionReads.Remove(plan.AttemptId);
            return null;
        }

        // As it is NOW: a comment of this very pass may have decided it, or written an answer.
        long cursor;
        List<string> given;
        bool approvable;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var now = await scope.ServiceProvider.GetRequiredService<HephaistoDbContext>().CodeFixAttempts.AsNoTracking()
                .Where(a => a.Id == plan.AttemptId)
                .Select(a => new { a.State, a.CommandAnswers, a.CommandReactionId, a.NeedsCait })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (now is null || now.State != CodeFixState.PlanReady)
            {
                reactionReads.Remove(plan.AttemptId);
                return null;
            }

            cursor = now.CommandReactionId ?? 0;
            given = [.. (now.CommandAnswers ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            approvable = !now.NeedsCait;
        }

        var etag = reactionReads.TryGetValue(plan.AttemptId, out var known) ? known.ETag : null;
        var list = await client.ListCommentReactionsAsync(repository, planComment, etag, ct).ConfigureAwait(false);

        if (list.Outcome == GitHubOutcome.NotModified)
        {
            // Unchanged since a list that was acted on completely.
            return null;
        }

        if (list is not { Ok: true, Value: { } reactions })
        {
            reactionReads.Remove(plan.AttemptId);
            return $"{repository}#{plan.Number}: the reactions on its plan could not be read: {list.Describe()}";
        }

        if (reactions.Count >= GitHubClient.PageSize)
        {
            logger.LogWarning(
                "{Repository}#{Number}: the plan comment {CommentId} has {Count} reactions or more; only the oldest of them are read for an answer. A command in a comment still works.",
                repository, plan.Number, planComment, reactions.Count);
        }

        bool Own(GitHubReaction r) => string.Equals(r.Author.Login, bot, StringComparison.OrdinalIgnoreCase);

        var recorded = given.Count;
        var written = 1 + given.Count;
        var handled = cursor;
        var decided = false;
        string? problem = null;

        // Writes the one answer of a key, unless it was given or the ceiling is reached - both
        // of which are "said". False only when GitHub refused the write.
        async Task<bool> SayOnceAsync(GitHubReaction to, IssueCommand command, string key, string outcome, Func<string> text)
        {
            if (given.Contains(key, StringComparer.Ordinal))
            {
                return true;
            }

            if (written >= IssueComments.MaxPerAttempt)
            {
                logger.LogWarning(
                    "{Repository}#{Number}: not answering reaction {ReactionId} ({Answer}); Hephaisto has written {Written} comments for this attempt, which is its ceiling.",
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

            await AuditReactionAsync(repository, plan, to, command, outcome, key, ct).ConfigureAwait(false);
            return true;
        }

        foreach (var reaction in reactions.Where(r => r.Id > cursor && !Own(r)).OrderBy(r => r.Id))
        {
            // Six of GitHub's eight say nothing here, whoever set them.
            if (IssueCommands.ReadReaction(reaction.Content) is not { } command)
            {
                handled = reaction.Id;
                continue;
            }

            var login = Cap(reaction.Author.Login, 64);
            var verb = IssueCommands.Verb(command.Kind);

            if (!approvers.Contains(reaction.Author.Id))
            {
                if (!await SayOnceAsync(reaction, command, IssueComments.NotApproverKey, "not_approver", () => IssueComments.NotApprover(plan.AttemptId, login)).ConfigureAwait(false))
                {
                    // Not past this reaction: the next pass answers it.
                    break;
                }

                handled = reaction.Id;
                metrics.Command(verb, GitHubMetrics.CommandNotApprover);
                continue;
            }

            CodeFixDecisionResult decision;

            await using (var scope = scopes.CreateAsyncScope())
            {
                decision = await scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>()
                    .DecideForWorkItemAsync(
                        plan.WorkItemId, plan.AttemptId, command.Kind == IssueCommandKind.Approve, $"github:{login}", ApprovalSource.GitHub, authenticated: true, command.Reason, ct)
                    .ConfigureAwait(false);
            }

            if (decision.Outcome == CodeFixDecisionOutcome.Done)
            {
                var outcome = command.Kind == IssueCommandKind.Approve ? "approved" : "rejected";

                handled = reaction.Id;
                decided = true;
                metrics.Command(verb, GitHubMetrics.CommandAccepted);

                await AuditReactionAsync(repository, plan, reaction, command, outcome, null, ct).ConfigureAwait(false);

                logger.LogInformation(
                    "{Login} ({AccountId}) {Verb} the plan of {Repository}#{Number} with reaction {ReactionId} ({Content}): {Message}.",
                    login, reaction.Author.Id, outcome, repository, plan.Number, reaction.Id, reaction.Content, decision.Message);

                // This attempt has been decided: no later reaction on its comment is an answer.
                break;
            }

            var key = IssueComments.AnswerKey(decision.Refusal, decision.Mode);

            if (!await SayOnceAsync(reaction, command, key, "refused", () => IssueComments.Refused(plan.AttemptId, login, command.Kind, decision.Refusal, decision.Mode, byReaction: true)).ConfigureAwait(false))
            {
                break;
            }

            handled = reaction.Id;
            metrics.Command(verb, GitHubMetrics.CommandRefused(key));

            logger.LogInformation(
                "{Login} ({AccountId}) set {Content} on the plan of {Repository}#{Number} (reaction {ReactionId}) and was refused: {Message}.",
                login, reaction.Author.Id, reaction.Content, repository, plan.Number, reaction.Id, decision.Message);

            if (decision.Refusal is CodeFixRefusal.NotWaiting or CodeFixRefusal.SubjectTakenBack or CodeFixRefusal.NotFound)
            {
                // Somebody else answered it, or it is gone: no reaction can decide it.
                decided = true;
                break;
            }
        }

        if (handled != cursor || given.Count != recorded)
        {
            var answers = given.Count == 0 ? null : string.Join(",", given);

            await using var scope = scopes.CreateAsyncScope();

            // By statement, as the comment cursor is: the row is the coordinator's and the watcher's too.
            await scope.ServiceProvider.GetRequiredService<HephaistoDbContext>().CodeFixAttempts.Where(a => a.Id == plan.AttemptId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CommandReactionId, handled).SetProperty(a => a.CommandAnswers, answers), ct)
                .ConfigureAwait(false);
        }

        if (problem is not null)
        {
            reactionReads.Remove(plan.AttemptId);
            return problem;
        }

        if (decided)
        {
            // Nothing is offered on a plan that has been answered; what is there stays.
            reactionReads.Remove(plan.AttemptId);
            return null;
        }

        // The two an approver clicks: whichever is not there (any more). After the answers, so
        // that a token that may not set a reaction still reads them.
        var refused = await OfferReactionsAsync(
            client, repository, planComment, approvable, [.. reactions.Where(Own).Select(r => r.Content)], ct).ConfigureAwait(false);

        if (refused is not null)
        {
            // No tag is kept, so the next pass reads the list again and tries again.
            reactionReads.Remove(plan.AttemptId);
            return $"{repository}#{plan.Number}: a reaction to click could not be set on its plan: {refused}";
        }

        // A list that was acted on completely. After setting one the list has changed, and the
        // tag with it: the next pass reads it once more and finds nothing to do.
        reactionReads[plan.AttemptId] = (repository, list.ETag);

        return null;
    }

    /// <summary>
    /// Sets, as Hephaisto's own, the reactions an approver clicks on a plan comment: 🚀 and 👎,
    /// or 👎 alone on a plan that cannot be approved here. Whatever of them is in
    /// <paramref name="present"/> is left alone; setting one twice would not be an error either.
    /// </summary>
    /// <returns>Null when both are there now; otherwise what GitHub said to the first it refused.</returns>
    private static async Task<string?> OfferReactionsAsync(
        IGitHubClient client, string repository, long planComment, bool approvable, IReadOnlyCollection<string> present, CancellationToken ct)
    {
        foreach (var content in new[] { IssueCommands.ApproveReaction, IssueCommands.RejectReaction })
        {
            if ((content == IssueCommands.ApproveReaction && !approvable) || present.Contains(content, StringComparer.Ordinal))
            {
                continue;
            }

            var set = await client.AddCommentReactionAsync(repository, planComment, content, ct).ConfigureAwait(false);

            if (!set.Ok)
            {
                return set.Describe();
            }
        }

        return null;
    }

    /// <summary>
    /// The row a command's is (<see cref="AuditCommandAsync"/>), for an answer given by reaction:
    /// one per reaction that led to something - a decision, or an answer Hephaisto wrote.
    /// </summary>
    private async Task AuditReactionAsync(
        string repository, Waiting plan, GitHubReaction reaction, IssueCommand command, string outcome, string? answer, CancellationToken ct)
    {
        var login = Cap(reaction.Author.Login, 64);
        var how = $"a {reaction.Content} reaction";

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        db.AuditEvents.Add(new AuditEvent
        {
            At = clock.UtcNow,
            Type = AuditCommand,
            Actor = IncidentStateMachine.SystemActor,
            Summary = outcome switch
            {
                "approved" => $"{login} ({reaction.Author.Id}) approved the plan of {repository}#{plan.Number} with {how}",
                "rejected" => $"{login} ({reaction.Author.Id}) rejected the plan of {repository}#{plan.Number} with {how}",
                "not_approver" => $"{how} on the plan of {repository}#{plan.Number} by {login} ({reaction.Author.Id}) did not count: not an approver",
                _ => $"{how} on the plan of {repository}#{plan.Number} by {login} ({reaction.Author.Id}) was refused: {answer}",
            },
            Detail = JsonSerializer.Serialize(new
            {
                work_item_id = plan.WorkItemId,
                attempt_id = plan.AttemptId,
                source = WorkItem.GitHubSource,
                repository,
                number = plan.Number,
                comment_id = plan.PlanCommentId,
                reaction_id = reaction.Id,
                reaction = reaction.Content,
                login,
                account_id = reaction.Author.Id,
                command = IssueCommands.Word(command.Kind),
                outcome,
                answer,
            }),
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
