using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Core.CodeFix;

/// <summary>What one code-fix arm said.</summary>
public readonly record struct CodeFixArm
{
    public required string Name { get; init; }

    public required ModeArmStatus Status { get; init; }

    public CodeFixMode? Declared { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// The most this arm allows, or null for no opinion. Malformed and unreadable arms read as
    /// <see cref="CodeFixMode.Off"/>, not as Plan: on this axis Off costs nothing and hides
    /// nothing, so there is no reason to be generous to a typo.
    /// </summary>
    public CodeFixMode? Ceiling => Status switch
    {
        ModeArmStatus.Silent => null,
        ModeArmStatus.Declared => Declared,
        _ => CodeFixMode.Off,
    };

    public string Describe() => Status switch
    {
        ModeArmStatus.Silent => $"{Name}: not set",
        ModeArmStatus.Declared => $"{Name}: {Declared}",
        ModeArmStatus.Malformed => $"{Name}: malformed ({Detail}) - reads as Off",
        _ => $"{Name}: unreadable ({Detail}) - reads as Off",
    };
}

/// <summary>The resolved code-fix mode, with every override named.</summary>
public sealed record CodeFixModeResolution
{
    public required CodeFixMode Effective { get; init; }

    /// <summary>What the code-fix arms alone allow, before the agent's kill switch is consulted.</summary>
    public required CodeFixMode Declared { get; init; }

    public required string DecidedBy { get; init; }

    public required IReadOnlyList<CodeFixArm> Arms { get; init; }

    public required AgentMode AgentMode { get; init; }

    public required bool EmergencyStop { get; init; }

    public required bool RunawayLatched { get; init; }

    /// <summary>An agent arm was configured and could not be read or parsed.</summary>
    public required bool AgentArmFailed { get; init; }

    public string Explain() =>
        $"code-fix mode {Effective}, bound by {DecidedBy} [{string.Join("; ", Arms.Select(a => a.Describe()))}; agent {AgentMode}"
        + (EmergencyStop ? "; emergency stop engaged" : string.Empty)
        + (RunawayLatched ? "; runaway latch set" : string.Empty)
        + (AgentArmFailed ? "; an agent arm is unreadable" : string.Empty) + "]";
}

/// <summary>
/// Combines the code-fix arms with the agent's kill switch into the mode the coder stage runs in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most restrictive wins, and silence means Off.</b> That is the opposite default from the agent
/// mode, deliberately: an unconfigured agent still diagnoses (Observe), but an unconfigured coder
/// stage must spend nothing and start nothing.
/// </para>
/// <para>
/// <b>The agent's kill switch always wins.</b> It is consulted by arm, not by effective mode, because
/// the emergency stop and the runaway latch both parse as <see cref="AgentMode.Observe"/> on the agent
/// axis - and Observe is a mode this stage runs in happily. Reading only the effective agent mode
/// would make the big red button stop the cluster actions and leave the coders running.
/// </para>
/// </remarks>
public static class CodeFixModeResolver
{
    public static CodeFixModeResolution Resolve(
        IReadOnlyList<CodeFixArm> arms,
        ModeResolution agent,
        string emergencyStopArm,
        string runawayLatchArm)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(agent);

        CodeFixMode? declared = null;
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

        var declaredMode = declared ?? CodeFixMode.Off;

        var stop = agent.Arms.Any(a => a.Name == emergencyStopArm && a.Status != ModeArmStatus.Silent);
        var latched = agent.Arms.Any(a => a.Name == runawayLatchArm && a.Status == ModeArmStatus.Declared);
        var failed = agent.Arms.Any(a => a.Status is ModeArmStatus.Malformed or ModeArmStatus.Unreadable);

        var effective = declaredMode;
        var bound = decidedBy ?? "default";

        if (effective != CodeFixMode.Off)
        {
            if (agent.Effective == AgentMode.Off)
                (effective, bound) = (CodeFixMode.Off, $"agent mode Off ({agent.DecidedBy})");
            else if (stop)
                (effective, bound) = (CodeFixMode.Off, emergencyStopArm);
            else if (latched)
                (effective, bound) = (CodeFixMode.Off, runawayLatchArm);
            else if (failed)
                (effective, bound) = (CodeFixMode.Off, "an unreadable agent arm");
        }

        return new CodeFixModeResolution
        {
            Effective = effective,
            Declared = declaredMode,
            DecidedBy = bound,
            Arms = arms,
            AgentMode = agent.Effective,
            EmergencyStop = stop,
            RunawayLatched = latched,
            AgentArmFailed = failed,
        };
    }

    /// <summary>Strict parse: <c>off|plan|pr</c>. A number or a typo is Malformed, never a guess.</summary>
    public static CodeFixArm Parse(string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new CodeFixArm { Name = name, Status = ModeArmStatus.Silent };

        var trimmed = raw.Trim();

        CodeFixMode? mode = trimmed.ToLowerInvariant() switch
        {
            "off" => CodeFixMode.Off,
            "plan" => CodeFixMode.Plan,
            "pr" => CodeFixMode.Pr,
            _ => null,
        };

        return mode is { } m
            ? new CodeFixArm { Name = name, Status = ModeArmStatus.Declared, Declared = m, Detail = trimmed }
            : new CodeFixArm
            {
                Name = name,
                Status = ModeArmStatus.Malformed,
                Detail = $"'{trimmed}' is not one of off|plan|pr",
            };
    }

    public static CodeFixArm Unreadable(string name, string detail) =>
        new() { Name = name, Status = ModeArmStatus.Unreadable, Detail = detail };
}
