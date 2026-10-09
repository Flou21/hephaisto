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
/// <item><b>No git or NuGet token beside the model</b> (backlog #116). The pod is one image started
/// in up to three containers, and the kernel - not a guard over shell commands - keeps what one
/// holds from the others: there is no shared process namespace, so no container can read
/// another's <c>/proc</c>.</item>
/// </list>
/// <para>The containers, in the order they run:</para>
/// <list type="bullet">
/// <item><c>prepare</c>, an init container: the clones, the open-PR and remote-branch checks, the
/// pre-restore. Holds <c>GITHUB_TOKEN</c> and <c>NUGET_GITHUB_TOKEN</c>, and no model credential.
/// It has ended before the model exists.</item>
/// <item><c>coder</c>: the agent, and everything that executes what the agent wrote - the build and
/// the tests. Holds the model credential and nothing else. For a plan it is the pod's one regular
/// container and prints the result; to implement it is the SECOND init container, so that it too
/// has ended - every process of it - before the next one starts.</item>
/// <item><c>publish</c>, implement only, the one regular container: the push and the pull request, and
/// the result. Holds <c>GITHUB_TOKEN</c> alone, has a <c>/tmp</c> of its own, and reads what
/// <c>prepare</c> decided from a volume <c>coder</c> never mounted.</item>
/// </list>
/// <para>
/// An init container that exits non-zero fails the pod, and with <c>backoffLimit: 0</c> the Job:
/// no result is printed, which is the "Job failed without a result" Hephaisto already knew from a
/// runner that was OOM-killed. A role that fails in a way it can describe writes its result for the
/// next one and exits zero instead (coder/src/handoff.ts).
/// </para>
/// </remarks>
public static class CodeFixJobSpec
{
    public const int CoderUid = 64198;

    /// <summary>The container the model runs in. It is never handed a git or NuGet key.</summary>
    public const string ContainerName = "coder";

    public const string PrepareContainerName = "prepare";

    public const string PublishContainerName = "publish";

    /// <summary>
    /// On the Job: which container's log carries the framed result. Written by this file, read by the
    /// launcher - so a Job an older Hephaisto started, which has no such annotation and one container
    /// named <c>coder</c>, is still read correctly after an upgrade.
    /// </summary>
    public const string ResultContainerAnnotation = "hephaisto.dev/result-container";

    public const string AppLabel = "hephaisto-coder";

    public const string AttemptLabel = "hephaisto.dev/attempt";

    public const string IncidentLabel = "hephaisto.dev/incident";

    /// <summary>On the Job of a work item's attempt, in place of <see cref="IncidentLabel"/>: the work item's id.</summary>
    public const string WorkItemLabel = "hephaisto.dev/work-item";

    public const string PhaseLabel = "hephaisto.dev/phase";

    /// <summary>The model's credential, either form. Only <c>coder</c> is handed these.</summary>
    public static readonly IReadOnlyList<string> ModelSecretKeys = ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"];

    /// <summary>What <c>prepare</c> clones and restores with.</summary>
    public static readonly IReadOnlyList<string> PrepareSecretKeys = ["GITHUB_TOKEN", "NUGET_GITHUB_TOKEN"];

    /// <summary>What <c>publish</c> pushes and opens the PR with. No feed is asked there.</summary>
    public static readonly IReadOnlyList<string> PublishSecretKeys = ["GITHUB_TOKEN"];

    /// <summary>Every key a coder Job reads from the operator's Secret, across its containers. Each optional.</summary>
    public static readonly IReadOnlyList<string> SecretKeys = [.. ModelSecretKeys, .. PrepareSecretKeys];

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

        if (!string.IsNullOrWhiteSpace(o.Gh))
            env.Add(new V1EnvVar { Name = "CODEFIX_GH", Value = o.Gh });

        if (!string.IsNullOrWhiteSpace(o.ConsoleBaseUrl))
            env.Add(new V1EnvVar { Name = "CODEFIX_CONSOLE_URL", Value = o.ConsoleBaseUrl.TrimEnd('/') });

        // Every container talks to the network through the same pod IP and the same NetworkPolicy,
        // so every container needs the proxy: prepare and publish for GitHub and the feed, coder
        // for the model's API and whatever a build downloads.
        env.AddRange(ProxyEnv(o.EgressProxyUrl, noProxy: []));

        var coder = new ContainerShape(ContainerName, "coder", CoderKeysFor(o.Sdk), CoderEnv(o.Model));
        var prepare = new ContainerShape(PrepareContainerName, "prepare", PrepareSecretKeys, []);

