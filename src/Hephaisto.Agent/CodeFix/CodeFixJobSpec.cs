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

        env.AddRange(ProxyEnv(o.EgressProxyUrl, noProxy: []));
        // A scripted (fake) coder calls no model, and the runner refuses fake mode beside a model
        // credential - so it is handed none, and a $0 run cannot be turned into a paid one by a
        // Secret it shares with a real coder. It still gets the git tokens its push may need.
        env.AddRange(SecretEnv(
            string.Equals(o.Sdk, "fake", StringComparison.Ordinal) ? SecretKeys.Skip(2) : SecretKeys,
            o.SecretName));

        return Hardened(
            new JobShape(
                JobName(attempt.Id, phase),
                ConfigMapName(attempt.Id, phase),
                Labels(attempt, phase),
                env,
                phase == CodeFixPhase.Plan ? o.PlanDeadline : o.ImplementDeadline,
                o.WorkspaceSize,
                o.NugetCacheClaim),
            o);
    }

    /// <summary>What differs between the Jobs Hephaisto starts; everything else is <see cref="Hardened"/>.</summary>
    internal sealed record JobShape(
        string Name,
        string ConfigMapName,
        IDictionary<string, string> Labels,
        IList<V1EnvVar> Env,
        TimeSpan Deadline,
        string WorkspaceSize,
        string? NugetCacheClaim);

    /// <summary>
    /// The pod every Job Hephaisto starts runs in - a coder's and, since v0.12.0, an investigator's.
    /// One place, so the properties in this file's remarks cannot hold for one kind and not the other.
    /// </summary>
    internal static V1Job Hardened(JobShape shape, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(o);

        var mounts = new List<V1VolumeMount>
        {
            new() { Name = "work", MountPath = "/work" },
            new() { Name = "tmp", MountPath = "/tmp" },
            new() { Name = "request", MountPath = "/work/in", ReadOnlyProperty = true },
        };

        var volumes = new List<V1Volume>
        {
            new() { Name = "work", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity(shape.WorkspaceSize) } },
            new() { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("1Gi") } },
            new()
            {
                Name = "request",
                ConfigMap = new V1ConfigMapVolumeSource { Name = shape.ConfigMapName, DefaultMode = 0x124 },
            },
        };

        if (!string.IsNullOrWhiteSpace(shape.NugetCacheClaim))
        {
            mounts.Add(new V1VolumeMount { Name = "nuget", MountPath = "/work/nuget/packages" });
            volumes.Add(new V1Volume
            {
                Name = "nuget",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = shape.NugetCacheClaim },
            });
        }

        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta { Name = shape.Name, NamespaceProperty = o.Namespace, Labels = shape.Labels },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                ActiveDeadlineSeconds = (long)shape.Deadline.TotalSeconds,
                TtlSecondsAfterFinished = (int)o.JobTtl.TotalSeconds,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = shape.Labels },
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
                                Env = shape.Env,
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
    /// The proxy variables, in both cases: git and curl read only the lowercase <c>http_proxy</c> for
    /// an http:// URL, while node and .NET read the uppercase ones; setting one spelling leaves a
    /// tool going direct. <paramref name="noProxy"/> adds hosts reached without the proxy.
    /// </summary>
    internal static IEnumerable<V1EnvVar> ProxyEnv(string? proxyUrl, IReadOnlyList<string> noProxy)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            yield break;

        var bypass = string.Join(',', new[] { "localhost", "127.0.0.1" }.Concat(noProxy));

        foreach (var (name, value) in new[]
                 {
                     ("HTTPS_PROXY", proxyUrl), ("HTTP_PROXY", proxyUrl), ("NO_PROXY", bypass),
                     ("https_proxy", proxyUrl), ("http_proxy", proxyUrl), ("no_proxy", bypass),
                 })
        {
            yield return new V1EnvVar { Name = name, Value = value };
        }
    }

    /// <summary>Credentials by reference, each optional, from a Secret Hephaisto can name but never read.</summary>
    internal static IEnumerable<V1EnvVar> SecretEnv(IEnumerable<string> keys, string secretName) =>
        keys.Select(key => new V1EnvVar
        {
            Name = key,
            ValueFrom = new V1EnvVarSource
            {
                SecretKeyRef = new V1SecretKeySelector { Name = secretName, Key = key, Optional = true },
            },
        });

    /// <summary>
    /// The request, as a ConfigMap owned by its Job. Created after the Job so it can carry the Job's
    /// uid as owner: the pod waits a moment for the volume, and garbage collection removes the
    /// request with the Job instead of leaving evidence excerpts lying in a namespace forever.
    /// </summary>
    public static V1ConfigMap RequestConfigMap(CodeFixAttempt attempt, CodeFixPhase phase, CodeFixOptions o, string requestJson, V1Job owner)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(o);

        return RequestConfigMap(ConfigMapName(attempt.Id, phase), Labels(attempt, phase), o.Namespace, requestJson, owner);
    }

    /// <summary>The request ConfigMap of any Job Hephaisto starts, owned by that Job.</summary>
    internal static V1ConfigMap RequestConfigMap(
        string name, IDictionary<string, string> labels, string ns, string requestJson, V1Job owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = labels,
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
