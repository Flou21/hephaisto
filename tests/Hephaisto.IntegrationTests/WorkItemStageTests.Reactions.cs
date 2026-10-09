using Microsoft.EntityFrameworkCore;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// A plan answered by a reaction on its comment (#298), with the doubles and the real Postgres
/// of the tests beside it. Four claims: an approver's 🚀 or 👎 on the plan comment decides
/// exactly once, through the door a command knocks on; nobody else's reaction and no other
/// reaction changes anything; a reaction is read once and is bound to the one plan it is on;
/// and Hephaisto sets the two to click itself, which are never an answer.
/// </summary>
public sealed partial class WorkItemStageTests
{
    private const string Rocket = IssueCommands.ApproveReaction;
    private const string ThumbsDown = IssueCommands.RejectReaction;

    private async Task<long> PlanCommentAsync() => (await AttemptAsync()).PlanCommentId!.Value;

    // --- the two to click -------------------------------------------------------------------------

    [Fact]
    public async Task The_plan_comment_gets_the_two_reactions_to_click_once_and_they_answer_nothing()
    {
        var (world, _, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();
        var plan = await PlanCommentAsync();

        // By the pass that wrote the plan: below it when it is first read, not a poll later.
        world.GitHub.Reactions.Select(r => (r.CommentId, r.Content, r.Author)).Should().BeEquivalentTo(
            [(plan, Rocket, Bot), (plan, ThumbsDown, Bot)],
            "both are on the plan comment, as Hephaisto's own, so that GitHub shows them below it");

        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        world.GitHub.ReactionWrites.Should().HaveCount(2, "what is there is not set again");
        world.GitHub.ReactionReads[^1].Answered.Should().Be(GitHubOutcome.NotModified, "an unchanged plan costs nothing");

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady, "its own reactions are never an answer");
        attempt.CommandReactionId.Should().BeNull();
        world.Launcher.Launched.Should().ContainSingle();
        Answers(world).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reaction_somebody_took_off_is_set_again_also_by_a_process_that_knows_nothing()
    {
        var (world, _, _, _) = await PlanOnTheIssueAsync();
        await world.Poller().PassAsync(Ct);

        world.GitHub.Reactions.RemoveAll(r => r.Content == Rocket);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        world.GitHub.Reactions.Select(r => r.Content).Should().BeEquivalentTo(Rocket, ThumbsDown);
        world.GitHub.Reactions.Should().OnlyContain(r => r.Author == Bot);
    }

    [Fact]
    public async Task A_plan_that_cannot_be_approved_here_is_given_the_thumbs_down_only()
    {
        var (world, _, _, _) = await PlanOnTheIssueAsync(configure: w => w.GitHub.FailReactionWrites = GitHubOutcome.Unauthorized);

        await using (var db = pg.CreateContext())
        {
            await db.CodeFixAttempts.ExecuteUpdateAsync(s => s.SetProperty(a => a.NeedsCait, true), Ct);
        }

        world.GitHub.FailReactionWrites = null;
        world.GitHub.ReactionWrites.Clear();
        await world.Poller().PassAsync(Ct);

        world.GitHub.Reactions.Select(r => r.Content).Should().Equal(ThumbsDown);
    }

    [Fact]
    public async Task With_nobody_listed_no_reaction_is_set_and_none_is_read()
    {
        var (world, _, _, _) = await PlanOnTheIssueAsync(configure: w => w.Approvers = []);
        var plan = await PlanCommentAsync();

        world.GitHub.React(plan, Maintainer, MaintainerId, Rocket);
        await world.Poller().PassAsync(Ct);

        world.GitHub.ReactionReads.Should().BeEmpty();
        world.GitHub.ReactionWrites.Should().BeEmpty("a reaction to click would promise an answer nobody reads");
        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady);
    }

    // --- an approver's reaction -------------------------------------------------------------------

    [Fact]
    public async Task An_approvers_rocket_on_the_plan_starts_exactly_one_implement_job_and_twice_is_once()
    {
        var (world, issue, workItemId, attemptId) = await PlanOnTheIssueAsync();
        var poller = world.Poller();
        var plan = await PlanCommentAsync();

        var reaction = world.GitHub.React(plan, Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Implementing);
        attempt.ApprovedBy.Should().Be("github:maintainer");
        attempt.ApprovalSource.Should().Be(ApprovalSource.GitHub);
        attempt.CommandReactionId.Should().Be(reaction);
        attempt.CommandAnswers.Should().BeNull();

        world.Launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Implement);

