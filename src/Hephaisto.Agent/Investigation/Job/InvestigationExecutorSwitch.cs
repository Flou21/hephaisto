using Microsoft.Extensions.Options;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Agent.Investigations.Jobs;

public interface IInvestigationExecutorSwitch
{
    /// <summary>The executor with the enable flag and the kill switch applied. Re-read on every call.</summary>
    Task<InvestigationExecutorResolution> ResolveAsync(CancellationToken ct);
}

/// <summary>
/// The executor's two arms - <c>Investigation__Job__Executor</c> and the
/// <c>investigationExecutor</c> key of the switch ConfigMap - with the agent's kill switch.
/// </summary>
/// <remarks>
/// The ConfigMap arm is the fast way back to in-process: <c>investigationExecutor: inprocess</c>
/// takes effect within a kubelet sync, for the next investigation. It is re-read every call, like
/// every other switch here. Built on <see cref="CodeFix.CodeFixSwitch"/>'s pattern, not on its type:
/// the two axes answer different questions and must be switchable apart.
/// </remarks>
public sealed class InvestigationExecutorSwitch(
    IOptionsMonitor<InvestigationJobOptions> options,
    IOptionsMonitor<KillSwitchOptions> killSwitchOptions,
    IKillSwitch killSwitch,
    ILogger<InvestigationExecutorSwitch> logger) : IInvestigationExecutorSwitch
{
    public const string EnvironmentArm = "env:Investigation__Job__Executor";
    public const string ConfigMapArm = "configmap:investigationExecutor";
    public const string ConfigMapKey = "investigationExecutor";

    public async Task<InvestigationExecutorResolution> ResolveAsync(CancellationToken ct)
    {
        var agent = await killSwitch.ResolveAsync(ct).ConfigureAwait(false);
        var o = options.CurrentValue;

        InvestigationExecutorArm[] arms =
        [
            InvestigationExecutorResolver.Parse(EnvironmentArm, o.Executor),
            ReadConfigMapArm(),
        ];

        return InvestigationExecutorResolver.Resolve(
            arms, o.Enabled, agent, KillSwitch.ConfigMapStopArm, KillSwitch.DatabaseArm);
    }

    private InvestigationExecutorArm ReadConfigMapArm()
    {
        var dir = killSwitchOptions.CurrentValue.SwitchDirectory;

        if (string.IsNullOrWhiteSpace(dir))
            return InvestigationExecutorResolver.Parse(ConfigMapArm, null);

        var path = Path.Combine(dir, ConfigMapKey);

        try
        {
            return File.Exists(path)
                ? InvestigationExecutorResolver.Parse(ConfigMapArm, File.ReadAllText(path))
                : InvestigationExecutorResolver.Parse(ConfigMapArm, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the executor switch at {Path}; reading it as InProcess", path);
            return InvestigationExecutorResolver.Unreadable(ConfigMapArm, ex.Message);
        }
    }
}

/// <summary>What <c>GET /api/investigations/executor</c> answers.</summary>
public sealed record InvestigationExecutorView(
    string Effective,
    string Explanation,
    bool Enabled,
    int MaxConcurrentJobs,
    int MaxJobsPerHour);

public static class InvestigationJobEndpoints
{
    /// <summary>
    /// Read-only, open to the console's readers. There is deliberately no route that sets the
    /// executor: it is a Helm value and a ConfigMap key, like every other mode here.
    /// </summary>
    public static IEndpointRouteBuilder MapInvestigationJobEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/investigations/executor", async (
                IInvestigationExecutorSwitch executor,
                IOptionsMonitor<InvestigationJobOptions> options,
                CancellationToken ct) =>
            {
                var resolution = await executor.ResolveAsync(ct).ConfigureAwait(false);
                var o = options.CurrentValue;

                return TypedResults.Ok(new InvestigationExecutorView(
                    resolution.Effective.ToString(),
                    resolution.Explain(),
                    o.Enabled,
                    o.MaxConcurrentJobs,
                    o.MaxJobsPerHour));
            })
            .WithName("InvestigationExecutor");

        return app;
    }
}
