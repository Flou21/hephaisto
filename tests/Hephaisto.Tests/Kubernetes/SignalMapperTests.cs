using k8s.Models;

using Hephaisto.Agent.Kubernetes;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Kubernetes;

public class SignalMapperTests
{
    // ------------------------------------------------------------------
    // The one that matters most
    // ------------------------------------------------------------------

    /// <summary>
    /// An OOMKilled container is simultaneously in CrashLoopBackOff, because the kubelet backs
    /// off any repeatedly-failing container. Classifying it as a crash loop sends the
    /// investigation to the CrashLoopBackOff runbook, whose first instruction is to read the
    /// previous container's logs - and an OOMKilled process wrote none, because the kernel
    /// killed it without warning. The investigation then concludes "no logs, unknown cause"
    /// while the actual answer, a memory limit, was in the container status all along.
    /// </summary>
    [Fact]
    public void OomKilled_wins_over_CrashLoopBackOff_when_both_are_present()
    {
        var pod = K8sFixtures.Pod(containers:
        [
            K8sFixtures.Container(
                ready: false,
                restartCount: 14,
                state: K8sFixtures.Waiting("CrashLoopBackOff", "back-off 5m0s restarting failed container"),
                lastState: K8sFixtures.Terminated(137, "OOMKilled")),
        ]);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal.Should().NotBeNull();
        signal!.Kind.Should().Be(SignalKind.OomKilled);
        signal.Kind.Should().NotBe(SignalKind.CrashLoopBackOff);
        signal.Reason.Should().Be("OOMKilled");
        signal.Message.Should().Contain("137").And.Contain("512Mi");
    }

    [Fact]
    public void CrashLoopBackOff_without_an_oom_kill_stays_a_crash_loop()
    {
        var pod = K8sFixtures.Pod(containers:
        [
            K8sFixtures.Container(
                ready: false,
                restartCount: 6,
                state: K8sFixtures.Waiting("CrashLoopBackOff"),
                lastState: K8sFixtures.Terminated(1, "Error")),
        ]);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal!.Kind.Should().Be(SignalKind.CrashLoopBackOff);
        signal.Message.Should().Contain("exit code 1");
    }

