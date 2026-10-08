using Microsoft.EntityFrameworkCore;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Notifications;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// A work item's code fix in the outbox (v0.14.0, #248): the three moments an incident's attempt
/// announces - a plan waits, a pull request is open, it ended without one - with the issue where
/// the incident was, and nothing of an incident made up. The first of the three is held where the
/// plan result is collected (<c>A_plan_result_makes_it_PlanReady_...</c>).
/// </summary>
public sealed partial class WorkItemStageTests
{
    [Fact]
    public async Task A_pull_request_for_an_issue_is_announced_once_with_its_address()
    {
        var (_, issue, workItemId, attemptId) = await PullRequestOpenAsync(pr: 14);

        await using var db = pg.CreateContext();
        var rows = await db.NotificationDeliveries.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(Ct);

        rows.Select(d => d.Event).Should().Equal(NotificationEvent.CodeFixPlanReady, NotificationEvent.CodeFixPrOpened);
        rows.Should().OnlyContain(d => d.IncidentId == null && d.CorrelationKey == CodeFixNotifier.WorkItemKey(workItemId));

        var opened = rows[1].Snapshot;
        opened.WorkItemId.Should().Be(workItemId);
        opened.CodeFixAttemptId.Should().Be(attemptId);
        opened.ExternalUrl.Should().Be($"{CloneUrl}/pull/14");
        opened.Reason.Should().Contain($"{Repo}#{issue}").And.Contain($"{CloneUrl}/pull/14");
    }

    [Fact]
    public async Task An_issue_taken_back_while_its_plan_waits_is_announced_as_ended_without_a_pull_request()
    {
        var (world, issue, workItemId, _) = await PlanOnTheIssueAsync();

        world.GitHub.Unassign(Repo, issue);
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Cancelled);

        await using var db = pg.CreateContext();
        var rows = await db.NotificationDeliveries.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(Ct);

        rows.Select(d => d.Event).Should().Equal(NotificationEvent.CodeFixPlanReady, NotificationEvent.CodeFixFailed);

        var ended = rows[1].Snapshot;
        ended.WorkItemId.Should().Be(workItemId);
        ended.IncidentId.Should().BeNull();
        ended.Reason.Should().Contain($"{Repo}#{issue}").And.Contain("ended without a pull request").And.Contain("cancelled");
    }

    [Fact]
    public async Task With_no_approvers_listed_the_notification_sends_nobody_to_the_issue()
    {
        await PlanOnTheIssueAsync(configure: w => w.Approvers = []);

        await using var db = pg.CreateContext();
        var told = (await db.NotificationDeliveries.AsNoTracking().SingleAsync(Ct)).Snapshot;

        told.Reason.Should().Contain("in the console").And.NotContain("/approve");
    }
}
