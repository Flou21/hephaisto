using Microsoft.Extensions.DependencyInjection.Extensions;
using Hephaisto.Agent.Options;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Policy;

namespace Hephaisto.Agent.CodeFix;

public static class CodeFixServiceCollectionExtensions
{
    /// <summary>
    /// The code-fix stage. Registered unconditionally - evaluation must run even when the mode is Off,
    /// because "would have started" is the evidence an operator turns the mode on from - but every
    /// default is inert, and a Job needs a mode, a mapped repository and an image.
    /// </summary>
    public static IServiceCollection AddHephaistoCodeFix(this IServiceCollection services, IConfiguration configuration)
    {
        var policy = configuration.GetSection(PolicyOptions.SectionName).Get<PolicyOptions>() ?? new PolicyOptions();
        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        var kubernetesEnabled = !string.Equals(configuration["Kubernetes:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

        services.AddOptions<CodeFixOptions>()
            .Bind(configuration.GetSection(CodeFixOptions.SectionName))
            .Validate(
                o => IsSafeNamespace(o.Namespace, policy),
                "CodeFix:Namespace must be set and must not be default, kube-*, a protected namespace or an "
                    + "actionable one: the coder namespace is the one place Hephaisto may create Jobs, and it must "
                    + "never be a place Hephaisto may also act.")
            .Validate(
                o => ModeOf(o) != CodeFixMode.Pr || auth.IsConfigured || o.AllowUnauthenticatedApproval,
                "CodeFix:Mode=Pr needs Auth:Enabled with an Authority: approving a repository write needs an "
                    + "authenticated human, not an attributed string. (CodeFix:AllowUnauthenticatedApproval exists for "
                    + "throwaway e2e clusters only.)")
            .Validate(
                o => ModeOf(o) == CodeFixMode.Off || !kubernetesEnabled || !string.IsNullOrWhiteSpace(o.Image),
                "CodeFix:Image must be set when CodeFix:Mode is not Off.")
            .Validate(
                o => o.Repositories.TrueForAll(r => !string.IsNullOrWhiteSpace(r.Workload) && Uri.TryCreate(r.Url, UriKind.Absolute, out _)),
                "every CodeFix:Repositories entry needs a Workload and an absolute Url.")
            .Validate(
                o => o.PlanDeadline > TimeSpan.FromMinutes(4) && o.ImplementDeadline > TimeSpan.FromMinutes(4),
                "CodeFix deadlines must exceed four minutes; the runner reserves three to report.")
            .ValidateOnStart();

        services.TryAddSingleton<CodeFixMetrics>();
        services.TryAddSingleton<CodeFixStateMachine>();
        services.TryAddSingleton<CodeFixRequestBuilder>();
        services.TryAddSingleton<ICodeFixSwitch, CodeFixSwitch>();

        if (kubernetesEnabled)
        {
            services.TryAddSingleton<ICodeFixJobLauncher, KubernetesCodeFixJobLauncher>();
            services.TryAddSingleton<IWorkloadImageReader, KubernetesWorkloadImageReader>();
        }
        else
        {
            services.TryAddSingleton<ICodeFixJobLauncher, RefusingCodeFixJobLauncher>();
            services.TryAddSingleton<IWorkloadImageReader, NullWorkloadImageReader>();
        }

        services.AddScoped<CodeFixNotifier>();
        services.AddScoped<CodeFixCoordinator>();
        services.AddScoped<CodeFixQueries>();
        services.AddHostedService<CodeFixJobWatcher>();

        return services;
    }

    private static CodeFixMode ModeOf(CodeFixOptions o) =>
        CodeFixModeResolver.Parse("env", o.Mode).Ceiling ?? CodeFixMode.Off;

    public static bool IsSafeNamespace(string? ns, PolicyOptions policy)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return false;

        if (ns is "default" || ns.StartsWith("kube-", StringComparison.Ordinal))
            return false;

        return !policy.AllowedNamespaces.Contains(ns) && !policy.ProtectedNamespaces.Contains(ns);
    }
}
