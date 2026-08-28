using k8s.Models;

using Watchtower.Agent.Kubernetes;

namespace Watchtower.Tests.Kubernetes;

/// <summary>
/// Hand-built Kubernetes objects, shaped like the ones the chaos fixtures produce.
/// </summary>
/// <remarks>
/// Every helper defaults to a <b>healthy</b> object, so a test reads as the single fact it
/// changes - the container state that flips the classification - rather than as forty lines of
/// setup with the significant one hidden in the middle.
/// </remarks>
internal static class K8sFixtures
{
    public const string Cluster = "test-cluster";

    public const string Namespace = "watchtower-chaos";

    public static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    public static V1Pod Pod(
        string name = "api-7d4c9f8b6-x2k9p",
        string phase = "Running",
        string? reason = null,
        IList<V1ContainerStatus>? containers = null,
        IList<V1PodCondition>? conditions = null,
        IList<V1OwnerReference>? owners = null,
        string node = "node-1") =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = Namespace,
                Uid = $"uid-{name}",
                CreationTimestamp = Now.AddHours(-1).UtcDateTime,
                OwnerReferences = owners,
            },
            Spec = new V1PodSpec
            {
                NodeName = node,
                Containers = [new V1Container { Name = "app", Image = "ghcr.io/example/api:1.4.2" }],
            },
            Status = new V1PodStatus
            {
                Phase = phase,
                Reason = reason,
                Conditions = conditions,
                ContainerStatuses = containers ?? [Container()],
            },
        };

    public static V1ContainerStatus Container(
        string name = "app",
        bool ready = true,
        int restartCount = 0,
        V1ContainerState? state = null,
        V1ContainerState? lastState = null,
        string? memoryLimit = "512Mi") =>
        new()
        {
            Name = name,
            Ready = ready,
            RestartCount = restartCount,
            Image = "ghcr.io/example/api:1.4.2",
            State = state ?? new V1ContainerState { Running = new V1ContainerStateRunning() },
            LastState = lastState,
            Resources = memoryLimit is null
                ? null
                : new V1ResourceRequirements
                {
                    Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new(memoryLimit) },
                },
        };

    public static V1ContainerState Waiting(string reason, string message = "") =>
        new() { Waiting = new V1ContainerStateWaiting { Reason = reason, Message = message } };

    public static V1ContainerState Terminated(int exitCode, string reason) =>
        new()
        {
            Terminated = new V1ContainerStateTerminated
            {
                ExitCode = exitCode,
                Reason = reason,
                FinishedAt = Now.AddMinutes(-2).UtcDateTime,
            },
        };

    public static V1OwnerReference OwnedBy(string kind, string name) =>
        new()
        {
            Kind = kind,
            Name = name,
            Uid = $"uid-{name}",
            Controller = true,
            ApiVersion = kind == "ReplicaSet" ? "apps/v1" : "apps/v1",
        };

    public static V1ObjectMeta Meta(string name, params V1OwnerReference[] owners) =>
        new()
        {
            Name = name,
            NamespaceProperty = Namespace,
            Uid = $"uid-{name}",
            OwnerReferences = owners.Length == 0 ? null : owners,
        };

    /// <summary>
    /// An <see cref="OwnerLookup"/> backed by a dictionary, which is what makes the owner walk
    /// testable without a cluster: the walk is pure given a pure lookup.
    /// </summary>
    public static OwnerLookup LookupOf(params V1ObjectMeta[] objects)
    {
        var map = objects.ToDictionary(o => o.Name!, StringComparer.Ordinal);

        return (_, _, name) => map.GetValueOrDefault(name);
    }
}
