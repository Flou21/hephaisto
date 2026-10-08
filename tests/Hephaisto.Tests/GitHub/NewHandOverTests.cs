using Hephaisto.Agent.GitHub;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// When an assignment of an issue to Hephaisto is a NEW hand-over of a work item whose attempt
/// has ended - the rule by which off-and-on-again within one poll interval is noticed (#285).
/// Two clocks are compared: the attempt's end is this process's, the assignment's is GitHub's,
/// in whole seconds.
/// </summary>
public sealed class NewHandOverTests
{
    private static readonly DateTimeOffset Ended = new(2026, 10, 8, 9, 28, 30, 400, TimeSpan.Zero);

    private static DateTimeOffset At(int minute, int second) => new(2026, 10, 8, 9, minute, second, TimeSpan.Zero);

    [Fact]
    public void An_assignment_after_the_attempt_ended_is_a_new_hand_over()
    {
        // The first real issue: its attempt had failed, unassigned at 09:28:53, assigned at 09:29:00.
        GitHubIssuePoller.IsNewHandOver(At(29, 0), Ended, lastActedOn: null).Should().BeTrue();
    }

    [Fact]
    public void The_assignment_the_work_item_began_with_is_not()
    {
        GitHubIssuePoller.IsNewHandOver(At(20, 0), Ended, lastActedOn: null).Should().BeFalse();
        GitHubIssuePoller.IsNewHandOver(At(28, 29), Ended, lastActedOn: null).Should().BeFalse("one second before the attempt ended is before it ended");
    }

    [Fact]
    public void GitHubs_seconds_are_whole_so_the_second_the_attempt_ended_in_counts()
    {
        // The attempt ended at :30.4; an assignment at :30.9 is stamped :30 by GitHub.
        GitHubIssuePoller.IsNewHandOver(At(28, 30), Ended, lastActedOn: null).Should().BeTrue();
    }

    [Fact]
    public void One_assignment_is_one_hand_over_whatever_the_clocks_say()
    {
        // It was acted on: a second attempt started, and ended - by this process's clock, which
        // runs behind GitHub's here - "before" the assignment that started it.
        var actedOn = At(29, 0);
        var secondAttemptEnded = new DateTimeOffset(2026, 10, 8, 9, 28, 58, TimeSpan.Zero);

        GitHubIssuePoller.IsNewHandOver(actedOn, secondAttemptEnded, lastActedOn: actedOn).Should().BeFalse();

        // The next assignment is a new one again.
        GitHubIssuePoller.IsNewHandOver(At(31, 0), secondAttemptEnded, lastActedOn: actedOn).Should().BeTrue();
    }
}
