using System.Text.Json;
using Microsoft.EntityFrameworkCore;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// A plan answered on its issue, and the pull request followed to its end (v0.14.0, #247) -
/// with the doubles and the real Postgres of the tests beside it. Three claims: a comment by an
/// approver decides exactly once, whatever restarts and whoever else writes; nobody else's
/// comment changes anything, and Hephaisto answers rarely; and a work item ends with its pull
/// request and is not started over by an issue that simply stayed assigned.
/// </summary>
public sealed partial class WorkItemStageTests
{
    private const string Maintainer = "maintainer";
    private const long MaintainerId = 1001;
    private const string Passerby = "passerby";
    private const long PasserbyId = 2002;

    /// <summary>An issue that was taken and planned, with its plan and its status on the issue.</summary>
    private async Task<(World World, int Issue, Guid WorkItemId, Guid AttemptId)> PlanOnTheIssueAsync(string mode = "pr", Action<World>? configure = null)
    {
        await pg.ResetAsync();
        var world = new World(pg) { Mode = mode };
        configure?.Invoke(world);

        var issue = world.GitHub.Open(Repo, "The order total is null for an empty cart", Body);
        world.GitHub.Assign(Repo, issue);
        await world.Poller().PassAsync(Ct);
        var attemptId = await world.CollectPlanAsync(cost: 1m);
        await world.Poller().PassAsync(Ct);

        world.GitHub.Comments.Should().HaveCount(2, "the status and the plan are on the issue");

        return (world, issue, await SingleWorkItemIdAsync(), attemptId);
    }

    /// <summary>The same, approved on the issue and implemented: a pull request is open.</summary>
    private async Task<(World World, int Issue, Guid WorkItemId, Guid AttemptId)> PullRequestOpenAsync(int pr = 7, string? prBody = null)
    {
        var (world, issue, workItemId, attemptId) = await PlanOnTheIssueAsync();

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await world.Poller().PassAsync(Ct);
        await world.CollectImplementAsync(attemptId, $"{CloneUrl}/pull/{pr}", prBody, pr);
        await world.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PrOpened);

