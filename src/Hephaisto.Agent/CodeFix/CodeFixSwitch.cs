using Microsoft.Extensions.Options;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.CodeFix;

namespace Hephaisto.Agent.CodeFix;

public interface ICodeFixSwitch
{
    /// <summary>The code-fix mode with the agent's kill switch applied. Re-read on every call.</summary>
    Task<CodeFixModeResolution> ResolveAsync(CancellationToken ct);
}

/// <summary>
/// The code-fix mode's two arms - the <c>CodeFix__Mode</c> environment variable and the
/// <c>codeFixMode</c> key of the switch ConfigMap - combined with all three arms of the agent's kill
/// switch.
/// </summary>
/// <remarks>
/// The ConfigMap arm is the fast way down: <c>kubectl edit cm hephaisto-switches</c> sets
/// <c>codeFixMode: off</c> within a kubelet sync, and the watcher cancels running coders on its next
/// poll. It is re-read every call for the same reason the agent's is: a cached stop is not a stop. An
/// absent key is silence, which on this axis is already Off.
/// </remarks>
public sealed class CodeFixSwitch(
    IConfiguration configuration,
    IOptionsMonitor<KillSwitchOptions> killSwitchOptions,
    IKillSwitch killSwitch,
    ILogger<CodeFixSwitch> logger) : ICodeFixSwitch
{
    public const string EnvironmentArm = "env:CodeFix__Mode";
    public const string ConfigMapArm = "configmap:codeFixMode";
    public const string ConfigMapKey = "codeFixMode";

    public async Task<CodeFixModeResolution> ResolveAsync(CancellationToken ct)
    {
        var agent = await killSwitch.ResolveAsync(ct).ConfigureAwait(false);

        CodeFixArm[] arms =
        [
            CodeFixModeResolver.Parse(EnvironmentArm, configuration[$"{CodeFixOptions.SectionName}:Mode"]),
            ReadConfigMapArm(),
        ];

        return CodeFixModeResolver.Resolve(arms, agent, KillSwitch.ConfigMapStopArm, KillSwitch.DatabaseArm);
    }

    private CodeFixArm ReadConfigMapArm()
    {
        var dir = killSwitchOptions.CurrentValue.SwitchDirectory;

        if (string.IsNullOrWhiteSpace(dir))
            return CodeFixModeResolver.Parse(ConfigMapArm, null);

        var path = Path.Combine(dir, ConfigMapKey);

        try
        {
            return File.Exists(path)
                ? CodeFixModeResolver.Parse(ConfigMapArm, File.ReadAllText(path))
                : CodeFixModeResolver.Parse(ConfigMapArm, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the code-fix switch at {Path}; reading it as Off", path);
            return CodeFixModeResolver.Unreadable(ConfigMapArm, ex.Message);
        }
    }
}
