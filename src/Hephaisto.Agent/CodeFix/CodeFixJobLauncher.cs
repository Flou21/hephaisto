using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.CodeFix;

public enum CodeFixJobPhase
{
    /// <summary>The Job does not exist - never created, garbage-collected, or deleted by a human.</summary>
    Missing = 0,
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}

public sealed record CodeFixJobObservation(CodeFixJobPhase Phase, string? Detail);

/// <summary>Starts, watches and removes coder Jobs. The only code in the process that creates a Job.</summary>
public interface ICodeFixJobLauncher
{
    /// <summary>False when this process has no cluster client; callers record why nothing ran.</summary>
    bool IsAvailable { get; }

    /// <summary>Creates the Job and its request ConfigMap; returns the Job name. Idempotent on the name.</summary>
    Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct);

    Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct);

    /// <summary>The tail of the coder container's log, or null when there is no pod to read.</summary>
    Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct);

    Task DeleteAsync(string jobName, CancellationToken ct);
}

public sealed class CodeFixLaunchRefusedException(string message) : Exception(message);

/// <summary>Registered when <c>Kubernetes:Enabled=false</c>. Refuses loudly instead of pretending.</summary>
public sealed class RefusingCodeFixJobLauncher : ICodeFixJobLauncher
{
    public bool IsAvailable => false;

    public Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct) =>
        throw new CodeFixLaunchRefusedException("this process has no cluster client (Kubernetes:Enabled=false)");

    public Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct) =>
        Task.FromResult(new CodeFixJobObservation(CodeFixJobPhase.Missing, "no cluster client"));

    public Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task DeleteAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The Kubernetes implementation. Every call is in <see cref="CodeFixOptions.Namespace"/>, the one