        // Plan: prepare, then coder prints. Implement: prepare, coder, and publish prints - coder as
        // an init container, so publish starts only once nothing of coder is left running.
        IReadOnlyList<ContainerShape> containers = phase == CodeFixPhase.Plan
            ? [prepare, coder]
            : [prepare, coder, new ContainerShape(PublishContainerName, "publish", PublishSecretKeys, [])];

        return Hardened(
            new JobShape(
                JobName(attempt.Id, phase),
                ConfigMapName(attempt.Id, phase),
                Labels(attempt, phase),
                env,
                containers,
                phase == CodeFixPhase.Plan ? o.PlanDeadline : o.ImplementDeadline,
                o.WorkspaceSize,
                o.NugetCacheClaim),
            o);
    }

    /// <summary>
    /// The Secret keys the model's container is handed: its credential, and for a scripted
    /// (<c>fake</c>) run nothing at all. A scripted coder calls no model, the runner refuses fake
    /// mode beside a model credential, and a $0 run that could be flipped into a paid one by a
    /// Secret it happens to share is not $0.
    /// </summary>
    internal static IReadOnlyList<string> CoderKeysFor(string? sdk) =>
        string.Equals(sdk, "fake", StringComparison.Ordinal) ? [] : ModelSecretKeys;

    /// <summary>What only the container that runs the model needs to know.</summary>
    internal static IReadOnlyList<V1EnvVar> CoderEnv(string? model) =>
        string.IsNullOrWhiteSpace(model) ? [] : [new V1EnvVar { Name = "CODEFIX_MODEL", Value = model.Trim() }];

    /// <summary>
    /// One container of the pod: its name, the role the image is started in
    /// (<c>CODEFIX_ROLE</c>), the Secret keys it is handed - and it is handed no others - and the
    /// environment that is its alone.
    /// </summary>
    internal sealed record ContainerShape(string Name, string Role, IReadOnlyList<string> SecretKeys, IReadOnlyList<V1EnvVar> Env);

    /// <summary>What differs between the Jobs Hephaisto starts; everything else is <see cref="Hardened"/>.</summary>
    /// <param name="Env">What every container gets. Never a credential: those are per container.</param>
    /// <param name="Containers">
    /// In the order they run. The last is the pod's one regular container, whose log carries the
    /// result; every one before it is an init container and has ended before the next starts.
    /// </param>
    internal sealed record JobShape(
        string Name,
        string ConfigMapName,
        IDictionary<string, string> Labels,
        IList<V1EnvVar> Env,
        IReadOnlyList<ContainerShape> Containers,
        TimeSpan Deadline,
        string WorkspaceSize,
        string? NugetCacheClaim);

    /// <summary>
    /// The pod every Job Hephaisto starts runs in - a coder's and, since v0.12.0, an investigator's.
    /// One place, so the properties in this file's remarks cannot hold for one kind and not the other,
    /// nor for one container of a pod and not the next: every container is built by
    /// <see cref="Container"/>, with the same security context and the same resources.
    /// </summary>
    /// <remarks>
    /// <para>The volumes, and who mounts which:</para>
    /// <list type="bullet">
    /// <item><c>work</c> at <c>/work</c>: every container. It is where the model works, so nothing in
    /// it is believed by <c>publish</c> and nothing secret is ever written to it.</item>
    /// <item><c>request</c> at <c>/work/in</c>, read-only: every container.</item>
    /// <item>a <c>/tmp</c> each. <c>publish</c> builds the repository it pushes from in its own,
    /// which is therefore as large as the workspace may be.</item>
    /// <item><c>sealed</c> at <c>/sealed</c>, only in a pod with a <c>publish</c>: writable in
    /// <c>prepare</c>, read-only in <c>publish</c>, and NOT mounted in <c>coder</c> - what prepare
    /// decided (the base commit, the protected paths, the PR's shape) reaches the container that
    /// pushes without passing through anything the model could write.</item>
    /// <item>the NuGet cache, when configured: writable in <c>prepare</c>, whose restore of the
    /// untouched default branch fills it; READ-ONLY in <c>coder</c>, because it outlives the Job and
    /// the next attempt's <c>prepare</c> restores from it beside both tokens; absent in
    /// <c>publish</c>.</item>
    /// </list>
    /// <para>
    /// <c>shareProcessNamespace</c> is never set. That absence is the fix for #116, and a test pins it.
    /// </para>
    /// </remarks>
    internal static V1Job Hardened(JobShape shape, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(o);

        if (shape.Containers.Count == 0)
            throw new ArgumentException("a Job needs at least one container", nameof(shape));

        var main = shape.Containers[^1];
        var publishes = shape.Containers.Any(c => c.Name == PublishContainerName);
        var cache = !string.IsNullOrWhiteSpace(shape.NugetCacheClaim);

        var volumes = new List<V1Volume>
        {
            new() { Name = "work", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity(shape.WorkspaceSize) } },
            new()
            {
                Name = "request",
                ConfigMap = new V1ConfigMapVolumeSource { Name = shape.ConfigMapName, DefaultMode = 0x124 },
            },
        };

        // publish clones the default branch (without file contents) into its /tmp to check the
        // branch against it, so its limit is the workspace's; the others keep the 1Gi they had.
        volumes.AddRange(shape.Containers.Select(c => new V1Volume
        {
            Name = TmpVolume(c.Name),
            EmptyDir = new V1EmptyDirVolumeSource
            {
                SizeLimit = new ResourceQuantity(c.Name == PublishContainerName ? shape.WorkspaceSize : "1Gi"),
            },
        }));

        if (publishes)
        {
            volumes.Add(new V1Volume
            {
                Name = "sealed",
                EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("16Mi") },
            });
        }

        if (cache)
        {
            volumes.Add(new V1Volume
            {
                Name = "nuget",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = shape.NugetCacheClaim },
            });
        }

        List<V1VolumeMount> MountsOf(ContainerShape c)
        {
            var mounts = new List<V1VolumeMount>
            {
                new() { Name = "work", MountPath = "/work" },
                new() { Name = TmpVolume(c.Name), MountPath = "/tmp" },
                new() { Name = "request", MountPath = "/work/in", ReadOnlyProperty = true },
            };

            if (publishes && c.Name == PrepareContainerName)
                mounts.Add(new V1VolumeMount { Name = "sealed", MountPath = "/sealed" });

            if (publishes && c.Name == PublishContainerName)
                mounts.Add(new V1VolumeMount { Name = "sealed", MountPath = "/sealed", ReadOnlyProperty = true });

            if (cache && c.Name == PrepareContainerName)
                mounts.Add(new V1VolumeMount { Name = "nuget", MountPath = "/work/nuget/packages" });

            if (cache && c.Name == ContainerName)
                mounts.Add(new V1VolumeMount { Name = "nuget", MountPath = "/work/nuget/packages", ReadOnlyProperty = true });

            return mounts;
        }

        V1Container Container(ContainerShape c) => new()
        {
            Name = c.Name,
            Image = o.Image,
            ImagePullPolicy = o.ImagePullPolicy,
            Env =
            [
                new V1EnvVar { Name = "CODEFIX_ROLE", Value = c.Role },
                .. shape.Env,
                .. c.Env,
                .. SecretEnv(c.SecretKeys, o.SecretName),
            ],
            VolumeMounts = MountsOf(c),
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
        };

        var init = shape.Containers.Take(shape.Containers.Count - 1).Select(Container).ToList();

        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta
            {
                Name = shape.Name,
                NamespaceProperty = o.Namespace,
                Labels = shape.Labels,
                Annotations = new Dictionary<string, string> { [ResultContainerAnnotation] = main.Name },
            },
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
                        InitContainers = init.Count == 0 ? null : init,
                        Containers = [Container(main)],
                        Volumes = volumes,
                    },
                },
            },
        };
    }

    /// <summary><c>tmp</c> for the container the model runs in, as it always was; <c>tmp-&lt;name&gt;</c> for the others.</summary>
    private static string TmpVolume(string container) => container == ContainerName ? "tmp" : $"tmp-{container}";

    /// <summary>
    /// The container whose log carries a Job's framed result: the one its annotation names, when it
    /// names one of the two that can print; otherwise <c>coder</c>, which is every Job from before
    /// the annotation existed.
    /// </summary>
    public static string ResultContainer(V1Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.Metadata?.Annotations?.TryGetValue(ResultContainerAnnotation, out var name) == true
               && name is ContainerName or PublishContainerName
            ? name
            : ContainerName;
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

    private static Dictionary<string, string> Labels(CodeFixAttempt attempt, CodeFixPhase phase)
    {
        // In this order, with exactly one of the two subject labels: an incident's Job is
        // labelled as it always was.
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = AppLabel,
            ["app.kubernetes.io/managed-by"] = "hephaisto",
            [AttemptLabel] = attempt.Id.ToString(),
        };

        if (attempt.IncidentId is { } incident)
            labels[IncidentLabel] = incident.ToString();
        else if (attempt.WorkItemId is { } workItem)
            labels[WorkItemLabel] = workItem.ToString();

        labels[PhaseLabel] = phase == CodeFixPhase.Plan ? "plan" : "implement";

        return labels;
    }
}
