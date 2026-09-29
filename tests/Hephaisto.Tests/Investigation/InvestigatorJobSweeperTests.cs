using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Investigations.Jobs;
using Hephaisto.Agent.Kubernetes;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// After an agent restart every running investigator Job is an orphan: its session is gone, its
/// calls are refused, and it holds a node and the subscription's rate limit until its deadline.
/// The sweeper removes exactly those, against a real client over a fake API server.
/// </summary>
public sealed class InvestigatorJobSweeperTests
{
    private sealed class FakeApiServer(IReadOnlyList<V1Job> jobs) : DelegatingHandler
    {
        public List<string> Deleted { get; } = [];

        public string? LabelSelector { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/apis/batch/v1/namespaces/hephaisto-coder/jobs")
            {
                LabelSelector = Uri.UnescapeDataString(request.RequestUri.Query);
                return Json(request, new V1JobList { Items = [.. jobs], ApiVersion = "batch/v1", Kind = "JobList" });
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/apis/batch/v1/namespaces/hephaisto-coder/jobs/", StringComparison.Ordinal))
            {
                Deleted.Add(path[(path.LastIndexOf('/') + 1)..]);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("""{"kind":"Status","apiVersion":"v1","status":"Success"}""", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        private static Task<HttpResponseMessage> Json(HttpRequestMessage request, object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(KubernetesJson.Serialize(body), Encoding.UTF8, "application/json"),
            });
    }

    private static V1Job Job(string name, Guid? investigation, bool finished = false) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = "hephaisto-coder",
            Labels = investigation is { } id
                ? new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = InvestigateJobSpec.AppLabel,
                    [InvestigateJobSpec.InvestigationLabel] = id.ToString(),
                }
                : new Dictionary<string, string> { ["app.kubernetes.io/name"] = InvestigateJobSpec.AppLabel },
        },
        Status = finished ? new V1JobStatus { Succeeded = 1 } : new V1JobStatus { Active = 1 },
    };

    [Fact]
    public async Task Only_running_Jobs_no_live_run_owns_are_deleted()
    {
        var clock = new TestClock();
        var sessions = new InvestigationJobSessions(clock);
        var live = Guid.CreateVersion7();

        sessions.Open(new InvestigationJobSession
        {
            InvestigationId = live,
            IncidentId = Guid.CreateVersion7(),
            Tools = new Dictionary<string, AIFunction>(),
            Conclusion = new InvestigationRunner.ConclusionHolder(),
            ExpiresAt = clock.UtcNow + TimeSpan.FromMinutes(5),
        });

        var server = new FakeApiServer(
        [
            Job("investigate-live", live),
            Job("investigate-orphan", Guid.CreateVersion7()),
            Job("investigate-unlabelled", investigation: null),
            Job("investigate-done", Guid.CreateVersion7(), finished: true),
        ]);

        var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }, server);

        var sweeper = new InvestigatorJobSweeper(
            new KubernetesApi(client),
            sessions,
            new TestOptionsMonitor<InvestigationJobOptions>(new InvestigationJobOptions { Enabled = true }),
            new TestOptionsMonitor<CodeFixOptions>(new CodeFixOptions()),
            NullLogger<InvestigatorJobSweeper>.Instance);

        var swept = await sweeper.SweepAsync(CancellationToken.None);

        swept.Should().Be(2);
        server.Deleted.Should().BeEquivalentTo(["investigate-orphan", "investigate-unlabelled"]);
        server.LabelSelector.Should().Contain("app.kubernetes.io/name=hephaisto-investigator",
            "it lists investigators only; a coder Job is never its business");
    }
}