    // ------------------------------------------------------------------
    // Owner resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// The walk must reach the Deployment. Stopping at the ReplicaSet means a rollout produces
    /// a new workload key, so cooldowns, budgets and oscillation detection - all keyed on the
    /// workload - silently stop applying at the exact moment a deploy breaks something.
    /// </summary>
    [Fact]
    public void Owner_walk_resolves_to_the_Deployment_not_the_ReplicaSet_or_the_Pod()
    {
        var pod = K8sFixtures.Pod(
            containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("CrashLoopBackOff"))],
            owners: [K8sFixtures.OwnedBy("ReplicaSet", "api-7d4c9f8b6")]);

        var lookup = K8sFixtures.LookupOf(
            K8sFixtures.Meta("api-7d4c9f8b6", K8sFixtures.OwnedBy("Deployment", "api")),
            K8sFixtures.Meta("api"));

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None, lookup);

        signal!.Target.OwnerKind.Should().Be("Deployment");
        signal.Target.OwnerName.Should().Be("api");
        signal.Target.Kind.Should().Be("Pod");
        signal.Target.Name.Should().Be("api-7d4c9f8b6-x2k9p");
        signal.Target.WorkloadKey.Should().Be($"{K8sFixtures.Namespace}/Deployment/api");
    }

    [Fact]
    public void Owner_walk_stops_at_the_deepest_link_it_can_resolve()
    {
        var pod = K8sFixtures.Pod(
            containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("CrashLoopBackOff"))],
            owners: [K8sFixtures.OwnedBy("ReplicaSet", "api-7d4c9f8b6")]);

        // No lookup at all: the ReplicaSet cannot be fetched, so the walk ends there. Coarse,
        // but still not keyed on the pod name.
        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal!.Target.OwnerKind.Should().Be("ReplicaSet");
        signal.Target.OwnerName.Should().Be("api-7d4c9f8b6");
    }

    /// <summary>
    /// The property the whole dedup pipeline rests on. Two pods of one Deployment - here from
    /// two different ReplicaSets, which is what a rollout produces - must fingerprint
    /// identically, or one broken Deployment becomes fifty incidents and fifty LLM bills.
    /// </summary>
    [Fact]
    public void Two_pods_of_one_Deployment_share_a_fingerprint()
    {
        var lookup = K8sFixtures.LookupOf(
            K8sFixtures.Meta("api-7d4c9f8b6", K8sFixtures.OwnedBy("Deployment", "api")),
            K8sFixtures.Meta("api-5c8b7d9f4", K8sFixtures.OwnedBy("Deployment", "api")),
            K8sFixtures.Meta("api"));

        var first = SignalMapper.FromPod(
            K8sFixtures.Pod(
                name: "api-7d4c9f8b6-x2k9p",
                containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("CrashLoopBackOff"))],
                owners: [K8sFixtures.OwnedBy("ReplicaSet", "api-7d4c9f8b6")]),
            K8sFixtures.Cluster,
            K8sFixtures.Now,
            PodTrend.None,
            lookup);

        var second = SignalMapper.FromPod(
            K8sFixtures.Pod(
                name: "api-5c8b7d9f4-qq81z",
                containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("CrashLoopBackOff"))],
                owners: [K8sFixtures.OwnedBy("ReplicaSet", "api-5c8b7d9f4")]),
            K8sFixtures.Cluster,
            K8sFixtures.Now,
            PodTrend.None,
            lookup);

        first!.Fingerprint.Should().Be(second!.Fingerprint);
        first.Fingerprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void A_different_cluster_produces_a_different_fingerprint()
    {
        var pod = K8sFixtures.Pod(
            containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("CrashLoopBackOff"))]);

        var here = SignalMapper.FromPod(pod, "prod-eu", K8sFixtures.Now, PodTrend.None);
        var there = SignalMapper.FromPod(pod, "staging-eu", K8sFixtures.Now, PodTrend.None);

        here!.Fingerprint.Should().NotBe(there!.Fingerprint);
    }

    // ------------------------------------------------------------------
    // The deliberately-similar pair: chaos fixtures C4 and C7
    // ------------------------------------------------------------------

    /// <summary>
    /// C4 (imagepull) and C7 (configerror) both leave the pod Waiting with no logs and no
    /// restarts. The container state <i>reason</i> is the only reliable discriminator - both
    /// runbooks say so explicitly - and the messages are close enough that inferring from them
    /// gets it wrong.
    /// </summary>
    [Theory]
    [InlineData("ImagePullBackOff", SignalKind.ImagePullBackOff)]
    [InlineData("ErrImagePull", SignalKind.ImagePullBackOff)]
    [InlineData("CreateContainerConfigError", SignalKind.ConfigError)]
    public void Waiting_reason_discriminates_image_pull_from_config_error(string reason, SignalKind expected)
    {
        var pod = K8sFixtures.Pod(
            phase: "Pending",
            containers:
            [
                K8sFixtures.Container(
                    ready: false,
                    state: K8sFixtures.Waiting(reason, "configmap \"app-config\" not found")),
            ]);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal!.Kind.Should().Be(expected);
        signal.Reason.Should().Be(reason);
    }

    [Fact]
    public void Image_pull_and_config_error_do_not_collapse_into_one_incident()
    {
        var pull = SignalMapper.FromPod(
            K8sFixtures.Pod(containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("ImagePullBackOff"))]),
            K8sFixtures.Cluster,
            K8sFixtures.Now,
            PodTrend.None);

        var config = SignalMapper.FromPod(
            K8sFixtures.Pod(containers: [K8sFixtures.Container(ready: false, state: K8sFixtures.Waiting("CreateContainerConfigError"))]),
            K8sFixtures.Cluster,
            K8sFixtures.Now,
            PodTrend.None);

        pull!.Fingerprint.Should().NotBe(config!.Fingerprint);
    }

    // ------------------------------------------------------------------
    // The rest of the pod vocabulary
    // ------------------------------------------------------------------

    [Fact]
    public void Pending_with_an_Unschedulable_condition_maps_to_Unschedulable()
    {
        var pod = K8sFixtures.Pod(
            phase: "Pending",
            node: string.Empty,
            containers: [],
            conditions:
            [
                new V1PodCondition
                {
                    Type = "PodScheduled",
                    Status = "False",
                    Reason = "Unschedulable",
                    Message = "0/1 nodes are available: 1 Insufficient memory.",
                },
            ]);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal!.Kind.Should().Be(SignalKind.Unschedulable);

        // The message enumerates every node and why each was rejected. It is the entire
        // diagnosis and must survive verbatim.
        signal.Message.Should().Be("0/1 nodes are available: 1 Insufficient memory.");
    }

    [Fact]
    public void An_evicted_pod_is_node_pressure_not_a_pod_problem()
    {
        var pod = K8sFixtures.Pod(
            phase: "Failed",
            reason: "Evicted",
            containers: []);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal!.Kind.Should().Be(SignalKind.NodePressure);
    }

    [Fact]
    public void A_healthy_pod_produces_no_signal()
    {
        SignalMapper.FromPod(K8sFixtures.Pod(), K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None)
            .Should().BeNull();
    }

    [Fact]
    public void Restarts_climbing_in_the_window_become_a_RestartStorm()
    {
        var pod = K8sFixtures.Pod(containers: [K8sFixtures.Container(restartCount: 9)]);

        var quiet = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, new PodTrend(2, 0));
        var storm = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, new PodTrend(5, 0));

        // A high total restart count with no recent restarts is history, not an incident.
        quiet.Should().BeNull();
        storm!.Kind.Should().Be(SignalKind.RestartStorm);
    }

    [Fact]
    public void Readiness_oscillation_becomes_ReadinessFlapping()
    {
        var pod = K8sFixtures.Pod(containers: [K8sFixtures.Container(ready: false)]);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, new PodTrend(0, 6));

        signal!.Kind.Should().Be(SignalKind.ReadinessFlapping);
    }

    /// <summary>
    /// Flapping is only the right story while the container keeps running. Once restarts are
    /// climbing as well it is a different incident, and the ReadinessFlapping runbook - whose
    /// whole point is "do not restart this" - would be the wrong one to reach for.
    /// </summary>
    [Fact]
    public void A_crash_loop_outranks_flapping_readiness()
    {
        var pod = K8sFixtures.Pod(containers:
        [
            K8sFixtures.Container(ready: false, restartCount: 7, state: K8sFixtures.Waiting("CrashLoopBackOff")),
        ]);

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, new PodTrend(5, 8));

        signal!.Kind.Should().Be(SignalKind.CrashLoopBackOff);
    }

    [Fact]
    public void An_init_container_failure_is_reported_ahead_of_the_app_container()
    {
        var pod = K8sFixtures.Pod(containers: [K8sFixtures.Container()]);
        pod.Status!.InitContainerStatuses =
        [
            K8sFixtures.Container(name: "wait-for-db", ready: false, state: K8sFixtures.Waiting("ImagePullBackOff")),
        ];

        var signal = SignalMapper.FromPod(pod, K8sFixtures.Cluster, K8sFixtures.Now, PodTrend.None);

        signal!.Kind.Should().Be(SignalKind.ImagePullBackOff);
        signal.Labels["container"].Should().Be("wait-for-db");
    }

    // ------------------------------------------------------------------
    // Nodes, jobs, events, alerts
    // ------------------------------------------------------------------

    [Fact]
    public void A_node_under_memory_pressure_produces_a_node_scoped_signal()
    {
        var node = new V1Node
        {
            Metadata = new V1ObjectMeta { Name = "node-1", Uid = "uid-node-1" },
            Status = new V1NodeStatus
            {
                Conditions =
                [
                    new V1NodeCondition { Type = "Ready", Status = "True" },
                    new V1NodeCondition { Type = "MemoryPressure", Status = "True", Reason = "KubeletHasInsufficientMemory" },
                ],
            },
        };

        var signal = SignalMapper.FromNode(node, K8sFixtures.Cluster, K8sFixtures.Now);

        signal!.Kind.Should().Be(SignalKind.NodePressure);
        signal.Severity.Should().Be(Severity.Critical);
        signal.Target.Kind.Should().Be("Node");
        signal.Target.NodeName.Should().Be("node-1");
    }

    [Fact]
    public void A_ready_node_produces_no_signal()
    {
        var node = new V1Node
        {
            Metadata = new V1ObjectMeta { Name = "node-1" },
            Status = new V1NodeStatus { Conditions = [new V1NodeCondition { Type = "Ready", Status = "True" }] },
        };

        SignalMapper.FromNode(node, K8sFixtures.Cluster, K8sFixtures.Now).Should().BeNull();
    }

    /// <summary>
    /// A nightly CronJob that fails five nights running is one incident with a rising count,
    /// not five - which is only true if the Job resolves up to the CronJob above it.
    /// </summary>
    [Fact]
    public void A_failed_Job_resolves_to_its_CronJob()
    {
        var job = new V1Job
        {
            Metadata = K8sFixtures.Meta("nightly-import-29155680", K8sFixtures.OwnedBy("CronJob", "nightly-import")),
            Spec = new V1JobSpec { BackoffLimit = 4, Template = new V1PodTemplateSpec() },
            Status = new V1JobStatus
            {
                Failed = 5,
                StartTime = K8sFixtures.Now.AddMinutes(-30).UtcDateTime,
                Conditions =
                [
                    new V1JobCondition
                    {
                        Type = "Failed",
                        Status = "True",
                        Reason = "BackoffLimitExceeded",
                        Message = "Job has reached the specified backoff limit",
                    },
                ],
            },
        };

        var signal = SignalMapper.FromJob(
            job,
            K8sFixtures.Cluster,
            K8sFixtures.Now,
            K8sFixtures.LookupOf(K8sFixtures.Meta("nightly-import")));

        signal!.Kind.Should().Be(SignalKind.JobFailed);
        signal.Reason.Should().Be("BackoffLimitExceeded");
        signal.Target.OwnerKind.Should().Be("CronJob");
        signal.Target.OwnerName.Should().Be("nightly-import");
    }

    [Fact]
    public void A_running_Job_produces_no_signal()
    {
        var job = new V1Job
        {
            Metadata = K8sFixtures.Meta("importer"),
            Status = new V1JobStatus { Active = 1 },
        };

        SignalMapper.FromJob(job, K8sFixtures.Cluster, K8sFixtures.Now).Should().BeNull();
    }

    [Fact]
    public void A_FailedScheduling_event_maps_to_Unschedulable()
    {
        var signal = SignalMapper.FromEvent(Event("Warning", "FailedScheduling", "0/1 nodes are available"), K8sFixtures.Cluster);

        signal!.Kind.Should().Be(SignalKind.Unschedulable);
        signal.Source.Should().Be(SignalSource.KubernetesWatch);
    }

    [Fact]
    public void Normal_events_are_ignored()
    {
        SignalMapper.FromEvent(Event("Normal", "Scheduled", "Successfully assigned"), K8sFixtures.Cluster)
            .Should().BeNull();
    }

    [Fact]
    public void Warning_events_with_no_runbook_are_ignored()
    {
        SignalMapper.FromEvent(Event("Warning", "SomeOperatorSpecificReason", "..."), K8sFixtures.Cluster)
            .Should().BeNull();
    }

    /// <summary>
    /// The kubelet reuses "Failed" for image pulls and for container creation alike, so it is
    /// the one event reason where the message has to be read.
    /// </summary>
    [Fact]
    public void A_generic_Failed_event_is_disambiguated_by_its_message()
    {
        SignalMapper.FromEvent(
            Event("Warning", "Failed", "Failed to pull image \"nope:1\": ErrImagePull"),
            K8sFixtures.Cluster)!
            .Kind.Should().Be(SignalKind.ImagePullBackOff);
    }

    [Fact]
    public void An_event_carries_its_own_occurrence_count()
    {
        var kubeEvent = Event("Warning", "BackOff", "Back-off restarting failed container");
        kubeEvent.Count = 214;

        SignalMapper.FromEvent(kubeEvent, K8sFixtures.Cluster)!.Count.Should().Be(214);
    }

    [Fact]
    public void An_alert_maps_to_an_Alertmanager_sourced_signal()
    {
        var signal = SignalMapper.FromAlert(
            "KubePodCrashLooping",
            new Dictionary<string, string>
            {
                ["alertname"] = "KubePodCrashLooping",
                ["namespace"] = "prod",
                ["pod"] = "api-7d4c9f8b6-x2k9p",
                ["deployment"] = "api",
                ["severity"] = "critical",
            },
            new Dictionary<string, string> { ["description"] = "pod is restarting" },
            K8sFixtures.Now.AddMinutes(-10),
            K8sFixtures.Now,
            K8sFixtures.Cluster);

        signal.Source.Should().Be(SignalSource.Alertmanager);
        signal.Kind.Should().Be(SignalKind.CrashLoopBackOff);
        signal.Severity.Should().Be(Severity.Critical);
        signal.Target.OwnerKind.Should().Be("Deployment");
        signal.Target.OwnerName.Should().Be("api");
        signal.Fingerprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void An_explicit_hephaisto_kind_label_overrides_the_alertname()
    {
        var signal = SignalMapper.FromAlert(
            "SomeoneElsesRuleName",
            new Dictionary<string, string>
            {
                ["alertname"] = "SomeoneElsesRuleName",
                ["hephaisto_kind"] = "PvcNearlyFull",
            },
            new Dictionary<string, string>(),
            K8sFixtures.Now,
            K8sFixtures.Now,
            K8sFixtures.Cluster);

        signal.Kind.Should().Be(SignalKind.PvcNearlyFull);
    }

    // ---------------------------------------------------------------------------------
    // A readiness probe failing once is a pod starting, not a pod flapping.
    // ---------------------------------------------------------------------------------
    //
    // Found on the v0.7.0 gate. This file already had a detector for ReadinessFlapping that
    // refuses to claim it below SignalThresholds.ReadinessFlapCount ready-transitions - and
    // three hundred lines below it, a second detector for the same kind that claimed it from a
    // single Unhealthy event. Two thresholds for one word: four, and one.
    //
    // The consequence was not subtle. Every pod that takes longer to start than its
    // initialDelaySeconds emits one of these, so every ordinary rollout opened an incident
    // asserting the workload was flapping - and because it opened FIRST, every later signal
    // correlated into an incident already carrying the wrong kind and therefore the wrong
    // runbook.

    [Fact]
    public void One_readiness_probe_failure_is_not_a_flap()
    {
        var signal = SignalMapper.FromEvent(
            EventWithCount("Warning", "Unhealthy", "Readiness probe failed: HTTP probe failed with statuscode: 503", count: 1),
            K8sFixtures.Cluster);

        signal.Should().BeNull(
            "a probe that has failed once is a pod that has not finished starting; claiming it "
            + "is flapping opens an incident on every ordinary rollout and hands the "
            + "investigation a runbook written for an intermittent fault");
    }

    [Fact]
    public void A_readiness_probe_failing_repeatedly_is_a_flap()
    {
        var signal = SignalMapper.FromEvent(
            EventWithCount("Warning", "Unhealthy", "Readiness probe failed: HTTP probe failed with statuscode: 503", count: 9),
            K8sFixtures.Cluster);

        signal.Should().NotBeNull();
        signal!.Kind.Should().Be(SignalKind.ReadinessFlapping);
    }

    [Fact]
    public void The_two_detectors_of_this_one_kind_agree_on_what_flapping_requires()
    {
        // The actual defect was that they disagreed, so the threshold is asserted rather than
        // just its effect. If somebody tunes one, this fails until they consider the other.
        var justBelow = SignalMapper.FromEvent(
            EventWithCount("Warning", "Unhealthy", "Readiness probe failed", count: new SignalThresholds().ReadinessFlapCount - 1),
            K8sFixtures.Cluster);

        var atThreshold = SignalMapper.FromEvent(
            EventWithCount("Warning", "Unhealthy", "Readiness probe failed", count: new SignalThresholds().ReadinessFlapCount),
            K8sFixtures.Cluster);

        justBelow.Should().BeNull();
        atThreshold.Should().NotBeNull();
    }

    [Fact]
    public void An_event_with_no_count_at_all_is_treated_as_one_occurrence()
    {
        // Count is nullable, and the newer Events API does not always populate it. Absent
        // evidence of repetition must not be read as evidence of repetition - the whole point
        // of the claim is that it happened more than once.
        var signal = SignalMapper.FromEvent(
            EventWithCount("Warning", "Unhealthy", "Readiness probe failed", count: null),
            K8sFixtures.Cluster);

        signal.Should().BeNull();
    }

    [Fact]
    public void A_liveness_probe_failure_is_still_not_this_signal()
    {
        // Guards the message match, not the count: liveness failures lead to restarts, which
        // CrashLoopBackOff and RestartStorm own.
        SignalMapper.FromEvent(
            EventWithCount("Warning", "Unhealthy", "Liveness probe failed: connection refused", count: 20),
            K8sFixtures.Cluster)
            .Should().BeNull();
    }

    private static Corev1Event EventWithCount(string type, string reason, string message, int? count)
    {
        var e = Event(type, reason, message);
        e.Count = count;
        return e;
    }

    private static Corev1Event Event(string type, string reason, string message) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"api.{reason}",
                NamespaceProperty = K8sFixtures.Namespace,
                Uid = $"uid-{reason}",
            },
            Type = type,
            Reason = reason,
            Message = message,
            LastTimestamp = K8sFixtures.Now.UtcDateTime,
            FirstTimestamp = K8sFixtures.Now.AddMinutes(-5).UtcDateTime,
            InvolvedObject = new V1ObjectReference
            {
                Kind = "Pod",
                Name = "api-7d4c9f8b6-x2k9p",
                NamespaceProperty = K8sFixtures.Namespace,
            },
        };
}
