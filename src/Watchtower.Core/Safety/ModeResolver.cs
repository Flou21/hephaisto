using Watchtower.Core.Domain;

namespace Watchtower.Core.Safety;

/// <summary>
/// What one arm of the kill switch has to say about the mode.
/// </summary>
/// <remarks>
/// The distinction between <see cref="Silent"/> and the two failure statuses is the whole
/// reason this is an enum rather than a nullable <see cref="AgentMode"/>, and getting it
/// wrong inverts the safety property:
/// <list type="bullet">
/// <item><b>Silent</b> means "this arm is not configured here" - a developer running
/// <c>dotnet run</c> with no env var and no mounted ConfigMap. It must NOT constrain, or
/// the agent could never leave Observe outside Kubernetes.</item>
/// <item><b>Malformed</b> means "this arm is configured and I could not understand it" -
/// <c>WATCHTOWER_MODE=atuo</c>. It MUST constrain to Observe. Treating a typo as silence
/// turns a fat-fingered kill switch into an autonomy upgrade, which is the exact failure
/// this type exists to prevent.</item>
/// <item><b>Unreadable</b> means "this arm is configured and I could not reach it" - the
/// switch file is mounted but the read threw. Also constrains to Observe.</item>
/// </list>
/// </remarks>
public enum ModeArmStatus
{
    /// <summary>Not configured in this environment. Expresses no opinion.</summary>
    Silent = 0,

    /// <summary>Configured and understood. Constrains to <see cref="ModeArm.Declared"/>.</summary>
    Declared = 1,

    /// <summary>Configured but not parseable. Constrains to Observe.</summary>
    Malformed = 2,

    /// <summary>Configured but not reachable. Constrains to Observe.</summary>
    Unreadable = 3,
}