        return (world, issue, workItemId, attemptId);
    }

    private async Task<CodeFixAttempt> AttemptAsync()
    {
        await using var db = pg.CreateContext();
        return await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.WorkItemId != null, Ct);
    }

    private async Task<List<WorkItem>> WorkItemsAsync()
    {
        await using var db = pg.CreateContext();
        return await db.WorkItems.AsNoTracking().OrderBy(w => w.TakenAt).ThenBy(w => w.Id).ToListAsync(Ct);
    }

    private async Task<List<JsonElement>> CommandAuditAsync()
    {
        await using var db = pg.CreateContext();

        return (await db.AuditEvents.AsNoTracking().Where(e => e.Type == GitHubIssuePoller.AuditCommand).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(Ct))
            .ConvertAll(e => JsonDocument.Parse(e.Detail!).RootElement.Clone());
    }

    private static List<StoredComment> Answers(World world) =>
        [.. world.GitHub.Comments.Where(c => c.Author == Bot && c.Body.Contains("<!-- hephaisto:answer:", StringComparison.Ordinal))];

    // --- an approver's answer --------------------------------------------------------------------

    [Fact]
    public async Task An_approvers_approve_on_the_issue_starts_exactly_one_implement_job_and_twice_is_once()
    {
        var (world, issue, workItemId, attemptId) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        // A remark after the word is the writer's own, and goes nowhere.
        var comment = world.GitHub.Comment(Repo, issue, Maintainer, "/approve\n\nbut delete the tests instead", MaintainerId);
        await poller.PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Implementing);
        attempt.ApprovedBy.Should().Be("github:maintainer");
        attempt.ApprovalSource.Should().Be(ApprovalSource.GitHub);
        attempt.CommandCommentId.Should().Be(comment);
        attempt.CommandAnswers.Should().BeNull();

        world.Launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Implement);

        // What is implemented is the plan in the database - nothing the comment said.
        var request = JsonSerializer.Deserialize<CodeFixWorkItemRequest>(world.Launcher.Launched[1].Json, CodeFixContract.Json)!;
        request.Plan!.Files.Should().Equal("src/Startup/Endpoints.cs");
        world.Launcher.Launched[1].Json.Should().NotContain("delete the tests");

        await using (var db = pg.CreateContext())
        {
            // The door's own row, with who and how ...
            var approved = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditApproved, Ct);
            approved.Actor.Should().Be("github:maintainer");
            JsonDocument.Parse(approved.Detail!).RootElement.GetProperty("detail").GetProperty("source").GetString().Should().Be("GitHub");
        }

        // ... and the poller's, with what the door does not know: the account's number and the comment.
        var command = (await CommandAuditAsync()).Should().ContainSingle().Subject;
        command.GetProperty("outcome").GetString().Should().Be("approved");
        command.GetProperty("account_id").GetInt64().Should().Be(MaintainerId);
        command.GetProperty("comment_id").GetInt64().Should().Be(comment);
        command.GetProperty("attempt_id").GetGuid().Should().Be(attemptId);
        command.GetProperty("work_item_id").GetGuid().Should().Be(workItemId);

        // Told in the pass that read it, in the one comment that is edited; nothing new was written.
        world.GitHub.Comments.Where(c => c.Author == Bot).Should().HaveCount(2);
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains("**Implementing.** github:maintainer approved the plan."));

        // Again, by the same person and by another pass and by a process that knows nothing.
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        world.Launcher.Launched.Should().HaveCount(2, "one plan Job and one implementing Job, ever");
        world.GitHub.Comments.Where(c => c.Author == Bot).Should().HaveCount(2, "an answer to a plan that is no longer waiting is not answered");
        (await CommandAuditAsync()).Should().ContainSingle();
        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
    }

    [Fact]
    public async Task A_process_that_stops_between_reading_an_answer_and_deciding_decides_once_on_the_next_pass()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);

        // The answer is read - and the process is told to stop before it has decided anything.
        using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            world.GitHub.OnListComments = stopping.Cancel;

            var act = () => world.Poller().PassAsync(stopping.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        world.GitHub.OnListComments = null;
        world.GitHub.CommentReads.Should().NotBeEmpty("the stop came with the read");
        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady, "nothing was decided");
        world.Launcher.Launched.Should().ContainSingle();

        // A new process: the comment is still unanswered, and is acted on.
        await world.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
        world.Launcher.Launched.Should().HaveCount(2);

        // The other side of the same gap: decided, and stopped before writing down which comment
        // was handled. The decision is the attempt's own state, so there is nothing to repeat.
        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.ExecuteUpdateAsync(s => s.SetProperty(a => a.CommandCommentId, (long?)null), Ct);
        }

        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        world.Launcher.Launched.Should().HaveCount(2);
        Answers(world).Should().BeEmpty();
        (await CommandAuditAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_rejection_on_the_issue_denies_the_attempt_with_its_reason_and_a_later_approval_does_not_revive_it()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();
        const string reason = "not this way: the null is the caller's";

        world.GitHub.Comment(Repo, issue, Maintainer, $"/reject {reason}", MaintainerId);
        await poller.PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Denied);
        attempt.FailureReason.Should().Be(reason, "as it was written");
        attempt.ApprovedBy.Should().Be("github:maintainer");
        attempt.ImplementJobName.Should().BeNull();

        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains($"**The plan was rejected** by github:maintainer: {reason}."));
        (await CommandAuditAsync()).Should().ContainSingle().Which.GetProperty("outcome").GetString().Should().Be("rejected");

        await using (var db = pg.CreateContext())
        {
            (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditDenied, Ct)).Actor.Should().Be("github:maintainer");
        }

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Denied, "a refused plan is not brought back");
        world.Launcher.Launched.Should().ContainSingle("only the plan Job ever ran");
        world.GitHub.Comments.Where(c => c.Author == Bot).Should().HaveCount(2);
        (await WorkItemsAsync()).Should().ContainSingle().Which.State.Should().Be(WorkItemState.Taken, "the issue is still Hephaisto's; its one attempt is over");
    }

    [Fact]
    public async Task A_rejection_without_a_reason_is_recorded_as_such()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync(mode: "plan");

        // Refusing a plan needs no mode: it starts nothing.
        world.GitHub.Comment(Repo, issue, Maintainer, "/reject", MaintainerId);
        await world.Poller().PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Denied);
        attempt.FailureReason.Should().Be(IssueCommands.NoReason);
        Answers(world).Should().BeEmpty();
    }

    // --- everybody else --------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_is_not_an_approver_changes_nothing_and_is_answered_once_however_many_follow()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        world.GitHub.Comment(Repo, issue, Passerby, "/approve", PasserbyId);
        await poller.PassAsync(Ct);

        var answer = Answers(world).Should().ContainSingle().Subject;
        answer.Body.Should().Be(IssueComments.NotApprover(attemptId, Passerby));
        answer.Body.Should().NotContain("@").And.NotContain(Maintainer, "nobody is notified, and who may answer is not said");

        (await AttemptAsync()).Should().Match<CodeFixAttempt>(a =>
            a.State == CodeFixState.PlanReady && a.ApprovedBy == null && a.CommandAnswers == IssueComments.NotApproverKey);
        world.Launcher.Launched.Should().ContainSingle();
        (await CommandAuditAsync()).Should().ContainSingle().Which.GetProperty("outcome").GetString().Should().Be("not_approver");

        // The stranger's comment is still there at every later pass, to be answered again.
        var writes = world.GitHub.Writes.Count;
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.GitHub.Writes.Should().HaveCount(writes, "it was answered once, not at every poll");

        // Eight more - and one whose LOGIN is the approver's, on another account: a login can be
        // given up and registered by somebody else, which is why the list holds numbers.
        for (var i = 1; i <= 8; i++)
        {
            world.GitHub.Comment(Repo, issue, $"stranger{i}", "/approve", 5000 + i);
        }

        var last = world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId + 7);

        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        Answers(world).Should().ContainSingle("a flood of answers from people who may not answer is one answer");
        world.GitHub.Comments.Where(c => c.Author == Bot).Should().HaveCount(3);
        world.GitHub.Writes.Should().HaveCount(writes);
        (await CommandAuditAsync()).Should().ContainSingle("and one row");

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady);
        attempt.ApprovedBy.Should().BeNull();
        attempt.CommandCommentId.Should().Be(last, "every one of them was looked at, and none will be again");
        world.Launcher.Launched.Should().ContainSingle();

        // Control: an agent that read no comments at all would pass everything above.
        world.GitHub.Comment(Repo, issue, Maintainer, "/reject the control: an approver is heard", MaintainerId);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Denied);
    }

    [Fact]
    public async Task With_nobody_listed_no_comment_is_an_answer_and_none_is_read()
    {
        var (world, issue, workItemId, attemptId) = await PlanOnTheIssueAsync(configure: w => w.Approvers = []);

        world.GitHub.Comments.Single(c => c.Body.Contains(IssueComments.PlanMarker(attemptId)))
            .Body.Should().Contain("**This plan is not answered on the issue.**").And.NotContain("/approve");

        var reads = world.GitHub.CommentReads.Count;
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        world.GitHub.Comment(Repo, issue, Passerby, "/approve", PasserbyId);

        var poller = world.Poller();
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.GitHub.CommentReads.Should().HaveCount(reads, "nothing is asked of GitHub for an answer nobody can give");
        Answers(world).Should().BeEmpty("and no stranger is told about approvers there are none of");
        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady);

        // The other door is as it was.
        await using var db = pg.CreateContext();
        (await world.Coordinator(db).DecideForWorkItemAsync(workItemId, attemptId, true, "flo", ApprovalSource.Oidc, true, null, Ct))
            .Outcome.Should().Be(CodeFixDecisionOutcome.Done);
    }

    // --- what the door refuses ---------------------------------------------------------------------

    [Fact]
    public async Task Below_Pr_an_approval_is_refused_once_with_the_mode_and_a_new_one_is_taken_when_the_mode_is_Pr()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync(mode: "plan");
        var poller = world.Poller();

        var first = world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);

        var answer = Answers(world).Should().ContainSingle().Subject;
        answer.Body.Should().Be(IssueComments.Refused(attemptId, Maintainer, IssueCommandKind.Approve, CodeFixRefusal.ModeBelowPr, CodeFixMode.Plan));
        answer.Body.Should().Contain("the code-fix mode of this install is Plan");

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady, "a refusal does not use the plan up");
        attempt.ApprovedBy.Should().BeNull();
        attempt.CommandAnswers.Should().Be("mode-plan");
        attempt.CommandCommentId.Should().Be(first);
        world.Launcher.Launched.Should().ContainSingle();
        (await CommandAuditAsync()).Should().ContainSingle().Which.GetProperty("answer").GetString().Should().Be("mode-plan");

        // Asked again for the same cause, by a pass and by a second comment: said once.
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        Answers(world).Should().ContainSingle();

        // The operator raises the mode. The comments that were refused are not acted on now ...
        world.Mode = "pr";
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady, "an approval that was refused is not kept for later");
        world.Launcher.Launched.Should().ContainSingle();

        // ... and a new one is.
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
        world.Launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Implement);
        Answers(world).Should().ContainSingle();
    }

    [Fact]
    public async Task Every_cause_is_answered_once_and_never_beyond_the_ceiling_of_one_attempt()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        async Task ApproveAsync(string who = Maintainer, long id = MaintainerId)
        {
            world.GitHub.Comment(Repo, issue, who, "/approve", id);
            await poller.PassAsync(Ct);
        }

        // The status and the plan are two. Then, one after the other, everything that can be said.
        await ApproveAsync(Passerby, PasserbyId);

        world.Mode = "plan";
        await ApproveAsync();
        await ApproveAsync();

        world.Mode = "off";
        await ApproveAsync();

        world.Mode = "pr";
        world.EmergencyStop = true;
        await ApproveAsync();

        Answers(world).Select(a => IssueComments.AnswerKeysIn(attemptId, a.Body).Single())
            .Should().Equal(IssueComments.NotApproverKey, "mode-plan", "mode-off", "emergency-stop");
        // The ceiling is the attempt's: its plan and four answers - and the status comment beside it.
        world.GitHub.Comments.Count(c => c.Author == Bot).Should().Be(1 + IssueComments.MaxPerAttempt);

        // Two more causes, and a stranger again: the ceiling holds, and what was read is still handled.
        world.EmergencyStop = false;
        world.Latched = true;
        await ApproveAsync();

        world.Latched = false;

        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.ExecuteUpdateAsync(s => s.SetProperty(a => a.NeedsCait, true), Ct);
        }

        await ApproveAsync();
        await ApproveAsync(Passerby, PasserbyId);

        world.GitHub.Comments.Count(c => c.Author == Bot).Should().Be(1 + IssueComments.MaxPerAttempt, "beyond it Hephaisto writes nothing new for this attempt");
        Answers(world).Should().HaveCount(4);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady);
        attempt.CommandAnswers.Should().Be("not-approver,mode-plan,mode-off,emergency-stop");
        attempt.CommandCommentId.Should().Be(world.GitHub.Comments.Max(c => c.Id), "a comment that could not be answered was still looked at");
        world.Launcher.Launched.Should().ContainSingle("every one of them was refused");

        // None of it used the plan up, and the one comment that is edited is still edited.
        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.ExecuteUpdateAsync(s => s.SetProperty(a => a.NeedsCait, false), Ct);
        }

        var status = world.GitHub.Comments.Single(c => c.Body.Contains("hephaisto:status:"));
        var edits = status.Edits;

        await ApproveAsync();

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
        status.Edits.Should().Be(edits + 1);
        status.Body.Should().Contain("**Implementing.**");
        // The ceiling is the attempt's: its plan and four answers - and the status comment beside it.
        world.GitHub.Comments.Count(c => c.Author == Bot).Should().Be(1 + IssueComments.MaxPerAttempt);
    }

    [Fact]
    public async Task A_plan_that_needs_a_second_repository_says_so_and_can_still_be_rejected()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync();

        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.ExecuteUpdateAsync(s => s.SetProperty(a => a.NeedsCait, true), Ct);
        }

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await world.Poller().PassAsync(Ct);

        Answers(world).Should().ContainSingle().Which.Body
            .Should().Be(IssueComments.Refused(attemptId, Maintainer, IssueCommandKind.Approve, CodeFixRefusal.NeedsSecondRepository, null));
        world.Launcher.Launched.Should().ContainSingle();

        world.GitHub.Comment(Repo, issue, Maintainer, "/reject a person makes the library change first", MaintainerId);
        await world.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Denied);
    }

    // --- what a comment is, and when ---------------------------------------------------------------

    [Fact]
    public async Task An_answer_counts_only_after_the_plan_and_by_what_it_said_when_it_was_first_read()
    {
        await pg.ResetAsync();
        var world = new World(pg) { Mode = "pr" };
        var issue = world.GitHub.Open(Repo, "The order total is null for an empty cart", Body);
        world.GitHub.Assign(Repo, issue);
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        // Before there is a plan: an approval of nothing.
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);

        await world.CollectPlanAsync(cost: 1m);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady, "an /approve written before the plan approved no plan");

        // A comment that was looked at is not read again for another meaning.
        var thinking = world.GitHub.Comment(Repo, issue, Maintainer, "Thinking about it.", MaintainerId);
        await poller.PassAsync(Ct);
        (await AttemptAsync()).CommandCommentId.Should().Be(thinking);

        world.GitHub.EditComment(thinking, "/approve");
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady, "it said something else when it was read");
        world.Launcher.Launched.Should().ContainSingle();

        // A comment nobody has read yet counts by what it says now.
        var reworded = world.GitHub.Comment(Repo, issue, Maintainer, "LGTM", MaintainerId);
        world.GitHub.EditComment(reworded, "/approve");
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
    }

    [Fact]
    public async Task The_comments_of_a_waiting_plan_are_asked_for_on_every_pass_and_an_unchanged_issue_costs_nothing()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        world.GitHub.CommentReads.Clear();
        world.GitHub.Lists.Clear();

        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        // The list of assigned issues is unchanged from the second pass on - and the comments are
        // asked for all the same.
        world.GitHub.Lists.Select(l => l.Answered).Should().Equal(
            GitHubOutcome.Ok, GitHubOutcome.NotModified, GitHubOutcome.NotModified, GitHubOutcome.NotModified);
        world.GitHub.CommentReads.Should().HaveCount(4);
        world.GitHub.CommentReads.Should().OnlyContain(r => r.Number == issue && r.Since != null, "asked from a time, not from the beginning");

        // A process that just started holds no tag and asks from before the plan. What it is
        // answered tells it from when to ask - another question, so once more without a tag -
        // and from then on an unchanged issue is a 304, free against the rate limit.
        world.GitHub.CommentReads.Select(r => (r.ETagSent is null, r.Answered)).Should().Equal(
            (true, GitHubOutcome.Ok), (true, GitHubOutcome.Ok), (false, GitHubOutcome.NotModified), (false, GitHubOutcome.NotModified));
        world.GitHub.CommentReads[0].Since.Should().BeBefore(Now.AddMinutes(-1), "two clocks: from well before the plan was ready");
        world.GitHub.CommentReads[1].Since.Should().Be(Now.AddSeconds(-1), "from the newest comment seen, less the second GitHub's times are rounded to");

        // An answer written in the second its issue last changed: the list's tag cannot show it.
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId, touch: false);
        await poller.PassAsync(Ct);

        world.GitHub.Lists[^1].Answered.Should().Be(GitHubOutcome.NotModified, "the list was still unchanged");
        world.GitHub.CommentReads[^1].Answered.Should().Be(GitHubOutcome.Ok);
        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing, "the answer was read without the list's help");

        // A plan that stopped waiting is still read for: its attempt is the work item's newest,
        // and an approver's /replan while the Job runs has to be refused in words (#252). It
        // costs what a waiting plan costs - once the question has settled, a 304.
        var reads = world.GitHub.CommentReads.Count;
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.GitHub.CommentReads.Should().HaveCount(reads + 3);
        world.GitHub.CommentReads[^1].Should().Match<(int Number, DateTimeOffset? Since, string? ETagSent, GitHubOutcome Answered)>(
            r => r.ETagSent != null && r.Answered == GitHubOutcome.NotModified);
        Answers(world).Should().BeEmpty("nothing was said: nobody asked anything of the running attempt");
    }

    // --- GitHub refusing ---------------------------------------------------------------------------

    [Fact]
    public async Task An_answer_that_was_written_and_never_recorded_is_found_and_not_written_twice()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync(mode: "plan");

        // A stranger who copies the marker has written a stranger's comment.
        world.GitHub.Comment(Repo, issue, Passerby, "/approve\n" + IssueComments.AnswerMarker(attemptId, IssueComments.NotApproverKey), PasserbyId);
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await world.Poller().PassAsync(Ct);

        Answers(world).Should().HaveCount(2, "one to the stranger, one for the mode");

        // The process died after writing both and before recording either.
        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.ExecuteUpdateAsync(
                s => s.SetProperty(a => a.CommandCommentId, (long?)null).SetProperty(a => a.CommandAnswers, (string?)null), Ct);
        }

        var writes = world.GitHub.Writes.Count;
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        world.GitHub.Writes.Should().HaveCount(writes, "its own comments were found by their markers");
        Answers(world).Should().HaveCount(2);

        var attempt = await AttemptAsync();
        attempt.CommandAnswers!.Split(',').Should().BeEquivalentTo(IssueComments.NotApproverKey, "mode-plan");
        attempt.CommandCommentId.Should().Be(world.GitHub.Comments.Max(c => c.Id));
        (await CommandAuditAsync()).Should().HaveCount(2, "what was already said is not said to the audit trail again");
    }

    [Fact]
    public async Task An_answer_github_will_not_take_is_written_by_the_next_pass_and_fails_nothing_but_itself()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        var before = (await AttemptAsync()).CommandCommentId;

        world.GitHub.Comment(Repo, issue, Passerby, "/approve", PasserbyId);
        world.GitHub.FailCreates = GitHubOutcome.ServerError;
        await poller.PassAsync(Ct);

        Answers(world).Should().BeEmpty();
        (await AttemptAsync()).Should().Match<CodeFixAttempt>(a => a.State == CodeFixState.PlanReady && a.CommandCommentId == before && a.CommandAnswers == null,
            "the comment that could not be answered was not passed over");
        world.Health.Polls.Should().ContainSingle().Which.Should().Match<GitHubRepositoryPoll>(p =>
            !p.Succeeded && p.Detail.Contains("an answer could not be written on the issue"));

        // The rule the list's tag is kept by: only when everything was done.
        world.GitHub.Lists.Clear();
        world.GitHub.FailCreates = null;
        await poller.PassAsync(Ct);

        world.GitHub.Lists.Should().ContainSingle().Which.ETagSent.Should().BeNull();
        Answers(world).Should().ContainSingle();
        world.Health.Polls.Should().ContainSingle().Which.Succeeded.Should().BeTrue();

        // And the read itself failing: nothing is concluded, and the next pass reads.
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        world.GitHub.FailCommentReads = GitHubOutcome.RateLimited;
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady);
        world.Health.Polls.Should().ContainSingle().Which.Detail.Should().Contain("its comments could not be read for a command");

        world.GitHub.FailCommentReads = null;
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
    }

    // --- taking the issue back -------------------------------------------------------------------

    [Fact]
    public async Task Unassigning_while_the_implementing_job_runs_deletes_it_and_an_approval_after_that_does_nothing()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
        world.Launcher.Phase = CodeFixJobPhase.Running;

        // Taken back while it implements - and somebody approves once more in the same moment.
        world.GitHub.Unassign(Repo, issue);
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Cancelled);
        attempt.PrUrl.Should().BeNull("nothing was pushed, and no pull request appears");
        world.Launcher.Deleted.Should().Equal(CodeFixJobSpec.JobName(attemptId, CodeFixPhase.Implement));

        var item = (await WorkItemsAsync()).Should().ContainSingle().Subject;
        item.State.Should().Be(WorkItemState.Cancelled);
        item.StillAssigned.Should().BeFalse("it ended BY being taken back");

        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains("**Hephaisto has let go of this issue:**") && c.Body.Contains("was stopped"));

        // The result of a Job that was deleted is not collected, whatever its log would say.
        world.Launcher.Log = CodeFixResultParser.Frame(ImplementJson(attemptId, attempt.Branch, $"{CloneUrl}/pull/7"));
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Cancelled);
        world.Launcher.Launched.Should().HaveCount(2);
        world.Launcher.Deleted.Should().ContainSingle();
        Answers(world).Should().BeEmpty("an approval of an issue that is no longer Hephaisto's is not answered either");
        (await CommandAuditAsync()).Should().ContainSingle("the one approval that counted");
    }

    [Fact]
    public async Task An_approval_that_arrives_with_the_unassignment_starts_nothing()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        world.GitHub.Unassign(Repo, issue);
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Cancelled);
        world.Launcher.Launched.Should().ContainSingle("the plan Job, and no implementing one");
        (await CommandAuditAsync()).Should().BeEmpty();
    }

    // --- the pull request, to its end ---------------------------------------------------------------

    [Fact]
    public async Task The_pull_requests_description_is_kept_with_the_attempt_and_shown()
    {
        const string description = "Endpoints.Map needs a null check.\n\nCloses octo/shop#100\n\n| Issue | https://github.com/octo/shop/issues/100 |\n";
        var (world, _, workItemId, attemptId) = await PullRequestOpenAsync(prBody: description);

        var attempt = await AttemptAsync();
        attempt.PrBody.Should().Be(description);
        attempt.PrNumber.Should().Be(7);

        await using var db = pg.CreateContext();
        var queries = new CodeFixQueries(db, world.Switch, world.OptionsMonitor, new OptionsStub<AuthOptions>(new AuthOptions()));

        (await queries.ListAsync(null, 100, Ct)).Single(r => r.Id == attemptId).PrBody.Should().Be(description);
        (await queries.ForWorkItemAsync(workItemId, Ct)).Single().PrBody.Should().Contain("Closes octo/shop#100");
        JsonSerializer.Serialize(CodeFixQueries.View(attempt), new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().Contain("\"prBody\":\"Endpoints.Map");

        // The issue is told where the pull request is, in the comment that is edited.
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains($"**A pull request is open:** {CloneUrl}/pull/7"));
    }

    [Fact]
    public async Task A_runner_that_prints_no_description_opens_a_pull_request_all_the_same()
    {
        await PullRequestOpenAsync(prBody: null);

        (await AttemptAsync()).Should().Match<CodeFixAttempt>(a => a.State == CodeFixState.PrOpened && a.PrBody == null);
    }

    [Fact]
    public async Task An_open_pull_request_is_asked_about_on_every_pass_for_nothing_and_ends_nothing()
    {
        var (world, _, _, _) = await PullRequestOpenAsync();
        var poller = world.Poller();
        world.GitHub.PullReads.Clear();

        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.GitHub.PullReads.Select(r => (r.Number, r.Answered))
            .Should().Equal((7, GitHubOutcome.Ok), (7, GitHubOutcome.NotModified), (7, GitHubOutcome.NotModified));
        world.GitHub.PullReads[0].ETagSent.Should().BeNull();
        world.GitHub.PullReads.Skip(1).Should().OnlyContain(r => r.ETagSent != null, "a review takes days; an unchanged pull request is a 304");

        (await WorkItemsAsync()).Should().ContainSingle().Which.State.Should().Be(WorkItemState.Taken, "an open pull request does not end the work");
        (await AttemptAsync()).State.Should().Be(CodeFixState.PrOpened);
    }

    [Fact]
    public async Task A_merged_pull_request_ends_the_work_item_as_done_also_when_the_issue_closed_in_the_same_pass()
    {
        var (world, issue, _, _) = await PullRequestOpenAsync();
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        // Merged - and, as GitHub does for a description that says Closes, the issue with it.
        world.GitHub.Merge(Repo, 7);
        world.GitHub.Close(Repo, issue);
        await poller.PassAsync(Ct);

        var item = (await WorkItemsAsync()).Should().ContainSingle().Subject;
        item.State.Should().Be(WorkItemState.Done, "a merge is never recorded as 'the issue was closed'");
        item.StateReason.Should().Be(WorkItemReasons.Merged);
        item.ClosedAt.Should().Be(Now);
        item.StillAssigned.Should().BeFalse("the list it was not in said so, in the same pass");

        (await AttemptAsync()).State.Should().Be(CodeFixState.PrOpened, "the attempt ended when the pull request was opened");

        await using (var db = pg.CreateContext())
        {
            var types = await db.AuditEvents.AsNoTracking().Where(e => e.Type.StartsWith("workitem.")).Select(e => e.Type).ToListAsync(Ct);

            types.Should().Contain(GitHubIssuePoller.AuditDone).And.NotContain(GitHubIssuePoller.AuditCancelled);
        }

        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains($"**Done.** The pull request was merged: {CloneUrl}/pull/7"));

        // And it stays done: nothing is taken, planned or written again.
        var writes = world.GitHub.Writes.Count;
        var pulls = world.GitHub.PullReads.Count;
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        (await WorkItemsAsync()).Should().ContainSingle();
        world.Launcher.Launched.Should().HaveCount(2);
        world.GitHub.Writes.Should().HaveCount(writes);
        world.GitHub.PullReads.Should().HaveCount(pulls, "a pull request whose work item ended is not asked about");
    }

    [Fact]
    public async Task A_merge_between_the_two_reads_of_one_pass_is_still_done()
    {
        var (world, issue, _, _) = await PullRequestOpenAsync();
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        // The pass asks about the pull request - open - and then, before it reads the list, the
        // pull request is merged and GitHub closes the issue.
        world.GitHub.OnListIssues = () =>
        {
            world.GitHub.OnListIssues = null;
            world.GitHub.Merge(Repo, 7);
            world.GitHub.Close(Repo, issue);
        };

        await poller.PassAsync(Ct);

        var item = (await WorkItemsAsync()).Should().ContainSingle().Subject;
        item.State.Should().Be(WorkItemState.Done);
        item.StateReason.Should().Be(WorkItemReasons.Merged);
        item.StillAssigned.Should().BeFalse();

        // Read twice in that pass: with the tag, and once more without it before concluding.
        world.GitHub.PullReads.TakeLast(2).Select(r => (r.ETagSent is null, r.Answered))
            .Should().Equal((false, GitHubOutcome.NotModified), (true, GitHubOutcome.Ok));
    }

    [Fact]
    public async Task While_github_does_not_answer_for_the_pull_request_nothing_ends_not_even_when_the_issue_closed()
    {
        var (world, issue, _, _) = await PullRequestOpenAsync();
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.GitHub.FailPulls = GitHubOutcome.ServerError;
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Single().State.Should().Be(WorkItemState.Taken);
        world.Health.Polls.Should().ContainSingle().Which.Should().Match<GitHubRepositoryPoll>(p =>
            !p.Succeeded && p.Detail.Contains("its pull request #7 could not be read"));

        // Merged meanwhile, issue closed with it - and still no answer about the pull request.
        // "Closed" alone would be a cancellation; it is not concluded on a guess.
        world.GitHub.Close(Repo, issue);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Single().State.Should().Be(WorkItemState.Taken, "what closed the issue is not known yet");

        world.GitHub.FailPulls = null;
        world.GitHub.Merge(Repo, 7);
        world.GitHub.PullReads.Clear();
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Single().Should().Match<WorkItem>(w => w.State == WorkItemState.Done && w.StateReason == WorkItemReasons.Merged);
        world.GitHub.PullReads[0].ETagSent.Should().BeNull("the tag of an answer that could not be read again is not kept");
        world.Health.Polls.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task An_issue_that_is_closed_while_its_pull_request_is_open_is_cancelled_and_the_pull_request_stays()
    {
        var (world, issue, _, _) = await PullRequestOpenAsync();

        world.GitHub.Close(Repo, issue);
        await world.Poller().PassAsync(Ct);

        var item = (await WorkItemsAsync()).Single();
        item.State.Should().Be(WorkItemState.Cancelled);
        item.StateReason.Should().Be("the issue was closed");
        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains($"The pull request stays as it is: {CloneUrl}/pull/7"));
    }

    [Fact]
    public async Task A_pull_request_closed_without_merging_cancels_and_the_issue_is_taken_again_only_after_it_was_handed_over_again()
    {
        var (world, issue, _, _) = await PullRequestOpenAsync();
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.GitHub.ClosePull(Repo, 7);
        await poller.PassAsync(Ct);

        var item = (await WorkItemsAsync()).Should().ContainSingle().Subject;
        item.State.Should().Be(WorkItemState.Cancelled);
        item.StateReason.Should().Be(WorkItemReasons.PullRequestClosed);
        item.StillAssigned.Should().BeTrue("nobody took the issue back: it is open and assigned as before");

        WorkItemQueries.View(item).StillAssigned.Should().BeTrue();

        world.GitHub.Comments.Should().ContainSingle(c =>
            c.Body.Contains($"its pull request was closed without merging: {CloneUrl}/pull/7")
            && c.Body.Contains("unassign Hephaisto, wait a minute or two, and assign it again"));

        await using (var db = pg.CreateContext())
        {
            var cancelled = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == GitHubIssuePoller.AuditCancelled, Ct);
            cancelled.Summary.Should().Contain(WorkItemReasons.PullRequestClosed);
        }

        // Still listed at every pass, by this process and by one that knows only the database -
        // and "a listed issue with no taken work item" is not taken.
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        (await WorkItemsAsync()).Should().ContainSingle("the same hand-over is not started over");
        world.Launcher.Launched.Should().HaveCount(2);

        // Unassigned: one list it is not in. Nothing is taken, and the mark is gone.
        world.GitHub.Unassign(Repo, issue);
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Should().ContainSingle().Which.StillAssigned.Should().BeFalse();

        // Assigned again: that is new work, with a plan of its own.
        world.GitHub.Assign(Repo, issue);
        await poller.PassAsync(Ct);

        var items = await WorkItemsAsync();
        items.Should().HaveCount(2);
        items[1].State.Should().Be(WorkItemState.Taken);
        world.Launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Implement, CodeFixPhase.Plan);
    }

    [Fact]
    public async Task A_done_work_item_whose_issue_stayed_open_and_assigned_is_not_taken_again_until_it_is_reopened_or_reassigned()
    {
        // Merged into another branch than the default one: GitHub closes nothing.
        var (world, issue, _, _) = await PullRequestOpenAsync();
        var poller = world.Poller();
        await poller.PassAsync(Ct);

        world.GitHub.Merge(Repo, 7);
        await poller.PassAsync(Ct);

        var done = (await WorkItemsAsync()).Should().ContainSingle().Subject;
        done.State.Should().Be(WorkItemState.Done);
        done.StillAssigned.Should().BeTrue();

        // The hazard: open, assigned, no taken work item - on every pass from now on.
        for (var i = 0; i < 3; i++)
        {
            await poller.PassAsync(Ct);
        }

        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        (await WorkItemsAsync()).Should().ContainSingle("a finished issue that is still assigned is not new work");
        world.Launcher.Launched.Should().HaveCount(2, "and it is not planned again");

        // Closed: a list it is not in. Reopened: handed back - "it is not fixed".
        world.GitHub.Close(Repo, issue);
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Should().ContainSingle().Which.StillAssigned.Should().BeFalse();

        world.GitHub.Reopen(Repo, issue);
        await poller.PassAsync(Ct);

        var items = await WorkItemsAsync();
        items.Should().HaveCount(2);
        items.Select(w => w.State).Should().Equal(WorkItemState.Done, WorkItemState.Taken);
    }

    [Fact]
    public async Task An_issue_that_was_taken_back_is_taken_again_as_soon_as_it_is_assigned_again()
    {
        // The other direction of the same rule: a work item that ended BY being taken back holds
        // nothing, and the next assignment is new work at once.
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        world.GitHub.Unassign(Repo, issue);
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Should().ContainSingle().Which.Should().Match<WorkItem>(w => w.State == WorkItemState.Cancelled && !w.StillAssigned);

        world.GitHub.Assign(Repo, issue);
        await poller.PassAsync(Ct);

        (await WorkItemsAsync()).Select(w => w.State).Should().Equal(WorkItemState.Cancelled, WorkItemState.Taken);
    }

    [Fact]
    public async Task A_pull_request_without_a_reported_number_is_found_by_its_address()
    {
        var (world, _, _, attemptId) = await PlanOnTheIssueAsync();
        var issue = (await WorkItemsAsync()).Single().Number;

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await world.Poller().PassAsync(Ct);
        await world.CollectImplementAsync(attemptId, $"{CloneUrl}/pull/31", prBody: null, prNumber: null);

        (await AttemptAsync()).PrNumber.Should().BeNull();

        world.GitHub.Merge(Repo, 31);
        await world.Poller().PassAsync(Ct);

        world.GitHub.PullReads.Should().Contain(r => r.Number == 31);
        (await WorkItemsAsync()).Single().State.Should().Be(WorkItemState.Done);
    }

    // --- the moment between an approval and its Job ------------------------------------------------

    /// <summary>
    /// Found by the issues suite on 2026-10-08 (G06): approved at 10:59:16.336, "the Implement
    /// phase has no job recorded" at .342, Job started at .343. The watcher's pass had read the
    /// attempt in the moment an approval is durable and its Job is not yet created - and failed
    /// it. The Job ran for two minutes all the same and opened its pull request, for an attempt
    /// that said it had not worked.
    /// </summary>
    [Fact]
    public async Task An_approved_attempt_whose_job_is_still_being_created_is_not_failed_by_the_watcher()
    {
        var (world, _, _, attemptId) = await PlanOnTheIssueAsync();

        // The row as it is between the approval's commit and the Job's creation.
        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.Where(a => a.Id == attemptId).ExecuteUpdateAsync(
                s => s.SetProperty(a => a.State, CodeFixState.Implementing)
                    .SetProperty(a => a.ApprovedBy, "github:maintainer")
                    .SetProperty(a => a.ApprovalSource, ApprovalSource.GitHub)
                    .SetProperty(a => a.DecidedAt, Now),
                Ct);
        }

        await using (var db = pg.CreateContext())
        {
            await world.Coordinator(db).CollectAsync(await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct), Ct);
        }

        var between = await AttemptAsync();
        between.State.Should().Be(CodeFixState.Implementing, "its Job is on its way");
        between.FailureReason.Should().BeNull();

        await using (var db = pg.CreateContext())
        {
            (await db.AuditEvents.CountAsync(e => e.Type == CodeFixCoordinator.AuditFailed, Ct)).Should().Be(0);

            // The control: left like that by a process that died - for longer than a launch takes.
            await db.CodeFixAttempts.Where(a => a.Id == attemptId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DecidedAt, Now - CodeFixCoordinator.LaunchGrace - TimeSpan.FromSeconds(1)), Ct);

            await world.Coordinator(db).CollectAsync(await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct), Ct);
        }

        var left = await AttemptAsync();
        left.State.Should().Be(CodeFixState.Failed);
        left.FailureReason.Should().Be("the Implement phase has no job recorded");
    }
}
