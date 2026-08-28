using Microsoft.Extensions.Options;
using Watchtower.Agent.Persistence.Repositories;
using Watchtower.Core.Domain;
using Watchtower.Core.Safety;

namespace Watchtower.Agent.Safety;

public sealed class KillSwitchOptions
{
    public const string SectionName = "KillSwitch";

    /// <summary>
    /// Directory the switch ConfigMap is projected into, or null when there is no ConfigMap
    /// arm in this environment (local <c>dotnet run</c>). Null makes the arm silent; a set
    /// path whose files cannot be read makes it Unreadable, which is a very different thing.
    /// </summary>
    public string? SwitchDirectory { get; set; }

    public string ModeFileName { get; set; } = "mode";

    public string EmergencyStopFileName { get; set; } = "killSwitch";

    /// <summary>Name of the environment arm, for messages. Matches the manifest.</summary>
    public string ModeEnvironmentVariable { get; set; } = "WATCHTOWER_MODE";
}

public interface IKillSwitch
{
    /// <summary>
    /// The env and ConfigMap arms as raw arms, for callers that hold their own database read
    /// and need to combine all three themselves.
    /// </summary>
    IReadOnlyList<ModeArm> ExternalArms { get; }

    /// <summary>
    /// The mode allowed by the arms that need no database: the environment variable and the
    /// projected ConfigMap. Callers that are already inside a transaction combine this with
    /// the mode row they read themselves, so the database arm stays transactional.
    /// </summary>
    ModeResolution External { get; }

    /// <summary>All three arms, including the database row. Use everywhere else.</summary>
    Task<ModeResolution> ResolveAsync(CancellationToken ct);
}

/// <summary>
/// The three arms of the kill switch, combined by <see cref="ModeResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// The arms exist because they fail differently, and an operator reaches for whichever one
/// still works:
/// </para>
/// <list type="bullet">
/// <item><b>The environment variable</b> is fixed at deploy time and shows up in
/// <c>kubectl describe pod</c>. It survives a compromised process because changing it
/// requires replacing the pod.</item>
/// <item><b>The ConfigMap</b> is the big red button: <c>kubectl edit cm watchtower-switches</c>
/// takes effect within a kubelet sync without a restart, which is what you want when the
/// agent is misbehaving and a restart might not come back.</item>
/// <item><b>The database row</b> is the only arm a human can flip from the UI, and the only
/// one that can be read inside the same transaction that admits an action - so it is the
/// only one that cannot be raced by a concurrent execution.</item>
/// </list>
/// <para>
/// The ConfigMap arm is re-read on every call rather than cached. That is deliberate and is
/// what the ConfigMap's own comment demands: a cached emergency stop is not an emergency
/// stop. The read is a handful of bytes off a tmpfs-backed projected volume and happens once
/// per investigation or admission, not in a hot loop.
/// </para>
/// </remarks>
public sealed class KillSwitch(
    IConfiguration configuration,
    IOptionsMonitor<KillSwitchOptions> options,
    IServiceScopeFactory scopes,
    ILogger<KillSwitch> logger) : IKillSwitch
{
    public const string EnvironmentArm = "env:WATCHTOWER_MODE";
    public const string ConfigMapModeArm = "configmap:mode";
    public const string ConfigMapStopArm = "configmap:killSwitch";
    public const string DatabaseArm = "db:agent_mode";

    public ModeResolution External => ModeResolver.Resolve(ExternalArms);

    public async Task<ModeResolution> ResolveAsync(CancellationToken ct)
    {
        var arms = new List<ModeArm>(ExternalArms) { await ReadDatabaseArmAsync(ct).ConfigureAwait(false) };

        return ModeResolver.Resolve(arms);
    }

    /// <summary>The env and ConfigMap arms, in the order they are reported to humans.</summary>
    public IReadOnlyList<ModeArm> ExternalArms
    {
        get
        {
            var o = options.CurrentValue;

            return
            [
                ModeResolver.Parse(EnvironmentArm, configuration[o.ModeEnvironmentVariable]),
                ReadStopFile(o),
                ReadModeFile(o),
            ];
        }
    }

    private ModeArm ReadModeFile(KillSwitchOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.SwitchDirectory))
        {
            return ModeArm.Silent(ConfigMapModeArm, "no switch directory configured");
        }

        var path = Path.Combine(o.SwitchDirectory, o.ModeFileName);

        try
        {
            // A configured-but-absent file is Unreadable, not Silent. Deleting the key out
            // of the ConfigMap must not read as "no opinion" and restore Auto.
            return !File.Exists(path)
                ? ModeArm.Unreadable(ConfigMapModeArm, $"{path} does not exist")
                : ModeResolver.Parse(ConfigMapModeArm, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the mode switch at {Path}; reading it as Observe", path);

            return ModeArm.Unreadable(ConfigMapModeArm, ex.Message);
        }
    }

    private ModeArm ReadStopFile(KillSwitchOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.SwitchDirectory))
        {
            return ModeArm.Silent(ConfigMapStopArm, "no switch directory configured");
        }

        var path = Path.Combine(o.SwitchDirectory, o.EmergencyStopFileName);

        try
        {
            // Absent emergency stop is genuinely silent: the key is optional, and its
            // absence means "nobody pressed it". That is the opposite of the mode file,
            // where absence means a value went missing.
            return !File.Exists(path)
                ? ModeArm.Silent(ConfigMapStopArm, "not present")
                : ModeResolver.ParseEmergencyStop(ConfigMapStopArm, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the emergency stop at {Path}; treating it as engaged", path);

            return ModeArm.Unreadable(ConfigMapStopArm, ex.Message);
        }
    }

    private async Task<ModeArm> ReadDatabaseArmAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentModeStore>();
            var row = await store.GetAsync(ct).ConfigureAwait(false);

            if (row.RunawayLatched)
            {
                return ModeArm.Declaring(
                    DatabaseArm,
                    AgentMode.Observe,
                    $"runaway latch: {row.LatchReason ?? "unknown reason"}");
            }

            return ModeArm.Declaring(DatabaseArm, row.Mode);
        }
        catch (Exception ex)
        {
            // "Postgres unreachable => refuse to act" from the design. Unreadable collapses
            // to Observe, so a database outage cannot leave the agent mutating the cluster
            // with no audit trail to write the mutation to. No audit, no action.
            logger.LogError(ex, "Could not read the agent mode row; reading it as Observe");

            return ModeArm.Unreadable(DatabaseArm, ex.Message);
        }
    }
}
