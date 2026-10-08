using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Components;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// The history on an attempt's own page (v0.14.0, #248) is read off the attempt's row: in order,
/// in fixed words, and saying who decided and through what.
/// </summary>
public sealed class CodeFixHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CodeFixAttemptView Attempt(CodeFixState state) => new()
    {
        Id = Guid.CreateVersion7(),
        IncidentId = null,
        IncidentTitle = string.Empty,
        Workload = string.Empty,
        Repository = "https://github.com/octo/shop",
        DefaultBranch = "main",
        Branch = "hephaisto/codefix-0123456789ab",
        State = state,
        RequestedBy = "hephaisto/system",
        CreatedAt = T0,
    };

    [Fact]
    public void A_pull_request_approved_on_the_issue_reads_from_created_to_merged()
    {
        var attempt = Attempt(CodeFixState.PrOpened) with
        {
            PlanStartedAt = T0.AddSeconds(1),
            PlanJobName = "codefix-0123-plan",
            PlanReadyAt = T0.AddMinutes(2),
            PlanCommentId = 42,
            ApprovedBy = "github:maintainer",
            DecidedThrough = ApprovalSource.GitHub,
            DecidedAt = T0.AddMinutes(5),
            ImplementStartedAt = T0.AddMinutes(5),
            ImplementJobName = "codefix-0123-impl",
            FinishedAt = T0.AddMinutes(9),
            PrNumber = 14,
        };
        var item = new WorkItemView(Guid.CreateVersion7(), "github", "octo/shop", 12, "https://github.com/octo/shop/issues/12", "t", null, "reporter",
            WorkItemState.Done, WorkItemReasons.Merged, T0, T0.AddHours(1), "body");

        var history = CodeFixHistory.Of(attempt, item);

        history.Select(e => e.Step).Should().Equal("created", "planning", "plan ready", "approved", "implementing", "pr opened", "work item done");
        history.Select(e => e.At).Should().BeInAscendingOrder();

        history[0].Detail.Should().Be("requested by hephaisto/system");
        history[2].Detail.Should().Contain("posted on the issue");
        history[3].Detail.Should().Be("by github:maintainer, through a comment on the issue");
        history[5].Detail.Should().Be("draft pull request #14");
        history[6].Detail.Should().Be("merged");
    }

    [Theory]
    [InlineData(ApprovalSource.Ui, "the console")]
    [InlineData(ApprovalSource.Api, "the API")]
    [InlineData(ApprovalSource.Oidc, "the API, signed in")]
    [InlineData(ApprovalSource.Teams, "a Teams card")]
    [InlineData(ApprovalSource.GitHub, "a comment on the issue")]
    public void A_denial_is_one_line_with_who_and_through_what_and_not_a_second_ending(ApprovalSource source, string through)
    {
        var attempt = Attempt(CodeFixState.Denied) with
        {
            PlanStartedAt = T0.AddSeconds(1),
            PlanReadyAt = T0.AddMinutes(2),
            ApprovedBy = "flo",
            DecidedThrough = source,
            DecidedAt = T0.AddMinutes(3),
            FinishedAt = T0.AddMinutes(3),
            FailureReason = "<script>alert(1)</script> wrong layer",
        };

        var history = CodeFixHistory.Of(attempt);

        history.Select(e => e.Step).Should().Equal("created", "planning", "plan ready", "denied");
        history[^1].Detail.Should().Be($"by flo, through {through}");

        // The reason somebody typed is shown once, as text, where the page explains the end -
        // never repeated into a line of the history.
        history.Should().NotContain(e => e.Detail.Contains("wrong layer", StringComparison.Ordinal));
    }

    [Fact]
    public void A_denial_from_before_the_source_was_kept_names_who_and_nothing_it_does_not_know()
    {
        var attempt = Attempt(CodeFixState.Denied) with { PlanReadyAt = T0.AddMinutes(1), ApprovedBy = "flo", DecidedAt = T0.AddMinutes(2), FinishedAt = T0.AddMinutes(2) };

        CodeFixHistory.Of(attempt)[^1].Detail.Should().Be("by flo");
    }

    [Theory]
    [InlineData(CodeFixState.Failed, "failed", "ended without a pull request")]
    [InlineData(CodeFixState.Expired, "expired", "nobody answered the plan in time")]
    [InlineData(CodeFixState.Cancelled, "cancelled", "stopped before it finished")]
    public void An_end_without_a_pull_request_is_the_last_line_in_fixed_words(CodeFixState state, string step, string detail)
    {
        var attempt = Attempt(state) with { PlanStartedAt = T0.AddSeconds(1), FinishedAt = T0.AddMinutes(4), FailureReason = "a model's sentence" };

        var last = CodeFixHistory.Of(attempt)[^1];

        last.Step.Should().Be(step);
        last.Detail.Should().Be(detail);
    }

    [Fact]
    public void A_work_item_that_is_still_taken_adds_no_line()
    {
        var item = new WorkItemView(Guid.CreateVersion7(), "github", "octo/shop", 12, "u", "t", null, "reporter", WorkItemState.Taken, null, T0, null, "body");

        CodeFixHistory.Of(Attempt(CodeFixState.Planning) with { PlanStartedAt = T0 }, item)
            .Select(e => e.Step).Should().Equal("created", "planning");
    }

    [Fact]
    public void Approve_is_blocked_for_the_same_three_reasons_wherever_a_plan_is_answered()
    {
        var open = new CodeFixModeView(CodeFixMode.Pr, "pr", true, null);
        var shut = new CodeFixModeView(CodeFixMode.Plan, "plan", false, "approval needs code-fix mode Pr; it is Plan");
        var approver = ConsoleViewer.Anonymous;
        var reader = ConsoleViewer.Anonymous with { MayDecide = false };
        var ready = Attempt(CodeFixState.PlanReady);

        CodeFixDoor.BlockedBecause(open, ready, approver).Should().BeNull();
        CodeFixDoor.BlockedBecause(shut, ready, approver).Should().Contain("code-fix mode Pr");
        CodeFixDoor.BlockedBecause(open, ready with { NeedsCait = true }, approver).Should().Contain("Cait");
        CodeFixDoor.BlockedBecause(open, ready, reader).Should().Contain("approver role");
    }
}
