using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Components;
using Hephaisto.Agent.Options;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// What an attempt's own page reads (v0.14.0, #248): one attempt by its id with what it is for,
/// and who decided it through what - for a plan answered in the console, and for one answered
/// on the issue. Text somebody typed is kept as it was typed; showing it as text is the page's.
/// </summary>
public sealed partial class WorkItemStageTests
{
    [Fact]
    public async Task A_plan_answered_in_the_console_says_through_what_whether_it_was_approved_or_denied()
    {
        var (world, _, workItemId, attemptId) = await PlanOnTheIssueAsync();

        await using (var db = pg.CreateContext())
        {
            // What Components/Pages/CodeFixDetail.razor calls for a work item's attempt: the
            // work item's door, the console's source.
            var denied = await world.Coordinator(db).DecideForWorkItemAsync(
                workItemId, attemptId, approve: false, "flo", ApprovalSource.Ui, authenticated: false, "<b>wrong</b> layer, see #7", Ct);

            denied.Outcome.Should().Be(CodeFixDecisionOutcome.Done);
        }

        await using (var db = pg.CreateContext())
        {
            var queries = new CodeFixQueries(db, world.Switch, world.OptionsMonitor, new OptionsStub<AuthOptions>(new AuthOptions()));
            var detail = (await queries.AttemptAsync(attemptId, Ct))!;

            detail.Attempt.State.Should().Be(CodeFixState.Denied);
            detail.Attempt.ApprovedBy.Should().Be("flo");
            detail.Attempt.DecidedThrough.Should().Be(ApprovalSource.Ui);
            detail.Attempt.FailureReason.Should().Be("<b>wrong</b> layer, see #7", "kept as it was typed; the page shows it as text");

            CodeFixHistory.Of(detail.Attempt, detail.WorkItem).Select(e => e.Step)
                .Should().Equal("created", "planning", "plan ready", "denied");
            CodeFixHistory.Of(detail.Attempt, detail.WorkItem)[^1].Detail.Should().Be("by flo, through the console");
        }
    }

    [Fact]
    public async Task A_plan_answered_on_the_issue_says_so_on_its_page()
    {
        var (world, _, _, attemptId) = await PullRequestOpenAsync(pr: 14, prBody: "Closes octo/shop#1\n\n<script>x</script>");

        await using var db = pg.CreateContext();
        var queries = new CodeFixQueries(db, world.Switch, world.OptionsMonitor, new OptionsStub<AuthOptions>(new AuthOptions()));
        var detail = (await queries.AttemptAsync(attemptId, Ct))!;

        detail.Attempt.ApprovedBy.Should().Be("github:maintainer");
        detail.Attempt.DecidedThrough.Should().Be(ApprovalSource.GitHub);
        detail.Attempt.PrBody.Should().Contain("<script>x</script>", "kept as the runner sent it; the page shows it as text");

        var history = CodeFixHistory.Of(detail.Attempt, detail.WorkItem);
        history.Select(e => e.Step).Should().Equal("created", "planning", "plan ready", "approved", "implementing", "pr opened");
        history[3].Detail.Should().Be("by github:maintainer, through a comment on the issue");
    }
}
