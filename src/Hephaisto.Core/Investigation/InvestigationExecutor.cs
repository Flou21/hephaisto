using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Core.Investigations;

/// <summary>
/// Who runs an investigation's model loop (v0.12.0 F5). A third axis beside
/// <see cref="AgentMode"/> and <see cref="CodeFix.CodeFixMode"/>, not a point on either.
/// </summary>
/// <remarks>
/// <para>
/// Either way Hephaisto owns the tools, records every step and grounds the conclusion; this axis
/// only says which model reads the results. In-process is the v0.11 loop against
/// <c>Llm:Provider</c>. Job runs the loop as Claude Code in a Job next to the coder, reaching the
/// same tools through Hephaisto's investigator endpoint.
/// </para>
/// <para>
/// <b>Declared in order of increasing reach, and the order is load-bearing:</b> the resolver takes
/// the minimum over its arms, as every other mode axis here does, so an operator can always take it
/// down from the ConfigMap without a rollout.
/// </para>
/// </remarks>
public enum InvestigationExecutor
{
    /// <summary>The model loop runs inside the agent. The default, and what silence means.</summary>
    InProcess = 0,

    /// <summary>The model loop runs in a Job; Hephaisto serves it the tools.</summary>
    Job = 1,
}

/// <summary>
/// What <see cref="Domain.Investigation.Executor"/> records: who ran the model loop of one
/// investigation. Strings rather than the enum, because the third value is not a mode.
/// </summary>
public static class InvestigationExecutors
{
    /// <summary>The v0.11 loop in the agent. Also what a null reads as: every row before v0.12.0.</summary>
    public const string InProcess = "InProcess";

    /// <summary>A Job ran the model loop against Hephaisto's tools.</summary>
    public const string Job = "Job";

    /// <summary>A Job was started and gave no answer, and the agent investigated in-process instead.</summary>
    public const string JobFallback = "JobFallback";
}

/// <summary>What one executor arm said.</summary>
public readonly record struct InvestigationExecutorArm
{
    public required string Name { get; init; }

    public required ModeArmStatus Status { get; init; }

    public InvestigationExecutor? Declared { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// The most this arm allows, or null for no opinion. A typo reads as in-process: that path works
    /// without any of the Job's infrastructure, so it is the one a mistake should land on.
    /// </summary>
    public InvestigationExecutor? Ceiling => Status switch
    {
        ModeArmStatus.Silent => null,
        ModeArmStatus.Declared => Declared,
        _ => InvestigationExecutor.InProcess,
    };

    public string Describe() => Status switch
    {
        ModeArmStatus.Silent => $"{Name}: not set",
        ModeArmStatus.Declared => $"{Name}: {Declared}",
        ModeArmStatus.Malformed => $"{Name}: malformed ({Detail}) - reads as InProcess",
        _ => $"{Name}: unreadable ({Detail}) - reads as InProcess",
    };
}

/// <summary>The resolved executor, with every override named.</summary>
public sealed record InvestigationExecutorResolution
{
    public required InvestigationExecutor Effective { get; init; }

    /// <summary>What the arms alone allow, before the enable flag and the kill switch.</summary>
    public required InvestigationExecutor Declared { get; init; }

    public required string DecidedBy { get; init; }

    public required IReadOnlyList<InvestigationExecutorArm> Arms { get; init; }

    public required bool Enabled { get; init; }

    public required bool EmergencyStop { get; init; }

    public required bool RunawayLatched { get; init; }

    public string Explain() =>
        $"investigation executor {Effective}, bound by {DecidedBy} [{string.Join("; ", Arms.Select(a => a.Describe()))}"
        + (Enabled ? string.Empty : "; investigation.job.enabled is false")
        + (EmergencyStop ? "; emergency stop engaged" : string.Empty)
        + (RunawayLatched ? "; runaway latch set" : string.Empty) + "]";
}

/// <summary>
/// Combines the executor arms, the enable flag and the agent's kill switch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most restrictive wins, and silence means in-process.</b> An install that never heard of F5
/// resolves to exactly what v0.11 did.
/// </para>
/// <para>
/// <b>Not enabled means in-process whatever the arms say.</b> The enable flag is what renders the
/// investigator port, its NetworkPolicy and the Job's credentials; a ConfigMap edited to <c>job</c>
/// on an install without them would only launch Jobs that cannot reach anything.
/// </para>
/// <para>
/// <b>The emergency stop and the runaway latch force in-process</b>, read by arm for the reason
/// <see cref="CodeFix.CodeFixModeResolver"/> gives: both parse as Observe on the agent axis. A stop
/// means "nothing new and nothing expensive"; a Job is both, and the in-process loop is the
/// behaviour the operator already knows.
/// </para>
/// </remarks>
public static class InvestigationExecutorResolver
{
    public static InvestigationExecutorResolution Resolve(
        IReadOnlyList<InvestigationExecutorArm> arms,
        bool enabled,
        ModeResolution agent,
        string emergencyStopArm,
        string runawayLatchArm)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(agent);

        InvestigationExecutor? declared = null;
        string? decidedBy = null;

        foreach (var arm in arms)
        {
            if (arm.Ceiling is not { } ceiling)
                continue;

            if (declared is null || ceiling < declared)
            {
                declared = ceiling;
                decidedBy = arm.Name;
            }
        }

        var declaredExecutor = declared ?? InvestigationExecutor.InProcess;

        var stop = agent.Arms.Any(a => a.Name == emergencyStopArm && a.Status != ModeArmStatus.Silent);
        var latched = agent.Arms.Any(a => a.Name == runawayLatchArm && a.Status == ModeArmStatus.Declared);

        var effective = declaredExecutor;
        var bound = decidedBy ?? "default";

        if (effective != InvestigationExecutor.InProcess)
        {
            if (!enabled)
                (effective, bound) = (InvestigationExecutor.InProcess, "investigation.job.enabled");
            else if (stop)
                (effective, bound) = (InvestigationExecutor.InProcess, emergencyStopArm);
            else if (latched)
                (effective, bound) = (InvestigationExecutor.InProcess, runawayLatchArm);
        }

        return new InvestigationExecutorResolution
        {
            Effective = effective,
            Declared = declaredExecutor,
            DecidedBy = bound,
            Arms = arms,
            Enabled = enabled,
            EmergencyStop = stop,
            RunawayLatched = latched,
        };
    }

    /// <summary>Strict parse: <c>inprocess|job</c>. A number or a typo is Malformed, never a guess.</summary>
    public static InvestigationExecutorArm Parse(string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new InvestigationExecutorArm { Name = name, Status = ModeArmStatus.Silent };

        var trimmed = raw.Trim();

        InvestigationExecutor? executor = trimmed.ToLowerInvariant() switch
        {
            "inprocess" or "in-process" => InvestigationExecutor.InProcess,
            "job" => InvestigationExecutor.Job,
            _ => null,
        };

        return executor is { } e
            ? new InvestigationExecutorArm { Name = name, Status = ModeArmStatus.Declared, Declared = e, Detail = trimmed }
            : new InvestigationExecutorArm
            {
                Name = name,
                Status = ModeArmStatus.Malformed,
                Detail = $"'{trimmed}' is not one of inprocess|job",
            };
    }

    public static InvestigationExecutorArm Unreadable(string name, string detail) =>
        new() { Name = name, Status = ModeArmStatus.Unreadable, Detail = detail };
}