/// <summary>One arm of the kill switch: its name, what it said, and whether it could speak.</summary>
public readonly record struct ModeArm
{
    public required string Name { get; init; }

    public required ModeArmStatus Status { get; init; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="ModeArmStatus.Declared"/>.</summary>
    public AgentMode? Declared { get; init; }

    /// <summary>Human-readable note - the raw value that would not parse, the IO error, the file path.</summary>
    public string? Detail { get; init; }

    public static ModeArm Silent(string name, string? detail = null) =>
        new() { Name = name, Status = ModeArmStatus.Silent, Detail = detail };

    public static ModeArm Declaring(string name, AgentMode mode, string? detail = null) =>
        new() { Name = name, Status = ModeArmStatus.Declared, Declared = mode, Detail = detail };

    public static ModeArm Malformed(string name, string detail) =>
        new() { Name = name, Status = ModeArmStatus.Malformed, Detail = detail };

    public static ModeArm Unreadable(string name, string detail) =>
        new() { Name = name, Status = ModeArmStatus.Unreadable, Detail = detail };

    /// <summary>
    /// The most permissive mode this arm will allow, or null if it expresses no opinion.
    /// </summary>
    public AgentMode? Ceiling => Status switch
    {
        ModeArmStatus.Silent => null,
        ModeArmStatus.Declared => Declared,

        // Both failure modes collapse to Observe rather than to Off. Off would stop
        // ingestion entirely, which hides the very problem the operator needs to see: an
        // agent that has gone quiet looks identical to a cluster that is healthy. Observe
        // keeps detection, correlation and the UI alive while guaranteeing no mutation.
        _ => AgentMode.Observe,
    };

    public string Describe() => Status switch
    {
        ModeArmStatus.Silent => $"{Name}: not set",
        ModeArmStatus.Declared => $"{Name}: {Declared}",
        ModeArmStatus.Malformed => $"{Name}: malformed ({Detail}) - reads as Observe",
        ModeArmStatus.Unreadable => $"{Name}: unreadable ({Detail}) - reads as Observe",
        _ => $"{Name}: unknown",
    };
}

/// <summary>The resolved mode, and enough detail to explain it to a human without a debugger.</summary>
public sealed record ModeResolution
{
    public required AgentMode Effective { get; init; }

    /// <summary>The arm that actually bound the result - the one a human has to change.</summary>
    public required string DecidedBy { get; init; }

    public required IReadOnlyList<ModeArm> Arms { get; init; }

    /// <summary>True when some arm held the mode below what another arm asked for.</summary>
    public required bool IsConstrained { get; init; }

    public string Explain() =>
        $"effective mode {Effective}, bound by {DecidedBy} [{string.Join("; ", Arms.Select(a => a.Describe()))}]";
}

/// <summary>
/// Combines the independent arms of the kill switch into the mode the agent actually runs in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The most restrictive arm wins.</b> <see cref="AgentMode"/> is declared in order of
/// increasing permissiveness (Off, Observe, DryRun, Auto), so "most restrictive" is exactly
/// <c>Min</c> over the arms that expressed an opinion. That ordering is load-bearing; a
/// test pins it so that reordering the enum fails loudly rather than silently inverting
/// every kill switch in the system.
/// </para>
/// <para>
/// The direction matters more than the mechanism. An operator who sets
/// <c>WATCHTOWER_MODE=observe</c> to STOP an agent running in Auto must actually stop it -
/// if any arm could raise the mode, the big red button would be painted on. So no arm can
/// ever raise the result, only lower it, and an arm that cannot be read lowers it too.
/// </para>
/// <para>
/// When every arm is silent the result is Observe, never Auto and never Off. Auto would make
/// "forgot to configure it" the most dangerous state in the system. Off would make an
/// unconfigured agent look like a healthy cluster, because it would report nothing at all.
/// </para>
/// </remarks>
public static class ModeResolver
{
    /// <summary>What an entirely unconfigured agent runs as: fully diagnostic, never mutating.</summary>
    public const AgentMode WhenNoArmSpeaks = AgentMode.Observe;

    public static ModeResolution Resolve(params ModeArm[] arms) => Resolve((IReadOnlyList<ModeArm>)arms);

    public static ModeResolution Resolve(IReadOnlyList<ModeArm> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);

        var effective = AgentMode.Auto;
        string? decidedBy = null;
        var anySpoke = false;
        AgentMode? mostPermissiveRequest = null;

        foreach (var arm in arms)
        {
            if (arm.Ceiling is not { } ceiling)
            {
                continue;
            }

            anySpoke = true;

            if (arm.Status is ModeArmStatus.Declared && (mostPermissiveRequest is null || ceiling > mostPermissiveRequest))
            {
                mostPermissiveRequest = ceiling;
            }

            // Strictly-less keeps the FIRST arm at the binding value as the one named, so
            // the explanation is stable when two arms agree rather than flipping with
            // enumeration order.
            if (decidedBy is null || ceiling < effective)
            {
                effective = ceiling;
                decidedBy = arm.Name;
            }
        }

        if (!anySpoke)
        {
            return new ModeResolution
            {
                Effective = WhenNoArmSpeaks,
                DecidedBy = "default",
                Arms = arms,
                IsConstrained = false,
            };
        }

        return new ModeResolution
        {
            Effective = effective,
            DecidedBy = decidedBy!,
            Arms = arms,
            IsConstrained = mostPermissiveRequest is { } asked && effective < asked,
        };
    }

    /// <summary>
    /// Parses a mode name into an arm, strictly. Anything unrecognised is
    /// <see cref="ModeArmStatus.Malformed"/> rather than a guess.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT use <c>Enum.TryParse</c> alone: that accepts the underlying
    /// integer, so <c>WATCHTOWER_MODE=3</c> would quietly mean Auto. An operator typing a
    /// number into a kill switch has misunderstood it, and the safe reading of a
    /// misunderstanding is Observe.
    /// </remarks>
    public static ModeArm Parse(string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ModeArm.Silent(name);
        }

        var trimmed = raw.Trim();

        // Accept the spellings an operator actually types. Hyphen and underscore are
        // allowed for dry-run because both appear in the docs and in shell history.
        var canonical = trimmed.Replace("-", string.Empty).Replace("_", string.Empty);

        return canonical.ToLowerInvariant() switch
        {
            "off" => ModeArm.Declaring(name, AgentMode.Off, trimmed),
            "observe" => ModeArm.Declaring(name, AgentMode.Observe, trimmed),
            "dryrun" => ModeArm.Declaring(name, AgentMode.DryRun, trimmed),
            "auto" => ModeArm.Declaring(name, AgentMode.Auto, trimmed),
            _ => ModeArm.Malformed(name, $"'{trimmed}' is not one of off|observe|dryrun|auto"),
        };
    }

    /// <summary>
    /// Parses the boolean emergency stop. Anything that is not an unambiguous "false"
    /// engages the switch.
    /// </summary>
    /// <remarks>
    /// The asymmetry is deliberate and is the opposite of how a normal config flag parses.
    /// A human editing this under time pressure may type <c>yes</c>, <c>TRUE</c>, <c>1</c>
    /// or <c>engaged</c>; every one of those must stop the agent. The only way to NOT
    /// engage the stop is to say false clearly. A garbled emergency stop is an engaged one.
    /// </remarks>
    public static ModeArm ParseEmergencyStop(string name, string? raw)
    {
        if (raw is null)
        {
            return ModeArm.Silent(name);
        }

        var trimmed = raw.Trim();

        if (trimmed.Length == 0)
        {
            return ModeArm.Silent(name);
        }

        var engaged = trimmed.ToLowerInvariant() switch
        {
            "false" or "no" or "off" or "0" or "disengaged" => false,
            _ => true,
        };

        return engaged
            ? ModeArm.Declaring(name, AgentMode.Observe, $"engaged ('{trimmed}')")
            : ModeArm.Silent(name, "disengaged");
    }
}
