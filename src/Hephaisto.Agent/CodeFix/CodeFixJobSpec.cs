using k8s.Models;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.CodeFix;

/// <summary>
/// Renders the coder Job and its request ConfigMap. Pure, so the golden tests pin every field that
/// makes the pod safe to hand a model with a shell.
/// </summary>
/// <remarks>
/// <para>What the pod does NOT have is the point of this file:</para>
/// <list type="bullet">
/// <item><b>No cluster identity.</b> <c>automountServiceAccountToken: false</c> on a ServiceAccount
/// bound to nothing. The coder cannot read a Secret, a log, or anything else in the cluster.</item>
/// <item><b>No Hephaisto credential and no inbound surface.</b> It answers by printing a framed block
/// to its own log, which Hephaisto reads with the <c>get pods/log</c> it already holds.</item>
/// <item><b>No root, no writable image, no capabilities, no retries.</b> <c>backoffLimit: 0</c>: a
/// coder that failed once is reported, not re-run - a second run is a second bill.</item>
/// <item><b>No secret values in the spec.</b> Credentials arrive by <c>secretKeyRef</c> from a Secret
/// Hephaisto can name but never read.</item>
/// </list>
/// </remarks>
public static class CodeFixJobSpec
{
    public const int CoderUid = 64198;

    public const string ContainerName = "coder";

    public const string AppLabel = "hephaisto-coder";

    public const string AttemptLabel = "hephaisto.dev/attempt";

    public const string IncidentLabel = "hephaisto.dev/incident";

    public const string PhaseLabel = "hephaisto.dev/phase";