/// <summary>Why one investigation did or did not go to a Job.</summary>
public enum ExecutorChoice
{
    /// <summary>A Job runs the model loop.</summary>
    Job = 0,

    /// <summary>The resolved executor is in-process.</summary>
    InProcessByMode = 1,

    /// <summary>Every Job slot is taken; this one runs in-process now rather than waiting.</summary>
    Overflow = 2,

    /// <summary>The hourly Job cap is spent.</summary>
    HourlyCapReached = 3,

    /// <summary>An incident about another cluster: the Job's tools read only this one.</summary>
    ForeignCluster = 4,
}

public sealed record ExecutorCaps
{
    public required int MaxConcurrentJobs { get; init; }

    public required int MaxJobsPerHour { get; init; }
}

/// <summary>
/// Decides, per investigation, whether it goes to a Job. One pure function, so the whole rule is
/// one table in a test.
/// </summary>
/// <remarks>
/// <b>Overflow runs in-process, it never queues.</b> A storm - production opened 217 readiness
/// incidents in a few weeks - must not drain a subscription or wait behind it; the in-process loop
/// is the path that already copes. Waiting for a slot would also hold one of the worker's two
/// slots idle, starving the incidents behind it.
/// </remarks>
public static class InvestigationExecutorPolicy
{
    public static ExecutorChoice Decide(
        InvestigationExecutor effective,
        ExecutorCaps caps,
        int runningJobs,
        int jobsLastHour,
        bool foreignCluster)
    {
        ArgumentNullException.ThrowIfNull(caps);

        if (effective != InvestigationExecutor.Job)
            return ExecutorChoice.InProcessByMode;

        if (foreignCluster)
            return ExecutorChoice.ForeignCluster;

        if (runningJobs >= caps.MaxConcurrentJobs)
            return ExecutorChoice.Overflow;

        if (jobsLastHour >= caps.MaxJobsPerHour)
            return ExecutorChoice.HourlyCapReached;

        return ExecutorChoice.Job;
    }
}
