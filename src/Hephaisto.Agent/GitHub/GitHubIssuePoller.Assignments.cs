using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// The fifth statement of a pass (v0.14.0): <b>an issue whose attempt has ended has been asked
/// whether it was assigned again.</b> A fresh assignment is recognised by its TIME on GitHub,
/// not by a poll having seen the issue unassigned in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> After a failed attempt the issue said "unassign Hephaisto and assign it again".
/// On 2026-10-08 somebody did, on the first real issue: unassigned at 09:28:53 UTC, assigned
/// again at 09:29:00, with a poll once a minute. Every poll saw an assigned issue, so the work
/// item stayed as it was, with its one failed attempt, and nothing followed. An unassignment
/// exists for the list comparison only when a poll falls into it.
/// </para>
/// <para>
/// <b>The rule.</b> For a taken work item whose NEWEST attempt has ended without a pull request
/// (failed, denied, expired, cancelled), the issue's timeline is read, and an <c>assigned</c>
/// event for Hephaisto's account that is newer than that attempt's end is a new hand-over: a
/// new attempt for the same work item, as an approver's <c>/replan</c> without an answer
/// (<see cref="CodeFixCoordinator.HandOverAgainAsync"/>). The issue's text is read again, as
/// it is for a hand-over that a poll did see.
/// </para>
/// <para>
/// <b>A waiting plan is left alone</b>, and so is a running Job and an open pull request:
/// assigning an issue again does not answer its plan. For those the timeline is not read at
/// all - an install whose issues are all waiting, running or under review asks GitHub nothing
/// more than it did. For the others it is one conditional request per issue and pass, which an
/// unchanged timeline answers with a 304.
/// </para>
/// <para>
/// <b>Two clocks.</b> The attempt's end is this process's time and the event's is GitHub's, in
/// whole seconds. So the end is taken down to its second - an assignment in the second an
/// attempt ended counts - and the newest assignment that was acted on is kept on the work item
/// (<see cref="WorkItem.AssignmentSeenAt"/>), which makes one assignment one hand-over however
/// the clocks differ. A clock that runs ahead of GitHub's by a few seconds misses an assignment
/// made within those seconds of the end; the person assigns once more, or replies
/// <c>/replan</c>.
/// </para>
/// </remarks>
public sealed partial class GitHubIssuePoller
{
    private sealed record Ended(Guid WorkItemId, int Number, Guid AttemptId, DateTimeOffset EndedAt, DateTimeOffset? AssignmentSeenAt);

    /// <summary>The tag of the last timeline that held nothing new, by work item. Touched by the one loop.</summary>
    private readonly Dictionary<Guid, (string Repository, string ETag)> assignmentTags = [];

    /// <summary>
    /// Whether an assignment is a new hand-over: not before the second the newest attempt ended
    /// in, and later than the last one that was acted on.
    /// </summary>
    public static bool IsNewHandOver(DateTimeOffset assignedAt, DateTimeOffset attemptEndedAt, DateTimeOffset? lastActedOn) =>
        assignedAt >= DateTimeOffset.FromUnixTimeSeconds(attemptEndedAt.ToUnixTimeSeconds())
        && (lastActedOn is null || assignedAt > lastActedOn);

    /// <returns>Null when every such issue of the repository was asked about; otherwise the first that could not be.</returns>
    private async Task<string?> AssignedAgainAsync(IGitHubClient client, string repository, string bot, CancellationToken ct)
    {
        List<Ended> ended;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            // The newest attempt of everything taken, a handful of rows - and of those the ones
            // that ended without a pull request, are not at the ceiling of attempts, and after
            // which nothing was asked for yet.
            ended = (await db.CodeFixAttempts.AsNoTracking()
                    .Where(a => a.WorkItem != null
                        && a.WorkItem.Source == WorkItem.GitHubSource
                        && a.WorkItem.Repository == repository
                        && a.WorkItem.State == WorkItemState.Taken)
                    .OrderBy(a => a.CreatedAt)
                    .ThenBy(a => a.Id)
                    .Select(a => new
                    {
                        WorkItemId = a.WorkItemId!.Value,
                        a.WorkItem!.Number,
                        a.Id,
                        a.State,
                        EndedAt = a.FinishedAt ?? a.CreatedAt,
                        a.WorkItem.AssignmentSeenAt,
                        a.WorkItem.ReplanAfterAttemptId,
                    })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .GroupBy(a => a.WorkItemId)
                .Where(g => g.Count() < WorkItem.MaxAttempts)
                .Select(g => g.Last())
                .Where(a => a.State is CodeFixState.Failed or CodeFixState.Denied or CodeFixState.Expired or CodeFixState.Cancelled
                    && a.ReplanAfterAttemptId != a.Id)
                .Select(a => new Ended(a.WorkItemId, a.Number, a.Id, a.EndedAt, a.AssignmentSeenAt))
                .ToList();
        }

        foreach (var gone in assignmentTags.Where(t => t.Value.Repository == repository && ended.TrueForAll(e => e.WorkItemId != t.Key)).Select(t => t.Key).ToList())
        {
            assignmentTags.Remove(gone);
        }

        string? problem = null;

        foreach (var item in ended)
        {
            var tag = assignmentTags.TryGetValue(item.WorkItemId, out var known) ? known.ETag : null;
            var timeline = await client.ListAssignmentsAsync(repository, item.Number, tag, ct).ConfigureAwait(false);

            if (timeline.Outcome == GitHubOutcome.NotModified)
            {
                continue;
            }

            if (timeline is not { Ok: true, Value: { } assignments })
            {
                assignmentTags.Remove(item.WorkItemId);
                problem ??= $"{repository}#{item.Number}: its timeline could not be read for a new assignment: {timeline.Describe()}";
                continue;
            }

            var newest = assignments
                .Where(a => string.Equals(a.Assignee.Login, bot, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.At)
                .LastOrDefault();

            if (newest is not null && IsNewHandOver(newest.At, item.EndedAt, item.AssignmentSeenAt))
            {
                // A hand-over: the issue is read as it is now, as for one a poll did see.
                var issue = await client.GetIssueAsync(repository, item.Number, ct).ConfigureAwait(false);

                if (issue is not { Ok: true, Value: { } now })
                {
                    assignmentTags.Remove(item.WorkItemId);
                    problem ??= $"{repository}#{item.Number}: the issue could not be read again after it was assigned again: {issue.Describe()}";
                    continue;
                }

                // Assigned again and taken back since, or closed: the list comparison's to
                // record, on this pass or the next. Nothing is planned for it.
                if (now.IsOpen && now.IsAssignedTo(bot))
                {
                    await using var scope = scopes.CreateAsyncScope();

                    var asked = await scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>()
                        .HandOverAgainAsync(item.WorkItemId, item.AttemptId, newest.Actor is null ? null : Cap(newest.Actor, 64), newest.At, new IssueText(now.Title, now.Body), ct)
                        .ConfigureAwait(false);

                    logger.LogInformation(
                        "{Repository}#{Number} was assigned to {Bot} again at {AssignedAt:O}, after its attempt {AttemptId} had ended: {Message}.",
                        repository, item.Number, bot, newest.At, item.AttemptId, asked.Message);
                }
            }

            // Kept for an answer after which there is nothing left to do - and a long timeline
            // comes without one.
            if (timeline.ETag is { Length: > 0 } fresh)
            {
                assignmentTags[item.WorkItemId] = (repository, fresh);
            }
            else
            {
                assignmentTags.Remove(item.WorkItemId);
            }
        }

        return problem;
    }
}