    /// <summary>Keys the coder may read from the operator's Secret. Each optional.</summary>
    public static readonly IReadOnlyList<string> SecretKeys =
        ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "GITHUB_TOKEN", "NUGET_GITHUB_TOKEN"];

    /// <summary><c>codefix-&lt;id12&gt;-plan</c> / <c>-impl</c>. Well under the 63-character label limit.</summary>
    public static string JobName(Guid attemptId, CodeFixPhase phase) =>
        $"codefix-{Id12(attemptId)}-{(phase == CodeFixPhase.Plan ? "plan" : "impl")}";

    public static string ConfigMapName(Guid attemptId, CodeFixPhase phase) => JobName(attemptId, phase) + "-req";

    /// <summary><c>hephaisto/codefix-&lt;id12&gt;</c>: the one branch the runner may push.</summary>
    public static string BranchName(Guid attemptId) => $"hephaisto/codefix-{Id12(attemptId)}";

    /// <summary>
    /// The last twelve hex digits. A version-7 guid's first twelve are its millisecond timestamp, so
    /// two attempts in the same millisecond would collide there; the tail is random.
    /// </summary>
    public static string Id12(Guid id) => id.ToString("N")[^12..];

    public static V1Job Job(CodeFixAttempt attempt, CodeFixPhase phase, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(o);

        var name = JobName(attempt.Id, phase);
        var labels = Labels(attempt, phase);
        var deadline = phase == CodeFixPhase.Plan ? o.PlanDeadline : o.ImplementDeadline;

        var env = new List<V1EnvVar>
        {
            new V1EnvVar { Name = "CODEFIX_REQUEST", Value = "/work/in/request.json" },
            new V1EnvVar { Name = "CODEFIX_SDK", Value = string.IsNullOrWhiteSpace(o.Sdk) ? "real" : o.Sdk },
        };

        if (!string.IsNullOrWhiteSpace(o.Model))
            env.Add(new V1EnvVar { Name = "CODEFIX_MODEL", Value = o.Model.Trim() });

        if (!string.IsNullOrWhiteSpace(o.Gh))
            env.Add(new V1EnvVar { Name = "CODEFIX_GH", Value = o.Gh });

        if (!string.IsNullOrWhiteSpace(o.ConsoleBaseUrl))
            env.Add(new V1EnvVar { Name = "CODEFIX_CONSOLE_URL", Value = o.ConsoleBaseUrl.TrimEnd('/') });

        if (!string.IsNullOrWhiteSpace(o.EgressProxyUrl))
        {
            // Both cases. git and curl read only the lowercase http_proxy for an http:// URL, while
            // node and .NET read the uppercase ones; setting one spelling leaves a tool going direct.
            foreach (var (proxyVar, proxyValue) in new[]
                     {
                         ("HTTPS_PROXY", o.EgressProxyUrl), ("HTTP_PROXY", o.EgressProxyUrl), ("NO_PROXY", "localhost,127.0.0.1"),
                         ("https_proxy", o.EgressProxyUrl), ("http_proxy", o.EgressProxyUrl), ("no_proxy", "localhost,127.0.0.1"),
                     })
            {
                env.Add(new V1EnvVar { Name = proxyVar, Value = proxyValue });
            }
        }

        foreach (var key in SecretKeys)
        {
            env.Add(new V1EnvVar
            {
                Name = key,
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = o.SecretName, Key = key, Optional = true },
                },
            });
        }

        var mounts = new List<V1VolumeMount>
        {
            new() { Name = "work", MountPath = "/work" },
            new() { Name = "tmp", MountPath = "/tmp" },
            new() { Name = "request", MountPath = "/work/in", ReadOnlyProperty = true },
        };

        var volumes = new List<V1Volume>
        {
            new() { Name = "work", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity(o.WorkspaceSize) } },
            new() { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("1Gi") } },
            new()
            {
                Name = "request",
                ConfigMap = new V1ConfigMapVolumeSource { Name = ConfigMapName(attempt.Id, phase), DefaultMode = 0x124 },
            },
        };

        if (!string.IsNullOrWhiteSpace(o.NugetCacheClaim))
        {
            mounts.Add(new V1VolumeMount { Name = "nuget", MountPath = "/work/nuget/packages" });
            volumes.Add(new V1Volume
            {
                Name = "nuget",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = o.NugetCacheClaim },
            });
        }

        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = o.Namespace, Labels = labels },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                ActiveDeadlineSeconds = (long)deadline.TotalSeconds,
                TtlSecondsAfterFinished = (int)o.JobTtl.TotalSeconds,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        ServiceAccountName = o.ServiceAccountName,
                        AutomountServiceAccountToken = false,
                        EnableServiceLinks = false,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            RunAsUser = CoderUid,
                            RunAsGroup = CoderUid,
                            FsGroup = CoderUid,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
                        },
                        Containers =
                        [
                            new V1Container
                            {
                                Name = ContainerName,
                                Image = o.Image,
                                ImagePullPolicy = o.ImagePullPolicy,
                                Env = env,
                                VolumeMounts = mounts,
                                SecurityContext = new V1SecurityContext
                                {
                                    AllowPrivilegeEscalation = false,
                                    ReadOnlyRootFilesystem = true,
                                    RunAsNonRoot = true,
                                    Capabilities = new V1Capabilities { Drop = ["ALL"] },
                                },
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new(o.Resources.CpuRequest),
                                        ["memory"] = new(o.Resources.MemoryRequest),
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new(o.Resources.CpuLimit),
                                        ["memory"] = new(o.Resources.MemoryLimit),
                                    },
                                },
                            },
                        ],
                        Volumes = volumes,
                    },
                },
            },
        };
    }

    /// <summary>
    /// The request, as a ConfigMap owned by its Job. Created after the Job so it can carry the Job's
    /// uid as owner: the pod waits a moment for the volume, and garbage collection removes the
    /// request with the Job instead of leaving evidence excerpts lying in a namespace forever.
    /// </summary>
    public static V1ConfigMap RequestConfigMap(CodeFixAttempt attempt, CodeFixPhase phase, CodeFixOptions o, string requestJson, V1Job owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = new V1ObjectMeta
            {
                Name = ConfigMapName(attempt.Id, phase),
                NamespaceProperty = o.Namespace,
                Labels = Labels(attempt, phase),
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "batch/v1",
                        Kind = "Job",
                        Name = owner.Metadata.Name,
                        Uid = owner.Metadata.Uid,
                        BlockOwnerDeletion = false,
                        Controller = false,
                    },
                ],
            },
            Data = new Dictionary<string, string> { ["request.json"] = requestJson },
        };
    }

    private static Dictionary<string, string> Labels(CodeFixAttempt attempt, CodeFixPhase phase) => new()
    {
        ["app.kubernetes.io/name"] = AppLabel,
        ["app.kubernetes.io/managed-by"] = "hephaisto",
        [AttemptLabel] = attempt.Id.ToString(),
        [IncidentLabel] = attempt.IncidentId.ToString(),
        [PhaseLabel] = phase == CodeFixPhase.Plan ? "plan" : "implement",
    };
}
