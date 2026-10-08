using Hephaisto.Core.CodeFix;

namespace Hephaisto.Agent.CodeFix;

/// <summary>The code-fix stage. Bound from <c>CodeFix:*</c>; the chart emits <c>CodeFix__*</c>.</summary>
/// <remarks>
/// Every default here is inert. <see cref="Mode"/> unset is Off; <see cref="Repositories"/> empty
/// maps nothing; so an install that never heard of this feature spends nothing on it.
/// </remarks>
public sealed class CodeFixOptions
{
    public const string SectionName = "CodeFix";

    /// <summary>
    /// The environment arm, kept as the raw string so the strict parser in
    /// <see cref="CodeFixModeResolver"/> sees exactly what the operator typed. A bound enum would
    /// accept <c>2</c> as Pr.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>Where coder Jobs run. Never an actionable, protected, release or observability namespace.</summary>
    public string Namespace { get; set; } = "hephaisto-coder";

    public string Image { get; set; } = string.Empty;

    public string ImagePullPolicy { get; set; } = "IfNotPresent";

    /// <summary>Bound to nothing, token never mounted. It exists so the pod does not run as <c>default</c>.</summary>
    public string ServiceAccountName { get; set; } = "hephaisto-coder";

    /// <summary>
    /// The Secret the coder reads its credentials from, by reference. Hephaisto never reads it and
    /// holds no RBAC that could: keys <c>CLAUDE_CODE_OAUTH_TOKEN</c>, <c>GITHUB_TOKEN</c>,
    /// <c>NUGET_GITHUB_TOKEN</c>, each optional.
    /// </summary>
    public string SecretName { get; set; } = "hephaisto-codefix";

    public string ContextRepositoryUrl { get; set; } = string.Empty;

    public string ContextRepositoryRef { get; set; } = "main";

    /// <summary>The operator's authorization list: workload to repository. Empty maps nothing.</summary>
    public List<RepositoryBinding> Repositories { get; set; } = [];

    public List<string> AllowedRepositoryHosts { get; set; } = [];

    /// <summary>Primary-finding categories that may start a coder. <c>image</c> is deliberately never a default.</summary>
    public List<string> EligibleCategories { get; set; } = [];

    public double ConfidenceFloor { get; set; } = 0.7;

    public decimal MaxCostUsdPerPlan { get; set; } = 5m;

    public decimal MaxCostUsdPerImplement { get; set; } = 15m;

    public decimal MaxCostUsdPerDay { get; set; } = 50m;

    public int MaxAttemptsPerRepositoryPerDay { get; set; } = 3;

    public int MaxConcurrentJobs { get; set; } = 1;

    public TimeSpan PlanDeadline { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ImplementDeadline { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>A plan nobody decided on within this expires. Stale advice is not advice.</summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Finished Jobs (and their logs) are garbage-collected after this.</summary>
    public TimeSpan JobTtl { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public string WorkspaceSize { get; set; } = "8Gi";

    public CodeFixResources Resources { get; set; } = new();

    /// <summary>A PR only when the driver's own build and tests were green.</summary>
    public bool RequireGreenBuild { get; set; } = true;

    /// <summary>
    /// Pr mode needs <c>Auth:Enabled</c>: a repository write on the strength of a click needs an
    /// authenticated human, not an attributed string. This is the escape hatch for a throwaway e2e
    /// cluster, and startup logs a warning whenever it is on.
    /// </summary>
    public bool AllowUnauthenticatedApproval { get; set; }

    /// <summary><c>http://&lt;release&gt;-coder-egress.&lt;ns&gt;.svc:3128</c> when the chart's egress proxy is on; empty otherwise.</summary>
    public string EgressProxyUrl { get; set; } = string.Empty;

    /// <summary>Passed through as <c>CODEFIX_SDK</c>. <c>fake</c> runs scripted, $0 plumbing - dev and CI only.</summary>
    public string Sdk { get; set; } = "real";

    /// <summary>
    /// The Claude model the coder runs, passed through as <c>CODEFIX_MODEL</c> - an id such as
    /// <c>claude-haiku-4-5-20251001</c> or a CLI alias. Empty lets the Claude Code CLI choose its
    /// default for the account, which on a subscription is usually the most capable (and most
    /// expensive) model; pin it to control cost.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Passed through as <c>CODEFIX_GH</c>. <c>shim</c> answers gh locally - dev and CI only.</summary>
    public string Gh { get; set; } = string.Empty;

    /// <summary>A PVC name for the NuGet package cache, or empty for none.</summary>
    public string NugetCacheClaim { get; set; } = string.Empty;

    /// <summary>Base URL of this console, for the incident link in the PR body.</summary>
    public string ConsoleBaseUrl { get; set; } = string.Empty;

    public RepositoryBinding? BindingFor(string workloadKey) =>
        Repositories.FirstOrDefault(r => string.Equals(r.Workload.Trim(), workloadKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where the code of <c>owner/repo</c> is cloned from, for a work item (v0.14.0): the FIRST
    /// entry of <see cref="Repositories"/> whose URL names that repository - its path ends with
    /// <c>/owner/repo</c> or <c>/owner/repo.git</c>, whatever the case and whatever the host -
    /// with its default branch and path. Null when no entry does; the caller then asks GitHub.
    /// </summary>
    /// <remarks>
    /// The first, because one repository is routinely mapped by several workloads, and on a dev
    /// cluster by several fixture branches. An issue names a repository and nothing that runs,
    /// so there is nothing to choose between them by; an operator who wants another branch for
    /// issues puts that entry first.
    /// </remarks>
    public RepositoryBinding? BindingForRepository(string ownerRepo)
    {
        var wanted = "/" + ownerRepo.Trim().Trim('/');

        return wanted.Length <= 1
            ? null
            : Repositories.FirstOrDefault(r =>
                CodeFixResultParser.RepositoryPath(r.Url) is { } path && path.EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What is assumed of a listed repository that <see cref="Repositories"/> does not name: it
    /// is on github.com under its own name. <paramref name="defaultBranch"/> is what GitHub's API
    /// said, or nothing - then <c>main</c>.
    /// </summary>
    public static RepositoryBinding GitHubRepository(string ownerRepo, string? defaultBranch) => new()
    {
        Url = "https://github.com/" + ownerRepo.Trim().Trim('/'),
        DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim(),
    };

    /// <summary>
    /// The subdirectory an attempt works in: its workload's entry for an incident, and for a work
    /// item - which has no workload - the first entry with its repository URL.
    /// </summary>
    public string PathFor(string workload, string repositoryUrl) =>
        (string.IsNullOrEmpty(workload)
            ? Repositories.FirstOrDefault(r => string.Equals(r.Url.Trim(), repositoryUrl, StringComparison.OrdinalIgnoreCase))
            : BindingFor(workload))?.Path ?? string.Empty;

    public CodeFixEligibilityOptions ToEligibilityOptions() => new()
    {
        EligibleCategories = EligibleCategories,
        ConfidenceFloor = ConfidenceFloor,
        AllowedRepositoryHosts = AllowedRepositoryHosts,
        MaxAttemptsPerRepositoryPerDay = MaxAttemptsPerRepositoryPerDay,
        MaxConcurrentJobs = MaxConcurrentJobs,
        MaxCostUsdPerDay = MaxCostUsdPerDay,
    };
}

public sealed class CodeFixResources
{
    public string CpuRequest { get; set; } = "1";

    public string MemoryRequest { get; set; } = "2Gi";

    public string CpuLimit { get; set; } = "4";

    public string MemoryLimit { get; set; } = "6Gi";
}