/// namespace the chart grants <c>create jobs</c> in; <c>RbacSelfCheck</c> refuses to start if the
/// grant exists anywhere else.
/// </summary>
public sealed class KubernetesCodeFixJobLauncher(
    KubernetesApi api,
    IOptionsMonitor<CodeFixOptions> options,
    ILogger<KubernetesCodeFixJobLauncher> logger) : ICodeFixJobLauncher
{
    /// <summary>
    /// Enough tail to hold the result line and the handful of lines the runner writes after
    /// progress stops. The runner keeps stdout small, so the block is always in here.
    /// </summary>
    private const int TailLines = 64;

    private const int LimitBytes = 2 * 1024 * 1024;

    public bool IsAvailable => true;

    public async Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct)
    {
        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.Image))
            throw new CodeFixLaunchRefusedException("CodeFix:Image is not set");

        var spec = CodeFixJobSpec.Job(attempt, phase, o);
        V1Job job;

        try
        {
            job = await api.Batch.CreateNamespacedJobAsync(spec, o.Namespace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // A restart between the create and the save that records it. The name is derived from
            // the attempt id, so the existing Job is this attempt's own, not a stranger's.
            logger.LogInformation("Coder job {Job} already exists; adopting it.", spec.Metadata.Name);
            job = await api.Batch.ReadNamespacedJobAsync(spec.Metadata.Name, o.Namespace, cancellationToken: ct).ConfigureAwait(false);
        }

        var cm = CodeFixJobSpec.RequestConfigMap(attempt, phase, o, requestJson, job);

        try
        {
            await api.Core.CreateNamespacedConfigMapAsync(cm, o.Namespace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogInformation("Request ConfigMap {ConfigMap} already exists; keeping it.", cm.Metadata.Name);
        }

        return job.Metadata.Name;
    }

    public async Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct)
    {
        var o = options.CurrentValue;
        V1Job job;

        try
        {
            job = await api.Batch.ReadNamespacedJobAsync(jobName, o.Namespace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return new CodeFixJobObservation(CodeFixJobPhase.Missing, "the job no longer exists");
        }

        var conditions = job.Status?.Conditions ?? [];

        if (conditions.FirstOrDefault(c => c.Type is "Complete" or "SuccessCriteriaMet" && c.Status == "True") is not null)
            return new CodeFixJobObservation(CodeFixJobPhase.Succeeded, null);

        if (conditions.FirstOrDefault(c => c.Type is "Failed" or "FailureTarget" && c.Status == "True") is { } failed)
            return new CodeFixJobObservation(CodeFixJobPhase.Failed, $"{failed.Reason}: {failed.Message}".Trim(' ', ':'));

        return (job.Status?.Active ?? 0) > 0
            ? new CodeFixJobObservation(CodeFixJobPhase.Running, null)
            : new CodeFixJobObservation(CodeFixJobPhase.Pending, null);
    }

    public async Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct)
    {
        var o = options.CurrentValue;

        V1Job job;

        try
        {
            job = await api.Batch.ReadNamespacedJobAsync(jobName, o.Namespace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var pods = await api.Core.ListNamespacedPodAsync(
                o.Namespace, labelSelector: $"job-name={jobName}", cancellationToken: ct)
            .ConfigureAwait(false);

        // Only a pod the Job's controller created: a label is something anyone who can create a
        // pod in this namespace can copy, and the result is read from whichever pod is chosen
        // here. The owner reference with controller=true and the Job's uid is what the Job
        // controller sets and a copied label does not bring along.
        var pod = pods.Items
            .Where(p => p.Metadata.OwnerReferences?.Any(r => r.Controller == true && r.Kind == "Job" && r.Uid == job.Metadata.Uid) == true)
            .OrderByDescending(p => p.Metadata.CreationTimestamp ?? DateTime.MinValue)
            .FirstOrDefault();

        if (pod is null)
            return null;

        try
        {
            await using var stream = await api.Core.ReadNamespacedPodLogAsync(
                    pod.Metadata.Name,
                    o.Namespace,
                    container: CodeFixJobSpec.ContainerName,
                    tailLines: TailLines,
                    limitBytes: LimitBytes,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            logger.LogWarning("Could not read the log of coder pod {Pod}: {Status}", pod.Metadata.Name, ex.Response.StatusCode);
            return null;
        }
    }

    public async Task DeleteAsync(string jobName, CancellationToken ct)
    {
        try
        {
            await api.Batch.DeleteNamespacedJobAsync(
                    jobName,
                    options.CurrentValue.Namespace,
                    body: new V1DeleteOptions { PropagationPolicy = "Background" },
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone is the state we wanted.
        }
    }
}

/// <summary>What is actually running for a workload: the image a code fix must be analysed against.</summary>
public interface IWorkloadImageReader
{
    Task<(string? Image, string? Revision)> ReadAsync(TargetRef target, CancellationToken ct);

    /// <summary>
    /// The target with its top controller filled in when the signal that opened the incident did not
    /// resolve one. Never throws; returns the target unchanged when nothing can be learned.
    /// </summary>
    Task<TargetRef> ResolveWorkloadAsync(TargetRef target, CancellationToken ct);
}

public sealed class NullWorkloadImageReader : IWorkloadImageReader
{
    public Task<(string? Image, string? Revision)> ReadAsync(TargetRef target, CancellationToken ct) =>
        Task.FromResult<(string?, string?)>((null, null));

    public Task<TargetRef> ResolveWorkloadAsync(TargetRef target, CancellationToken ct) => Task.FromResult(target);
}

/// <summary>
/// Reads the controller's pod template, not a pod: the template is what the next pod will run, and
/// since CI tags images with the commit sha, its tag is the exact commit to analyse.
/// </summary>
public sealed class KubernetesWorkloadImageReader(KubernetesApi api, OwnerCache owners, ILogger<KubernetesWorkloadImageReader> logger)
    : IWorkloadImageReader
{
    /// <summary>
    /// Only the pod watch walks a pod up to its Deployment; an incident opened by an Alertmanager
    /// alert names the bare pod. The repository mapping is keyed by workload, so without this an
    /// alert-opened incident could never start a code fix - and which signal arrives first is not
    /// something the mapping should depend on. The incident itself is left as it is: its target
    /// feeds correlation, and changing it here would split or merge incidents after the fact.
    /// </summary>
    public async Task<TargetRef> ResolveWorkloadAsync(TargetRef target, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(target.OwnerKind) || target.Kind != "Pod" || string.IsNullOrEmpty(target.Name))
            return target;

        try
        {
            var ns = target.Namespace;

            // The pod an alert names is often already replaced - a crash-looping Deployment's pods
            // churn - but its ReplicaSet survives, and the pod name is that ReplicaSet's name plus
            // a random suffix.
            var meta = await owners.FetchAsync("Pod", ns, target.Name, ct).ConfigureAwait(false)
                ?? (ReplicaSetNameOf(target.Name) is { } rs ? await owners.FetchAsync("ReplicaSet", ns, rs, ct).ConfigureAwait(false) : null);

            if (meta is null)
                return target;

            await owners.WarmAsync(meta, ns, ct).ConfigureAwait(false);

            // A ReplicaSet fetched by name is itself a step of the walk; it may be the top when
            // nothing owns it.
            var top = OwnerWalker.TopController(meta, ns, owners.Lookup)
                ?? (meta.Name != target.Name ? new OwnerRef("ReplicaSet", meta.Name, meta.Uid) : (OwnerRef?)null);

            if (top is not { } owner)
                return target;

            var resolved = target.Clone();
            resolved.OwnerKind = owner.Kind;
            resolved.OwnerName = owner.Name;
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve the workload of pod {Namespace}/{Pod}", target.Namespace, target.Name);
            return target;
        }
    }

    /// <summary><c>shop-api-556d7fb5c6-2jwcd</c> -&gt; <c>shop-api-556d7fb5c6</c>; null when the name has no such shape.</summary>
    public static string? ReplicaSetNameOf(string podName)
    {
        var parts = podName.Split('-');

        return parts.Length >= 3 && parts[^1].Length == 5 && parts[^2].Length is >= 5 and <= 10
            && IsGenerated(parts[^1]) && IsGenerated(parts[^2])
            ? string.Join('-', parts[..^1])
            : null;
    }

    /// <summary>
    /// Kubernetes' generated suffixes and pod-template hashes use an alphabet without vowels and
    /// without 0, 1 and 3 (k8s.io apimachinery rand), so a numeric Job hash like <c>28745120</c> is not mistaken for one.
    /// </summary>
    private static bool IsGenerated(string s) => s.All(c => "bcdfghjklmnpqrstvwxz2456789".Contains(c));

    public async Task<(string? Image, string? Revision)> ReadAsync(TargetRef target, CancellationToken ct)
    {
        var kind = string.IsNullOrEmpty(target.OwnerKind) ? target.Kind : target.OwnerKind;
        var name = string.IsNullOrEmpty(target.OwnerName) ? target.Name : target.OwnerName;
        var ns = target.Namespace;

        try
        {
            switch (kind)
            {
                case "Deployment":
                {
                    var d = await api.Apps.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
                    var revision = d.Metadata.Annotations?.TryGetValue("deployment.kubernetes.io/revision", out var r) == true ? r : null;
                    return (Pick(d.Spec.Template.Spec.Containers, name), revision);
                }

                case "StatefulSet":
                {
                    var s = await api.Apps.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
                    return (Pick(s.Spec.Template.Spec.Containers, name), s.Status?.CurrentRevision);
                }

                case "DaemonSet":
                {
                    var s = await api.Apps.ReadNamespacedDaemonSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
                    return (Pick(s.Spec.Template.Spec.Containers, name), null);
                }

                case "Pod":
                {
                    var p = await api.Core.ReadNamespacedPodAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
                    return (Pick(p.Spec.Containers, name), null);
                }

                default:
                    return (null, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fatal: without an image the runner analyses the default branch's HEAD and says so.
            logger.LogWarning(ex, "Could not read the running image of {Kind} {Namespace}/{Name}", kind, ns, name);
            return (null, null);
        }
    }

    private static string? Pick(IList<V1Container>? containers, string workload)
    {
        if (containers is null || containers.Count == 0)
            return null;

        return containers.Count == 1
            ? containers[0].Image
            : (containers.FirstOrDefault(c => c.Name == workload) ?? containers[0]).Image;
    }
}
