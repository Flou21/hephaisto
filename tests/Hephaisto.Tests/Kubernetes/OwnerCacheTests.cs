using System.Net;
using System.Text;

using k8s;
using k8s.Models;

using Microsoft.Extensions.Logging.Abstractions;

using Hephaisto.Agent.Kubernetes;

namespace Hephaisto.Tests.Kubernetes;

/// <summary>
/// How long the owner cache believes what it was told. An object that was found is its owner
/// for good; an object that was NOT found may exist a minute later under the same name, and a
/// request that failed said nothing about the object at all.
/// </summary>
/// <remarks>
/// The dev cluster, four suite runs in a row: a Deployment and its ReplicaSet - whose name is a
/// hash of the unchanged pod template - were deleted and created again within the hour, the
/// lookups made in between were held as "does not exist" for that hour, and the new pod's
/// incident was filed under <c>hephaisto-chaos/ReplicaSet/catalog-api-56776568f4</c>.
/// </remarks>
public sealed class OwnerCacheTests
{
    private const string Ns = "hephaisto-chaos";
    private const string ReplicaSet = "catalog-api-56776568f4";
    private const string Deployment = "catalog-api";

    private static readonly string ReplicaSetPath = $"/apis/apps/v1/namespaces/{Ns}/replicasets/{ReplicaSet}";
    private static readonly string DeploymentPath = $"/apis/apps/v1/namespaces/{Ns}/deployments/{Deployment}";

    /// <summary>An API server that is a dictionary: a path answers its JSON, a status, or 404.</summary>
    private sealed class FakeApiServer : DelegatingHandler
    {
        public Dictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HttpStatusCode> Failing { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public int Asked(string path) => Requests.Count(r => r == path);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);

            if (Failing.TryGetValue(path, out var status))
            {
                return Task.FromResult(Json(request, status, """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"etcdserver: request timed out"}"""));
            }

            return Task.FromResult(Objects.TryGetValue(path, out var body)
                ? Json(request, HttpStatusCode.OK, body)
                : Json(request, HttpStatusCode.NotFound, """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}"""));
        }

        private static HttpResponseMessage Json(HttpRequestMessage request, HttpStatusCode status, string body) =>
            new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static readonly string ReplicaSetJson =
        "{\"apiVersion\":\"apps/v1\",\"kind\":\"ReplicaSet\",\"metadata\":{\"name\":\"" + ReplicaSet + "\",\"namespace\":\"" + Ns + "\",\"uid\":\"rs-2\","
        + "\"ownerReferences\":[{\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\",\"name\":\"" + Deployment + "\",\"uid\":\"dep-2\",\"controller\":true}]}}";

    private static readonly string DeploymentJson =
        "{\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\",\"metadata\":{\"name\":\"" + Deployment + "\",\"namespace\":\"" + Ns + "\",\"uid\":\"dep-2\"}}";

