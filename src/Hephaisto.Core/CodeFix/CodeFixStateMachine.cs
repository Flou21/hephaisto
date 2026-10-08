using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Core.CodeFix;

/// <summary>
/// The only code allowed to change <see cref="CodeFixAttempt.State"/>. One method per legal edge;
/// anything else throws.
/// </summary>
/// <remarks>
/// <para>
/// The edge that matters is <see cref="Approve"/>: it is the one door from a read-only analysis to a
/// Job that writes to a repository, and it refuses every actor the incident state machine refuses to
/// let grant anything, and every other <c>hephaisto/*</c> identity as well. A model, the verifier or the system approving its own code fix would be the
/// agent opening its own door.
/// </para>
/// <para>
/// Throwing on an illegal edge rather than ignoring it is the same choice
/// <see cref="IncidentStateMachine"/> makes: a caller racing a watcher (approve arriving as the plan
/// Job is being collected, say) gets a loud conflict instead of a silently skipped transition.
/// </para>
/// </remarks>
public sealed class CodeFixStateMachine(IClock clock)
{
    public void BeginPlanning(CodeFixAttempt attempt, string jobName)
    {
        Move(attempt, [CodeFixState.Eligible], CodeFixState.Planning);
        attempt.PlanJobName = jobName;
        attempt.PlanStartedAt = clock.UtcNow;
    }

    public void PlanReady(CodeFixAttempt attempt)
    {
        Move(attempt, [CodeFixState.Planning], CodeFixState.PlanReady);
        attempt.PlanReadyAt = clock.UtcNow;
    }

    /// <summary>PlanReady -&gt; Implementing. The one edge towards a repository write.</summary>
    public void Approve(CodeFixAttempt attempt, string actor, ApprovalSource source)
    {
        RefuseMachine(actor, "approve");

        if (attempt.NeedsCait)
        {
            throw new InvalidOperationException(
                $"code fix {attempt.Id} needs a Cait change first; that is staged delivery by a human, "
                + "Cait first, and the coder does not implement it.");
        }

        Move(attempt, [CodeFixState.PlanReady], CodeFixState.Implementing);
        attempt.ApprovedBy = actor.Trim();
        attempt.ApprovalSource = source;
        attempt.DecidedAt = clock.UtcNow;
    }

    public void BeginImplementing(CodeFixAttempt attempt, string jobName)
    {
        if (attempt.State != CodeFixState.Implementing)
            throw Illegal(attempt, CodeFixState.Implementing);

        attempt.ImplementJobName = jobName;
        attempt.ImplementStartedAt = clock.UtcNow;
    }

    /// <summary>
    /// PlanReady -&gt; Denied. <paramref name="source"/> is recorded as it is for an approval: a
    /// page that says who decided also says through what - the console, the API, a comment on the
    /// issue - and before v0.14.0 a denial left that to its audit row.
    /// </summary>
    public void Deny(CodeFixAttempt attempt, string actor, string? reason, ApprovalSource source)
    {
        RefuseMachine(actor, "deny");
        Move(attempt, [CodeFixState.PlanReady], CodeFixState.Denied);
        attempt.ApprovedBy = actor.Trim();
        attempt.ApprovalSource = source;
        attempt.DecidedAt = clock.UtcNow;
        attempt.FailureReason = string.IsNullOrWhiteSpace(reason) ? $"denied by {actor.Trim()}" : reason.Trim();
        attempt.FinishedAt = clock.UtcNow;
    }

    public void PrOpened(CodeFixAttempt attempt, string prUrl, int? prNumber)
    {
        Move(attempt, [CodeFixState.Implementing], CodeFixState.PrOpened);
        attempt.PrUrl = prUrl;
        attempt.PrNumber = prNumber;
        attempt.FinishedAt = clock.UtcNow;
    }

    public void Fail(CodeFixAttempt attempt, string reason)
    {
        Move(attempt, [CodeFixState.Eligible, CodeFixState.Planning, CodeFixState.Implementing], CodeFixState.Failed);
        attempt.FailureReason = reason;
        attempt.FinishedAt = clock.UtcNow;
    }

    public void Expire(CodeFixAttempt attempt)
    {
        Move(attempt, [CodeFixState.PlanReady], CodeFixState.Expired);
        attempt.FailureReason = "nobody approved or denied the plan in time";
        attempt.FinishedAt = clock.UtcNow;
    }

    public void Cancel(CodeFixAttempt attempt, string reason)
    {
        Move(attempt, CodeFixStates.Open, CodeFixState.Cancelled);
        attempt.FailureReason = reason;
        attempt.FinishedAt = clock.UtcNow;
    }

    private static void RefuseMachine(string actor, string verb)
    {
        // Stricter than the incident machine, which forbids only model identities because policy
        // admitting an action under L3 is a legitimate hephaisto/auto decision. There is no L3
        // for code: every hephaisto/* identity is refused, so no configuration can make the
        // agent approve its own repository write.
        if (string.IsNullOrWhiteSpace(actor)
            || IncidentStateMachine.IsForbiddenGranter(actor)
            || actor.Trim().StartsWith("hephaisto/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{actor}' may not {verb} a code fix: opening a repository to a coder is a human act.",
                nameof(actor));
        }
    }

    private static void Move(CodeFixAttempt attempt, IReadOnlyList<CodeFixState> from, CodeFixState to)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (!from.Contains(attempt.State))
            throw Illegal(attempt, to);

        attempt.State = to;
    }

    private static InvalidOperationException Illegal(CodeFixAttempt attempt, CodeFixState to) =>
        new($"code fix {attempt.Id} is {attempt.State}; it cannot move to {to}.");
}
