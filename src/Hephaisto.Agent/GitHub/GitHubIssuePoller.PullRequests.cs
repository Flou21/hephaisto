using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// The first statement of a pass, before the list of assigned issues is even compared (v0.14.0):
/// <b>a work item whose pull request is no longer open has ended.</b> Merged, and it is
/// <see cref="WorkItemState.Done"/>; closed without merging, and it is
/// <see cref="WorkItemState.Cancelled"/> with <see cref="WorkItemReasons.PullRequestClosed"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>First, because of what a merge does to the issue.</b> A pull request whose description says
/// <c>Closes owner/repo#n</c> closes that issue when it is merged, and a closed issue is no longer
/// in the list of assigned ones - which, read first, is "the issue was closed: cancelled". The
/// same event would be recorded as its opposite. So the pull request is read before the list,
/// and once more for a work item that the list says is gone, in case the merge happened between
/// the two reads: a merge is never recorded as a cancellation.
/// </para>
/// <para>
/// <b>Cheap for as long as a review takes.</b> A pull request waits for days. Each is asked for
/// with the tag of the last answer that said "open", so an unchanged one is a 304 and costs
/// nothing against the rate limit. The tag is memory only, and kept only for an answer after
/// which there was nothing to do.
/// </para>
/// <para>
/// <b>What ended is not taken again by itself.</b> An issue GitHub did not close - the pull
/// request went to another branch than the default one, or was closed unmerged - is still open
/// and still assigned when its work item ends, and "a listed issue with no taken work item is
/// taken" would start it over on the next pass, for ever. Such a work item is marked
/// <see cref="WorkItem.StillAssigned"/>; the list comparison leaves its issue alone until one
/// complete list did not hold it, and only an assignment after that is new work.
/// </para>
/// </remarks>
public sealed partial class GitHubIssuePoller
{
    public const string AuditDone = "workitem.done";

    private sealed record Followed(Guid WorkItemId, int Number, Guid AttemptId, int? PrNumber, string? PrUrl);

    private enum PullRequestFate
    {
        /// <summary>Open, or there is no pull request to ask about. Nothing ended.</summary>
        Open = 0,
        Merged = 1,
        Closed = 2,

        /// <summary>GitHub did not say. Nothing may be concluded from it - least of all a cancellation.</summary>
        Unknown = 3,
    }

    /// <summary>The tag of the last answer that said "open", by attempt. Touched by the one loop.</summary>
    private readonly Dictionary<Guid, (string Repository, string ETag)> pullRequestTags = [];

    /// <returns>Null when every open pull request of the repository's work items was asked about; otherwise the first that could not be.</returns>
    private async Task<string?> FollowPullRequestsAsync(IGitHubClient client, string repository, CancellationToken ct)
    {
        List<Followed> open;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            open = await db.CodeFixAttempts.AsNoTracking()
                .Where(a => a.State == CodeFixState.PrOpened
                    && a.WorkItem != null
                    && a.WorkItem.Source == WorkItem.GitHubSource
                    && a.WorkItem.Repository == repository
                    && a.WorkItem.State == WorkItemState.Taken)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new Followed(a.WorkItemId!.Value, a.WorkItem!.Number, a.Id, a.PrNumber, a.PrUrl))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        foreach (var gone in pullRequestTags.Where(t => t.Value.Repository == repository && open.TrueForAll(f => f.AttemptId != t.Key)).Select(t => t.Key).ToList())
        {
            pullRequestTags.Remove(gone);
        }

        string? problem = null;

        foreach (var followed in open)
        {
            // Still assigned, as far as anything says: it was in no list as taken back, and the
            // list read next clears the mark if the issue is not in it.
            problem ??= (await FollowAsync(client, repository, followed, conditional: true, stillAssigned: true, ct).ConfigureAwait(false)).Problem;
        }

        return problem;
    }

    /// <summary>The pull request of a taken work item, if its attempt opened one. For a work item the list says is gone.</summary>
    private async Task<Followed?> PullRequestOfAsync(Guid workItemId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        return await db.CodeFixAttempts.AsNoTracking()
            .Where(a => a.WorkItemId == workItemId && a.State == CodeFixState.PrOpened && a.WorkItem != null)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new Followed(a.WorkItemId!.Value, a.WorkItem!.Number, a.Id, a.PrNumber, a.PrUrl))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Asks what became of one pull request, and ends its work item when it is no longer open.</summary>
    /// <param name="conditional">Send the tag of the last "open" answer. Not for the second read of a pass, which must be a real one.</param>
    /// <param name="stillAssigned">What to record with an ending: whether the issue is, as far as the caller knows, still assigned.</param>
    private async Task<(PullRequestFate Fate, string? Problem)> FollowAsync(
        IGitHubClient client, string repository, Followed followed, bool conditional, bool stillAssigned, CancellationToken ct)
    {
        if (PullRequestNumber(followed) is not { } number)
        {
            // An attempt is PrOpened only with an address that was held to the repository's own
            // /pull/ path, so this is a row from somewhere else. Nothing to ask about.
            return (PullRequestFate.Open, null);
        }

        var tag = conditional && pullRequestTags.TryGetValue(followed.AttemptId, out var known) ? known.ETag : null;
        var asked = await client.GetPullRequestAsync(repository, number, tag, ct).ConfigureAwait(false);

        if (asked.Outcome == GitHubOutcome.NotModified)
        {
            return (PullRequestFate.Open, null);
        }

        if (asked is not { Ok: true, Value: { } pull })
        {
            pullRequestTags.Remove(followed.AttemptId);

            return (PullRequestFate.Unknown,
                $"{repository}#{followed.Number}: its pull request #{number.ToString(CultureInfo.InvariantCulture)} could not be read: {asked.Describe()}");
        }

        if (pull.Merged)
        {
            pullRequestTags.Remove(followed.AttemptId);
            await EndAsync(followed.WorkItemId, WorkItemState.Done, WorkItemReasons.Merged, GitHubMetrics.ReasonMerged, stillAssigned, ct).ConfigureAwait(false);

            return (PullRequestFate.Merged, null);
        }

        if (!pull.IsOpen)
        {
            pullRequestTags.Remove(followed.AttemptId);
            await EndAsync(
                    followed.WorkItemId, WorkItemState.Cancelled, WorkItemReasons.PullRequestClosed, GitHubMetrics.ReasonPullRequestClosed, stillAssigned, ct)
                .ConfigureAwait(false);

            return (PullRequestFate.Closed, null);
        }

        if (asked.ETag is { Length: > 0 } fresh)
        {
            pullRequestTags[followed.AttemptId] = (repository, fresh);
        }
        else
        {
            pullRequestTags.Remove(followed.AttemptId);
        }

        return (PullRequestFate.Open, null);
    }

    /// <summary>The number the runner reported, or - from a runner that reported none - the one in the address it was held to.</summary>
    private static int? PullRequestNumber(Followed followed)
    {
        if (followed.PrNumber is > 0 and var reported)
        {
            return reported;
        }

        return followed.PrUrl is { Length: > 0 } url
            && PullPath().Match(url) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
                ? parsed
                : null;
    }

    [GeneratedRegex(@"/pull/(\d{1,9})/?$")]
    private static partial Regex PullPath();
}
