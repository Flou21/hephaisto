namespace Hephaisto.Core.CodeFix;

/// <summary>
/// How far the code-fix stage may go. A second axis beside <see cref="Domain.AgentMode"/>, not a
/// point on it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Domain.AgentMode"/> answers "may the agent mutate the cluster". This answers "may
/// the agent spend money on a coder and, after a human approves, open a Draft PR". Tying the two
/// together would lock production - which runs in Observe - out of the feature entirely, or force
/// an operator to widen cluster autonomy to get a git feature. Neither is a trade anyone should be
/// made to take, so the axes are independent, and the kill switch still overrides both.
/// </para>
/// <para>
/// <b>Declared in order of increasing permissiveness, and the order is load-bearing:</b>
/// <see cref="CodeFixModeResolver"/> takes the minimum over its arms, exactly as
/// <see cref="Safety.ModeResolver"/> does for the agent mode. A test pins it.
/// </para>
/// </remarks>
public enum CodeFixMode
{
    /// <summary>No coder is ever started. The default, and what silence means.</summary>
    Off = 0,

    /// <summary>A read-only coder analyses the repository and writes a plan. Nothing is pushed.</summary>
    Plan = 1,

    /// <summary>As Plan, and a human may approve the plan into a Draft PR on an assigned branch.</summary>
    Pr = 2,
}

/// <summary>Where one code-fix attempt is. Its own lifecycle - never an <see cref="Domain.IncidentState"/>.</summary>
/// <remarks>
/// An incident is routinely Closed while its PR is still open for review, so folding these into the
/// incident's states would force one of the two facts to lie.
/// </remarks>
public enum CodeFixState
{
    /// <summary>Eligibility passed and a row exists; no Job yet.</summary>
    Eligible = 0,

    /// <summary>The read-only plan Job is running.</summary>
    Planning = 1,

    /// <summary>A plan exists and waits for a human. The only state approve and deny act on.</summary>
    PlanReady = 2,

    /// <summary>Approved; the implement Job is running.</summary>
    Implementing = 3,

    /// <summary>A Draft PR exists. Terminal for Hephaisto: a human reviews, merges and deploys.</summary>
    PrOpened = 4,

    Failed = 5,

    Denied = 6,

    /// <summary>Nobody answered within the approval timeout.</summary>
    Expired = 7,

    /// <summary>The kill switch or the code-fix mode stopped it mid-flight.</summary>
    Cancelled = 8,
}

/// <summary>Which phase a coder Job runs.</summary>
public enum CodeFixPhase
{
    Plan = 0,
    Implement = 1,
}

public static class CodeFixStates
{
    /// <summary>
    /// States that hold a slot: at most one per incident, enforced by a partial unique index as
    /// well as by <see cref="CodeFixReasonCode.AttemptAlreadyOpen"/>.
    /// </summary>
    public static readonly IReadOnlyList<CodeFixState> Open =
        [CodeFixState.Eligible, CodeFixState.Planning, CodeFixState.PlanReady, CodeFixState.Implementing];

    /// <summary>States in which a Job is (or should be) running.</summary>
    public static readonly IReadOnlyList<CodeFixState> Running = [CodeFixState.Planning, CodeFixState.Implementing];

    public static bool IsOpen(this CodeFixState state) => Open.Contains(state);

    public static bool IsRunning(this CodeFixState state) => Running.Contains(state);
}
