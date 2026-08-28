using k8s;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>
/// The one place that turns the DI-registered <see cref="IKubernetes"/> into the operation
/// groups the rest of this layer calls.
/// </summary>
/// <remarks>
/// <para>
/// KubernetesClient's <see cref="IKubernetes"/> is close to a marker interface - it carries
/// <c>BaseUri</c> and the exec/port-forward helpers and nothing else. Every generated
/// operation lives on a separate interface (<see cref="ICoreV1Operations"/>,
/// <see cref="IAppsV1Operations"/>, …) which the concrete <c>k8s.Kubernetes</c> implements
/// but <see cref="IKubernetes"/> does not inherit, so reaching an API means a cast.
/// </para>
/// <para>
/// Doing all of those casts once, in a constructor resolved at startup, is the point: a
/// substitute client that forgets one of these fails while the host is starting, with a
/// message naming the missing interface, instead of throwing <c>InvalidCastException</c>
/// from inside a tool call halfway through an incident.
/// </para>
/// </remarks>
public sealed class KubernetesApi
{
    public KubernetesApi(IKubernetes client)
    {
        ArgumentNullException.ThrowIfNull(client);

        Client = client;
        Core = As<ICoreV1Operations>(client);
        Apps = As<IAppsV1Operations>(client);
        Batch = As<IBatchV1Operations>(client);
        Authorization = As<IAuthorizationV1Operations>(client);
        Autoscaling = As<IAutoscalingV2Operations>(client);
        CustomObjects = As<ICustomObjectsOperations>(client);
    }

    public IKubernetes Client { get; }

    public ICoreV1Operations Core { get; }

    public IAppsV1Operations Apps { get; }

    public IBatchV1Operations Batch { get; }

    public IAuthorizationV1Operations Authorization { get; }

    /// <summary>v2, not v1: only v2 carries the multi-metric and behaviour fields worth reading.</summary>
    public IAutoscalingV2Operations Autoscaling { get; }

    /// <summary>metrics.k8s.io is an aggregated API with no generated client, so it goes through here.</summary>
    public ICustomObjectsOperations CustomObjects { get; }

    private static T As<T>(IKubernetes client)
        where T : class =>
        client as T ?? throw new InvalidOperationException(
            $"The registered IKubernetes implementation ({client.GetType().FullName}) does not implement " +
            $"{typeof(T).Name}. Hephaisto needs the full generated client, not a partial stub.");
}