        await using (var db = pg.CreateContext())
        {
            // The door's own row, with who and how ...
            (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Type == CodeFixCoordinator.AuditApproved, Ct)).Actor.Should().Be("github:maintainer");
        }

        // ... and the poller's, with what the door does not know: the account's number and the reaction.
        var row = (await CommandAuditAsync()).Should().ContainSingle().Subject;
        row.GetProperty("outcome").GetString().Should().Be("approved");
        row.GetProperty("command").GetString().Should().Be("/approve");
        row.GetProperty("reaction").GetString().Should().Be(Rocket);
        row.GetProperty("reaction_id").GetInt64().Should().Be(reaction);
        row.GetProperty("comment_id").GetInt64().Should().Be(plan);
        row.GetProperty("account_id").GetInt64().Should().Be(MaintainerId);
        row.GetProperty("work_item_id").GetGuid().Should().Be(workItemId);
        row.GetProperty("attempt_id").GetGuid().Should().Be(attemptId);

        // The reaction stays on the comment. The same process, and one that remembers nothing.
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        world.Launcher.Launched.Should().HaveCount(2, "one plan Job and one implement Job, ever");
        (await CommandAuditAsync()).Should().ContainSingle();
        world.GitHub.Comments.Should().ContainSingle(c => c.Number == issue && c.Body.Contains("**Implementing.** github:maintainer approved the plan."));
    }

    [Fact]
    public async Task An_approvers_thumbs_down_denies_the_attempt_and_says_how_it_was_given()
    {
        var (world, _, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        world.GitHub.React(await PlanCommentAsync(), Maintainer, MaintainerId, ThumbsDown);
        await poller.PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Denied);
        attempt.FailureReason.Should().Be(IssueCommands.NoReasonByReaction, "a reaction carries no reason");
        attempt.ApprovedBy.Should().Be("github:maintainer");
        attempt.ImplementJobName.Should().BeNull();

        world.GitHub.Comments.Should().ContainSingle(c => c.Body.Contains($"**The plan was rejected** by github:maintainer: {IssueCommands.NoReasonByReaction}."));
        (await CommandAuditAsync()).Should().ContainSingle().Which.GetProperty("outcome").GetString().Should().Be("rejected");

        // A rocket afterwards does not bring it back: nothing is waiting, and nothing is read.
        world.GitHub.React(await PlanCommentAsync(), Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Denied);
        world.Launcher.Launched.Should().ContainSingle("only the plan Job ever ran");
    }

    [Fact]
    public async Task A_command_and_a_reaction_in_one_pass_are_one_answer_and_the_command_is_it()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();

        world.GitHub.React(await PlanCommentAsync(), Maintainer, MaintainerId, Rocket);
        world.GitHub.Comment(Repo, issue, Maintainer, "/reject not yet", MaintainerId);
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.Denied, "the comments of a pass are read first, and a decided plan is asked nothing more");
        attempt.FailureReason.Should().Be("not yet");
        attempt.CommandReactionId.Should().BeNull();
        world.Launcher.Launched.Should().ContainSingle();
        (await CommandAuditAsync()).Should().ContainSingle();
    }

    // --- everybody else, and every other reaction -------------------------------------------------

    [Fact]
    public async Task Somebody_who_is_not_an_approver_changes_nothing_by_reacting_and_is_answered_once_whether_they_react_or_write()
    {
        var (world, issue, _, attemptId) = await PlanOnTheIssueAsync();
        var poller = world.Poller();
        var plan = await PlanCommentAsync();

        var first = world.GitHub.React(plan, Passerby, PasserbyId, Rocket);
        await poller.PassAsync(Ct);

        var answer = Answers(world).Should().ContainSingle().Subject;
        answer.Body.Should().Be(IssueComments.NotApprover(attemptId, Passerby));

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady);
        attempt.CommandReactionId.Should().Be(first);
        attempt.CommandAnswers.Should().Be(IssueComments.NotApproverKey);

        // More of them, another stranger, and the same thing as a comment: said once per plan.
        world.GitHub.React(plan, Passerby, PasserbyId, ThumbsDown);
        world.GitHub.React(plan, Reporter, ReporterId, Rocket);
        world.GitHub.Comment(Repo, issue, Passerby, "/approve", PasserbyId);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        Answers(world).Should().ContainSingle();
        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady);
        world.Launcher.Launched.Should().ContainSingle();

        // And an approver is still heard.
        world.GitHub.React(plan, Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
    }

    [Theory]
    [InlineData("+1")]
    [InlineData("heart")]
    [InlineData("hooray")]
    [InlineData("eyes")]
    [InlineData("laugh")]
    [InlineData("confused")]
    public async Task Every_other_reaction_is_not_an_answer_whoever_sets_it(string content)
    {
        // The thumbs-up above all: it is what people set on a comment they have read.
        var (world, _, _, _) = await PlanOnTheIssueAsync();
        var plan = await PlanCommentAsync();

        var approvers = world.GitHub.React(plan, Maintainer, MaintainerId, content);
        world.GitHub.React(plan, Passerby, PasserbyId, content);
        await world.Poller().PassAsync(Ct);

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady);
        attempt.CommandReactionId.Should().BeGreaterThanOrEqualTo(approvers, "looked at, and behind the cursor");
        Answers(world).Should().BeEmpty("nobody is told that a reaction was not a command");
        (await CommandAuditAsync()).Should().BeEmpty();
        world.Launcher.Launched.Should().ContainSingle();
    }

    // --- once, and for one plan --------------------------------------------------------------------

    [Fact]
    public async Task Below_Pr_a_rocket_is_refused_once_and_does_not_approve_by_itself_when_the_mode_is_Pr_but_one_set_again_does()
    {
        var (world, _, _, attemptId) = await PlanOnTheIssueAsync(mode: "plan");
        var poller = world.Poller();
        var plan = await PlanCommentAsync();

        var first = world.GitHub.React(plan, Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        var answer = Answers(world).Should().ContainSingle().Subject;
        answer.Body.Should().Be(IssueComments.Refused(attemptId, Maintainer, IssueCommandKind.Approve, CodeFixRefusal.ModeBelowPr, CodeFixMode.Plan, byReaction: true));
        answer.Body.Should().Contain("`maintainer`'s 🚀 was read and refused")
            .And.Contain("take the 🚀 off and set it again, or reply `/approve`");

        var attempt = await AttemptAsync();
        attempt.State.Should().Be(CodeFixState.PlanReady, "a refusal does not use the plan up");
        attempt.CommandAnswers.Should().Be("mode-plan");
        attempt.CommandReactionId.Should().Be(first);
        (await CommandAuditAsync()).Should().ContainSingle().Which.GetProperty("answer").GetString().Should().Be("mode-plan");

        // The operator raises the mode. The rocket is still on the comment - and was read.
        world.Mode = "pr";
        await poller.PassAsync(Ct);
        await new World(pg, world.GitHub, world.Launcher) { Mode = "pr" }.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady, "a reaction is a click, not a standing order");
        world.Launcher.Launched.Should().ContainSingle();

        // Off and on again is a new reaction.
        world.GitHub.Unreact(first);
        world.GitHub.React(plan, Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
        Answers(world).Should().ContainSingle();
    }

    [Fact]
    public async Task A_rocket_on_an_earlier_plans_comment_approves_nothing_once_there_is_a_new_plan()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();
        var earlier = await PlanCommentAsync();

        world.GitHub.Comment(Repo, issue, Maintainer, "/replan", MaintainerId);
        await poller.PassAsync(Ct);
        await CollectNewestPlanAsync(world, "Endpoints.Map needs a null check, and nothing else.");
        await poller.PassAsync(Ct);

        var attempts = await AttemptsAsync();
        attempts.Should().HaveCount(2);
        var newer = attempts[1].PlanCommentId!.Value;
        newer.Should().NotBe(earlier);

        // The plan somebody read an hour ago is not the plan that would be implemented.
        var readsOfTheEarlier = world.GitHub.ReactionReads.Count(r => r.CommentId == earlier);
        world.GitHub.React(earlier, Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);
        await poller.PassAsync(Ct);

        (await AttemptsAsync())[1].State.Should().Be(CodeFixState.PlanReady);
        world.Launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Plan);
        world.GitHub.ReactionReads.Count(r => r.CommentId == earlier).Should().Be(readsOfTheEarlier, "only the newest plan's comment is asked about");
        (await CommandAuditAsync()).Should().ContainSingle("the /replan, and nothing since");

        // On the new plan's comment it is an answer.
        world.GitHub.React(newer, Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        (await AttemptsAsync())[1].State.Should().Be(CodeFixState.Implementing);
    }

    // --- GitHub in the way -------------------------------------------------------------------------

    [Fact]
    public async Task A_token_that_may_not_set_a_reaction_still_reads_an_approvers_and_says_what_it_could_not_do()
    {
        var (world, _, _, _) = await PlanOnTheIssueAsync(configure: w => w.GitHub.FailReactionWrites = GitHubOutcome.Unauthorized);
        var poller = world.Poller();

        // The pass that wrote the plan let it pass; the one that reads the reactions says it.
        world.Health.Polls.Should().ContainSingle().Which.Succeeded.Should().BeTrue("a plan without something to click is still a plan on the issue");
        await poller.PassAsync(Ct);

        world.GitHub.Reactions.Should().BeEmpty();
        world.Health.Polls.Should().ContainSingle().Which.Detail.Should().Contain("a reaction to click could not be set on its plan");

        world.GitHub.React(await PlanCommentAsync(), Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing, "the answers are read before anything is set");
    }

    [Fact]
    public async Task While_the_reactions_cannot_be_read_a_command_still_answers_the_plan()
    {
        var (world, issue, _, _) = await PlanOnTheIssueAsync();
        var poller = world.Poller();

        world.GitHub.FailReactionReads = GitHubOutcome.ServerError;
        world.GitHub.React(await PlanCommentAsync(), Maintainer, MaintainerId, Rocket);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.PlanReady);
        world.Health.Polls.Should().ContainSingle().Which.Detail.Should().Contain("the reactions on its plan could not be read");

        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await poller.PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Implementing);
    }
}
