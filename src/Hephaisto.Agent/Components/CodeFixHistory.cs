using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Components;

/// <summary>One line of an attempt's history: when, which step, and what there is to say about it.</summary>
/// <param name="Step">A fixed word - <c>created</c>, <c>plan ready</c>, <c>approved</c> - never anybody's text.</param>
/// <param name="Detail">
/// Hephaisto's words around names it recorded: who asked, who decided and through what, a Job's
/// name, a pull request's number. A reason somebody typed is not here; the page shows that once,
/// as text, where the attempt's end is explained.
/// </param>
public sealed record CodeFixHistoryEntry(DateTimeOffset At, string Step, string Detail);

/// <summary>
/// What happened to one code-fix attempt, in order, read off the row itself.
/// </summary>
/// <remarks>
/// The attempt's own timestamps and nothing else - no audit query. They are what the state
/// machine wrote at each edge, so the list cannot disagree with the state beside it, and an
/// attempt for a GitHub issue (whose audit rows carry no incident id to find them by) has the
/// same history as an incident's.
/// </remarks>
public static class CodeFixHistory
{
    public static IReadOnlyList<CodeFixHistoryEntry> Of(CodeFixAttemptView a, WorkItemView? item = null)
    {
        ArgumentNullException.ThrowIfNull(a);

        var entries = new List<CodeFixHistoryEntry>
        {
            new(a.CreatedAt, "created", $"requested by {a.RequestedBy}"),
        };

        if (a.PlanStartedAt is { } planning)
        {
            entries.Add(new(planning, "planning", a.PlanJobName is { Length: > 0 } job ? $"job {job}" : "a read-only coder writes a plan"));
        }

        if (a.PlanReadyAt is { } ready)
        {
            entries.Add(new(ready, "plan ready", a.PlanCommentId is null
                ? "waiting for a person's answer"
                : "posted on the issue; waiting for an approver's answer"));
        }

        var denied = a.State == CodeFixState.Denied;

        if (a.DecidedAt is { } decided)
        {
            var by = a.ApprovedBy is { Length: > 0 } who ? $"by {who}" : "by somebody the row does not name";
            var through = Display.DecidedThrough(a.DecidedThrough) is { } how ? $", through {how}" : string.Empty;

            entries.Add(new(decided, denied ? "denied" : "approved", by + through));
        }

        if (a.ImplementStartedAt is { } implementing)
        {
            entries.Add(new(implementing, "implementing", a.ImplementJobName is { Length: > 0 } job ? $"job {job}" : "a coder writes to one branch"));
        }

        // A denial is its own end: the decision above already is the last thing that happened.
        if (a.FinishedAt is { } finished && !(denied && a.DecidedAt is not null))
        {
            entries.Add(new(finished, Display.CodeFixWord(a.State), a.State switch
            {
                CodeFixState.PrOpened => a.PrNumber is { } n ? $"draft pull request #{n}" : "a draft pull request",
                CodeFixState.Expired => "nobody answered the plan in time",
                CodeFixState.Cancelled => "stopped before it finished",
                CodeFixState.Denied => "a person said no",
                _ => "ended without a pull request",
            }));
        }

        // What became of the issue afterwards: its pull request merged or closed, the issue
        // closed, Hephaisto unassigned. The work item's own last word, in Hephaisto's vocabulary.
        if (item is { ClosedAt: { } closed, State: not WorkItemState.Taken })
        {
            entries.Add(new(closed, $"work item {Display.WorkItemWord(item.State)}", item.StateReason is { Length: > 0 } why
                ? why
                : item.State == WorkItemState.Done ? "its pull request was merged" : "the issue was taken back"));
        }

        return [.. entries.OrderBy(e => e.At)];
    }
}

/// <summary>The courtesy beside the approve button, shared by every place a plan is answered.</summary>
public static class CodeFixDoor
{
    /// <summary>
    /// Why approve is unavailable, in the words the reader needs, or null when it is available.
    /// </summary>
    /// <remarks>
    /// A courtesy, never the guard: <see cref="CodeFixCoordinator"/> re-checks all of this under a
    /// row lock at the moment of approving, and its refusal is shown verbatim.
    /// </remarks>
    public static string? BlockedBecause(CodeFixModeView mode, CodeFixAttemptView attempt, ConsoleViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(viewer);

        return !mode.CanApprove ? mode.ApprovalBlockedBecause ?? "the code-fix mode does not allow it"
            : attempt.NeedsCait ? "this fix needs a Cait change first; that is staged delivery by a human, Cait first"
            : !viewer.MayDecide ? "your account does not hold the approver role"
            : null;
    }
}
