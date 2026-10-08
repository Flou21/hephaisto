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
/// deadline, the model, and the credentials. Never the NuGet token, never a Hephaisto one: its only
/// credential for Hephaisto is the per-run bearer token in its request.
/// </para>
/// <para>
/// <b>Two containers since #116.</b> <c>prepare</c>, an init container, holds <c>GITHUB_TOKEN</c>
/// and makes the clones - dev-context, and the workload's source when source access is on - before
/// the model exists. <c>coder</c> holds the model's credential and nothing else, reaches the
/// investigator endpoint, and prints the result, exactly as the single container did. There is a
/// <c>prepare</c> in every investigator Job, source access or not: the context repository is
/// private in production, so the clone that needs the token happens in every run. (Found on the
/// first production install, where every Job for an unmapped workload failed at that clone.)
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

    /// <summary>What <c>prepare</c> clones with. Not the NuGet token: an investigator never restores.</summary>
    public static readonly IReadOnlyList<string> PrepareSecretKeys = ["GITHUB_TOKEN"];

    /// <summary><c>investigate-&lt;id12&gt;</c>.</summary>
    public static string JobName(Guid attemptId) => $"investigate-{CodeFixJobSpec.Id12(attemptId)}";

    public static string ConfigMapName(Guid attemptId) => JobName(attemptId) + "-req";

    public static V1Job Job(
        Guid attemptId, Guid incidentId, Guid investigationId,
        InvestigationJobOptions job, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(o);

        var env = new List<V1EnvVar>
        {
            new() { Name = "CODEFIX_REQUEST", Value = "/work/in/request.json" },
            new() { Name = "CODEFIX_SDK", Value = string.IsNullOrWhiteSpace(job.Sdk) ? "real" : job.Sdk },
        };

        var endpointHost = Uri.TryCreate(job.EndpointUrl, UriKind.Absolute, out var endpoint) ? endpoint.Host : null;

        env.AddRange(CodeFixJobSpec.ProxyEnv(o.EgressProxyUrl, endpointHost is null ? [] : [endpointHost]));

        return CodeFixJobSpec.Hardened(
            new CodeFixJobSpec.JobShape(
                JobName(attemptId),
                ConfigMapName(attemptId),
                Labels(attemptId, incidentId, investigationId),
                env,
                [
                    new CodeFixJobSpec.ContainerShape(CodeFixJobSpec.PrepareContainerName, "prepare", PrepareSecretKeys, []),
                    new CodeFixJobSpec.ContainerShape(
                        CodeFixJobSpec.ContainerName, "coder", CodeFixJobSpec.CoderKeysFor(job.Sdk), CodeFixJobSpec.CoderEnv(job.Model)),
                ],
                job.Deadline,
                o.WorkspaceSize,
                NugetCacheClaim: null),
            o);
    }

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
