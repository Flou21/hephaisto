using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Core.Domain;
using Hephaisto.Tests.TestData;
using NSubstitute;
using Hephaisto.Agent.Persistence.Repositories;

namespace Hephaisto.Tests.Pipeline;

/// <summary>
/// The judgement inside fact-gathering, and the one behaviour of the gatherer itself that is
/// a safety property rather than plumbing.
/// </summary>
public sealed class ClusterFactsRulesTests
{
    private static V1Pod Pod(string phase, params bool[] containersReady) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = "p", NamespaceProperty = "prod" },
            Status = new V1PodStatus
            {
                Phase = phase,
                ContainerStatuses = containersReady
                    .Select(r => new V1ContainerStatus
                    {
                        Name = "app",
                        Ready = r,
                        Image = "x",
                        ImageID = "x",
                        RestartCount = 0,
                    })
                    .ToList(),
            },
        };

    // --- what counts as unhealthy ---------------------------------------------------------

    [Theory]
    [InlineData("Failed")]
    [InlineData("Pending")]
    public void A_pod_in_a_bad_phase_is_unhealthy(string phase) =>
        ClusterFactsRules.IsUnhealthy(Pod(phase)).Should().BeTrue();

    [Fact]
    public void A_running_pod_whose_container_is_not_ready_is_unhealthy() =>
        // The CrashLoopBackOff shape: the pod is Running and the container is not Ready. This
        // is the case the fraction exists to notice, and phase alone would miss it.
        ClusterFactsRules.IsUnhealthy(Pod("Running", false)).Should().BeTrue();

    [Fact]
    public void A_running_ready_pod_is_healthy() =>
        ClusterFactsRules.IsUnhealthy(Pod("Running", true)).Should().BeFalse();

    [Fact]
    public void Finished_job_pods_do_not_count_against_the_cluster()
    {
        // A cluster running CronJobs accumulates Succeeded pods forever. Counting them would
        // drive the fraction past the ceiling and freeze the agent permanently - and it would
        // look like a cluster health problem rather than an arithmetic one.
        var pods = new[] { Pod("Succeeded"), Pod("Succeeded"), Pod("Running", true) };

        ClusterFactsRules.UnhealthyFraction(pods).Should().Be(0);
    }

    [Fact]
    public void The_fraction_is_unhealthy_over_considered()
    {
        var pods = new[] { Pod("Running", true), Pod("Running", false), Pod("Failed"), Pod("Succeeded") };

        ClusterFactsRules.UnhealthyFraction(pods).Should().Be(2d / 3d);
    }

    [Fact]
    public void An_empty_cluster_is_not_unhealthy() =>
        ClusterFactsRules.UnhealthyFraction([]).Should().Be(0);

    // --- selectors --------------------------------------------------------------------------

    [Fact]
    public void A_selector_renders_as_the_api_query_syntax() =>
        ClusterFactsRules.LabelSelector(new V1LabelSelector
        {
            MatchLabels = new Dictionary<string, string> { ["app"] = "api" },
        }).Should().Be("app=api");

    [Fact]
    public void An_absent_selector_is_null_rather_than_empty()
    {
        // Load-bearing. An empty selector means "every pod in the namespace", so returning ""
        // would widen a question about one workload into a question about all of them, and an
        // unrelated young pod would then block an action via the minimum-pod-age gate.
        ClusterFactsRules.LabelSelector(null).Should().BeNull();
        ClusterFactsRules.LabelSelector(new V1LabelSelector()).Should().BeNull();
        ClusterFactsRules.LabelSelector(new V1LabelSelector
        {
            MatchLabels = new Dictionary<string, string>(),
        }).Should().BeNull();
    }

    // --- revisions ----------------------------------------------------------------------------

    [Fact]
    public void A_replicaset_revision_comes_from_its_annotation() =>
        ClusterFactsRules.RevisionOf(new V1ReplicaSet
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string>
                {
                    [ClusterFactsRules.RevisionAnnotation] = "7",
                },
            },
        }).Should().Be(7);

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void An_unparseable_revision_sorts_last_rather_than_throwing(string raw) =>
        ClusterFactsRules.RevisionOf(new V1ReplicaSet
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string>
                {
                    [ClusterFactsRules.RevisionAnnotation] = raw,
                },
            },
        }).Should().Be(0);

    [Fact]
    public void A_replicaset_with_no_annotations_has_no_revision() =>
        ClusterFactsRules.RevisionOf(new V1ReplicaSet { Metadata = new V1ObjectMeta() }).Should().Be(0);

    // --- node conditions ------------------------------------------------------------------------

    [Fact]
    public void A_node_condition_is_only_true_when_it_says_True()
    {
        var node = new V1Node
        {
            Status = new V1NodeStatus
            {
                Conditions =
                [
                    new V1NodeCondition { Type = "MemoryPressure", Status = "True" },
                    new V1NodeCondition { Type = "DiskPressure", Status = "False" },
                ],
            },
        };

        ClusterFactsRules.HasCondition(node, "MemoryPressure").Should().BeTrue();
        ClusterFactsRules.HasCondition(node, "DiskPressure").Should().BeFalse();
        ClusterFactsRules.HasCondition(node, "PIDPressure").Should().BeFalse();
    }

    // --- the gatherer's own safety property --------------------------------------------------------

    [Fact]
    public async Task An_unreachable_cluster_produces_no_facts_at_all()
    {
        // The property worth a test: partial facts are more dangerous than none. ClusterFacts
        // has no "unknown" - a workload that could not be read is null, and a null workload
        // SKIPS the stability, blast-radius and last-replica gates rather than failing them.
        // So a half-built record turns a read failure into a quieter policy engine, and the
        // resulting verdict is indistinguishable from a considered one. It must throw.
        var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" });

        var actions = Substitute.For<IActionRepository>();
        actions
            .ReadBudgetAsync(Arg.Any<Guid>(), Arg.Any<TargetRef>(), Arg.Any<AgentMode>(), Arg.Any<CancellationToken>())
            .Returns(new ActionBudgetSnapshot
            {
                Mode = AgentMode.Auto,
                ActionsOnIncident = 0,
                ActionsOnWorkloadLastHour = 0,
                ActionsClusterWideLastHour = 0,
                ActionsClusterWideLastDay = 0,
            });

        var gatherer = new ClusterFactsGatherer(
            new KubernetesApi(client),
            actions,
            new PolicyStub(Given.Options()),
            Given.Clock(),
            NullLogger<ClusterFactsGatherer>.Instance);

        var act = async () => await gatherer.GatherAsync(Given.Incident(), AgentMode.Auto, CancellationToken.None);

        await act.Should().ThrowAsync<ClusterFactsUnavailableException>();
    }

    private sealed class PolicyStub(Hephaisto.Core.Policy.PolicyOptions value)
        : Microsoft.Extensions.Options.IOptionsMonitor<Hephaisto.Core.Policy.PolicyOptions>
    {
        public Hephaisto.Core.Policy.PolicyOptions CurrentValue => value;

        public Hephaisto.Core.Policy.PolicyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<Hephaisto.Core.Policy.PolicyOptions, string?> listener) => null;
    }
}
