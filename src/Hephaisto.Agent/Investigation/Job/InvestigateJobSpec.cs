using k8s.Models;
using Hephaisto.Agent.CodeFix;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>
/// The investigator Job (v0.12.0 F5): the coder's hardened pod, told to investigate, labelled so
/// that exactly it - and no coder - may reach the investigator port.
/// </summary>
/// <remarks>
/// <para>
/// Everything that makes the pod safe is <see cref="CodeFixJobSpec.Hardened"/>, shared on purpose:
/// no ServiceAccount token, non-root, read-only root, no capabilities, no retries, credentials by
/// reference. What differs is small and listed here: the label the NetworkPolicies select on, the
/// deadline, the model, and the credentials - an investigator gets the model's token and, only with
/// source access on, a GitHub token to clone with. Never the NuGet token, never a Hephaisto one:
/// its only credential for Hephaisto is the per-run bearer token in its request.
/// </para>
/// <para>
/// The endpoint's host goes on <c>NO_PROXY</c>: the call to the agent is in-cluster and must not be
/// routed through the egress proxy, which has no business allowing it.
/// </para>
/// </remarks>
public static class InvestigateJobSpec
{
    public const string AppLabel = "hephaisto-investigator";

    public const string InvestigationLabel = "hephaisto.dev/investigation";

    public static readonly IReadOnlyList<string> SecretKeys = ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"];

    /// <summary>With source access: the same, plus a token to clone the workload's repository.</summary>
    public static readonly IReadOnlyList<string> SourceSecretKeys = [.. SecretKeys, "GITHUB_TOKEN"];

    /// <summary><c>investigate-&lt;id12&gt;</c>.</summary>
    public static string JobName(Guid attemptId) => $"investigate-{CodeFixJobSpec.Id12(attemptId)}";

    public static string ConfigMapName(Guid attemptId) => JobName(attemptId) + "-req";

    public static V1Job Job(
        Guid attemptId, Guid incidentId, Guid investigationId, bool withSource,
        InvestigationJobOptions job, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(o);

        var env = new List<V1EnvVar>
        {
            new() { Name = "CODEFIX_REQUEST", Value = "/work/in/request.json" },
            new() { Name = "CODEFIX_SDK", Value = string.IsNullOrWhiteSpace(job.Sdk) ? "real" : job.Sdk },
        };

        if (!string.IsNullOrWhiteSpace(job.Model))
            env.Add(new V1EnvVar { Name = "CODEFIX_MODEL", Value = job.Model.Trim() });

        var endpointHost = Uri.TryCreate(job.EndpointUrl, UriKind.Absolute, out var endpoint) ? endpoint.Host : null;

        env.AddRange(CodeFixJobSpec.ProxyEnv(o.EgressProxyUrl, endpointHost is null ? [] : [endpointHost]));
        env.AddRange(CodeFixJobSpec.SecretEnv(KeysFor(job.Sdk, withSource), o.SecretName));

        return CodeFixJobSpec.Hardened(
            new CodeFixJobSpec.JobShape(
                JobName(attemptId),
                ConfigMapName(attemptId),
                Labels(attemptId, incidentId, investigationId),
                env,
                job.Deadline,
                o.WorkspaceSize,
                NugetCacheClaim: null),
            o);
    }

    /// <summary>
    /// The Secret keys a Job is handed. A scripted (<c>fake</c>) investigator gets no model credential
    /// at all: it calls no model, the runner refuses to start fake while one is present, and a $0
    /// run that could be flipped into a paid one by a Secret it happens to share is not $0.
    /// </summary>
    public static IReadOnlyList<string> KeysFor(string? sdk, bool withSource) =>
        string.Equals(sdk, "fake", StringComparison.Ordinal)
            ? withSource ? ["GITHUB_TOKEN"] : []
            : withSource ? SourceSecretKeys : SecretKeys;

    public static V1ConfigMap RequestConfigMap(
        Guid attemptId, Guid incidentId, Guid investigationId, CodeFixOptions o, string requestJson, V1Job owner) =>
        CodeFixJobSpec.RequestConfigMap(
            ConfigMapName(attemptId), Labels(attemptId, incidentId, investigationId), o.Namespace, requestJson, owner);

    public static Dictionary<string, string> Labels(Guid attemptId, Guid incidentId, Guid investigationId) => new()
    {
        ["app.kubernetes.io/name"] = AppLabel,
        ["app.kubernetes.io/managed-by"] = "hephaisto",
        [CodeFixJobSpec.AttemptLabel] = attemptId.ToString(),
        [CodeFixJobSpec.IncidentLabel] = incidentId.ToString(),
        [InvestigationLabel] = investigationId.ToString(),
        [CodeFixJobSpec.PhaseLabel] = "investigate",
    };
}