    private static (OwnerCache Cache, FakeApiServer Server, ManualTime Time) World()
    {
        var server = new FakeApiServer();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }, server);

        return (new OwnerCache(new KubernetesApi(client), time, NullLogger<OwnerCache>.Instance), server, time);
    }

    /// <summary>A pod of the ReplicaSet, as the watcher sees it.</summary>
    private static V1ObjectMeta Pod() => new()
    {
        Name = $"{ReplicaSet}-x7k2p",
        NamespaceProperty = Ns,
        OwnerReferences = [new V1OwnerReference { ApiVersion = "apps/v1", Kind = "ReplicaSet", Name = ReplicaSet, Uid = "rs-2", Controller = true }],
    };

    private static void Create(FakeApiServer server)
    {
        server.Objects[ReplicaSetPath] = ReplicaSetJson;
        server.Objects[DeploymentPath] = DeploymentJson;
    }

    [Fact]
    public void A_missing_object_is_remembered_for_seconds_and_a_found_one_for_the_hour()
    {
        OwnerCache.NegativeTtl.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        OwnerCache.NegativeTtl.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10), "a crash loop must not be a request per observation");
        OwnerCache.Ttl.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task An_object_that_was_not_there_is_not_asked_about_again_at_once()
    {
        var (cache, server, time) = World();

        (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None)).Should().BeNull();

        // The purpose the negative entry has: a crash-looping pod whose ReplicaSet is gone is
        // observed again and again, and none of those observations is a request.
        for (var i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None)).Should().BeNull();
        }

        server.Asked(ReplicaSetPath).Should().Be(1);
    }

    [Fact]
    public async Task Not_found_then_created_is_found_after_the_short_lifetime_and_not_before()
    {
        var (cache, server, time) = World();

        // While the fixture is gone: the watcher is replayed an event of the old pod.
        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);
        OwnerWalker.TopController(Pod(), Ns, cache.Lookup).Should().Be(new OwnerRef("ReplicaSet", ReplicaSet, "rs-2"));

        // Created again, under the same two names.
        Create(server);

        time.Advance(OwnerCache.NegativeTtl - TimeSpan.FromSeconds(1));
        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);

        OwnerWalker.TopController(Pod(), Ns, cache.Lookup)!.Value.Kind.Should().Be("ReplicaSet", "the negative entry still stands");
        server.Asked(ReplicaSetPath).Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(2));
        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);

        OwnerWalker.TopController(Pod(), Ns, cache.Lookup).Should().Be(new OwnerRef("Deployment", Deployment, "dep-2"));
        server.Asked(ReplicaSetPath).Should().Be(2);
        server.Asked(DeploymentPath).Should().Be(1);
    }

    [Fact]
    public async Task A_missing_object_is_no_longer_missing_an_hour_minus_a_minute_later()
    {
        var (cache, server, time) = World();

        (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None)).Should().BeNull();
        Create(server);

        // What the cache did before: the same lifetime as a hit.
        time.Advance(OwnerCache.Ttl - TimeSpan.FromMinutes(1));

        (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None))!.Name.Should().Be(ReplicaSet);
    }

    [Fact]
    public async Task An_object_that_was_found_is_served_from_memory_for_the_hour()
    {
        var (cache, server, time) = World();
        Create(server);

        (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None))!.Name.Should().Be(ReplicaSet);

        // Gone from the API server, and still its pod's owner: ownership does not change.
        server.Objects.Clear();
        time.Advance(OwnerCache.Ttl - TimeSpan.FromMinutes(1));

        (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None))!.Name.Should().Be(ReplicaSet);
        cache.Lookup("ReplicaSet", Ns, ReplicaSet)!.Name.Should().Be(ReplicaSet);
        server.Asked(ReplicaSetPath).Should().Be(1);

        time.Advance(TimeSpan.FromMinutes(2));

        (await cache.FetchAsync("ReplicaSet", Ns, ReplicaSet, CancellationToken.None)).Should().BeNull("the hour is over, and it is asked for again");
        server.Asked(ReplicaSetPath).Should().Be(2);
    }

    // --- gone, as opposed to not known ------------------------------------------------------

    private static readonly string PodPath = $"/api/v1/namespaces/{Ns}/pods/{ReplicaSet}-x7k2p";

    private static Corev1Event BackOff() => new()
    {
        Metadata = new V1ObjectMeta { Name = "x7k2p.backoff", NamespaceProperty = Ns, Uid = "ev-1" },
        Type = "Warning",
        Reason = "BackOff",
        Message = "Back-off restarting failed container app in pod",
        LastTimestamp = new DateTime(2026, 10, 6, 8, 55, 0, DateTimeKind.Utc),
        InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = $"{ReplicaSet}-x7k2p", NamespaceProperty = Ns },
    };

    [Fact]
    public async Task Only_a_404_is_gone_and_only_for_as_long_as_it_is_remembered()
    {
        var (cache, server, time) = World();

        cache.IsGone("Pod", Ns, $"{ReplicaSet}-x7k2p").Should().BeFalse("nobody has asked");

        (await cache.FetchAsync("Pod", Ns, $"{ReplicaSet}-x7k2p", CancellationToken.None)).Should().BeNull();
        cache.IsGone("Pod", Ns, $"{ReplicaSet}-x7k2p").Should().BeTrue();
        server.Asked(PodPath).Should().Be(1, "asking whether it is gone is not a request");

        // A kind this cache does not read was never asked for, so it is not known to be gone.
        (await cache.FetchAsync("PersistentVolumeClaim", Ns, "data", CancellationToken.None)).Should().BeNull();
        cache.IsGone("PersistentVolumeClaim", Ns, "data").Should().BeFalse();

        time.Advance(OwnerCache.NegativeTtl + TimeSpan.FromSeconds(1));
        cache.IsGone("Pod", Ns, $"{ReplicaSet}-x7k2p").Should().BeFalse("what was true half a minute ago is not known now");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_object_that_could_not_be_read_is_not_gone(HttpStatusCode status)
    {
        var (cache, server, _) = World();
        server.Failing[PodPath] = status;

        (await cache.FetchAsync("Pod", Ns, $"{ReplicaSet}-x7k2p", CancellationToken.None)).Should().BeNull();

        cache.IsGone("Pod", Ns, $"{ReplicaSet}-x7k2p").Should().BeFalse();
    }

    [Fact]
    public async Task An_object_that_exists_is_not_gone()
    {
        var (cache, server, _) = World();
        Create(server);

        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);

        cache.IsGone("ReplicaSet", Ns, ReplicaSet).Should().BeFalse();
        cache.IsGone("Deployment", Ns, Deployment).Should().BeFalse();
    }

    /// <summary>
    /// The watcher's three steps for an event, with the real cache: fetch the object the event
    /// is about, warm its chain, map. Seen on the dev cluster: an agent restarted nine minutes
    /// after a crash-looping fixture was deleted opened "CrashLoopBackOff on shop-api-...-zpx67"
    /// with the pod as its own workload.
    /// </summary>
    [Fact]
    public async Task A_replayed_warning_about_a_deleted_pod_is_no_signal_and_one_about_a_pod_that_could_not_be_read_still_is()
    {
        var (cache, server, time) = World();
        var notBefore = new DateTimeOffset(2026, 10, 6, 8, 50, 0, TimeSpan.Zero);

        async Task<Hephaisto.Core.Domain.Signal?> HandleAsync()
        {
            var meta = await cache.FetchAsync("Pod", Ns, $"{ReplicaSet}-x7k2p", CancellationToken.None);
            await cache.WarmAsync(meta, Ns, CancellationToken.None);

            return SignalMapper.FromEvent(BackOff(), "dev", cache.Lookup, notBefore, cache.IsGone);
        }

        // The pod is gone: 404.
        (await HandleAsync()).Should().BeNull();

        // The API server is failing: the pod is not known to be gone, and the warning is kept.
        time.Advance(OwnerCache.NegativeTtl + TimeSpan.FromSeconds(1));
        server.Failing[PodPath] = HttpStatusCode.ServiceUnavailable;

        (await HandleAsync())!.Target.WorkloadKey.Should().Be($"{Ns}/Pod/{ReplicaSet}-x7k2p");

        // The pod is there: the warning is its Deployment's.
        time.Advance(OwnerCache.NegativeTtl + TimeSpan.FromSeconds(1));
        server.Failing.Clear();
        Create(server);
        server.Objects[PodPath] = "{\"apiVersion\":\"v1\",\"kind\":\"Pod\",\"metadata\":{\"name\":\"" + ReplicaSet + "-x7k2p\",\"namespace\":\"" + Ns + "\","
            + "\"ownerReferences\":[{\"apiVersion\":\"apps/v1\",\"kind\":\"ReplicaSet\",\"name\":\"" + ReplicaSet + "\",\"uid\":\"rs-2\",\"controller\":true}]}}";

        (await HandleAsync())!.Target.WorkloadKey.Should().Be($"{Ns}/Deployment/{Deployment}");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_request_that_failed_is_not_an_hour_of_not_found(HttpStatusCode status)
    {
        var (cache, server, time) = World();
        Create(server);
        server.Failing[ReplicaSetPath] = status;

        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);
        OwnerWalker.TopController(Pod(), Ns, cache.Lookup)!.Value.Kind.Should().Be("ReplicaSet");

        // Held briefly all the same: a failing API server is not asked per observation.
        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);
        server.Asked(ReplicaSetPath).Should().Be(1);

        server.Failing.Clear();
        time.Advance(OwnerCache.NegativeTtl + TimeSpan.FromSeconds(1));
        await cache.WarmAsync(Pod(), Ns, CancellationToken.None);

        OwnerWalker.TopController(Pod(), Ns, cache.Lookup).Should().Be(new OwnerRef("Deployment", Deployment, "dep-2"));
    }
}
