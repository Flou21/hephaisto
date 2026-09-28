using Hephaisto.Agent.Web;
using Hephaisto.Core.Classification;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;

namespace Hephaisto.Tests;

/// <summary>
/// Which alert an alert is: backlog #126, #129, #131, #132, #134 and #151.
/// </summary>
public sealed class AlertIdentityTests
{
    private const string Cluster = "studio-rancher-desktop";

    private static Dictionary<string, string> Labels(params (string Key, string Value)[] labels) =>
        labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);

    private static AlertmanagerAlert Alert(string status, params (string Key, string Value)[] labels) => new()
    {
        Status = status,
        Labels = Labels(labels),
        Annotations = [],
        StartsAt = DateTimeOffset.UtcNow,
    };

    private static Signal Map(params (string Key, string Value)[] labels) =>
        AlertmanagerEndpoints.ToSignal(Alert("firing", labels), new AlertmanagerWebhook());

    // --- AlertKey --------------------------------------------------------------------------

    [Fact]
    public void Two_pods_of_one_rule_are_two_alert_instances()
    {
        AlertIdentity.AlertKey(Labels(("alertname", "A"), ("pod", "web-1")))
            .Should().NotBe(AlertIdentity.AlertKey(Labels(("alertname", "A"), ("pod", "web-2"))));
    }

    [Fact]
    public void The_scrape_labels_do_not_change_which_alert_it_is()
    {
        AlertIdentity.AlertKey(Labels(("alertname", "A"), ("pod", "web-1"), ("instance", "10.0.0.1:8080"), ("prometheus_replica", "a")))
            .Should().Be(AlertIdentity.AlertKey(Labels(("alertname", "A"), ("pod", "web-1"), ("instance", "10.0.0.9:8080"), ("prometheus_replica", "b"))));
    }

    [Fact]
    public void Label_order_does_not_matter()
    {
        AlertIdentity.AlertKey(Labels(("b", "2"), ("a", "1")))
            .Should().Be(AlertIdentity.AlertKey(Labels(("a", "1"), ("b", "2"))));
    }

    [Fact]
    public void A_value_cannot_masquerade_as_another_label()
    {
        AlertIdentity.AlertKey(Labels(("a", "1;b=2")))
            .Should().NotBe(AlertIdentity.AlertKey(Labels(("a", "1"), ("b", "2"))));
    }

    // --- Label-only alerts (#132) ------------------------------------------------------------

    [Fact]
    public void Two_providers_of_one_rule_are_two_incidents()
    {
        var alpha = Map(("alertname", "TooFewArticles"), ("provider", "alpha"));
        var beta = Map(("alertname", "TooFewArticles"), ("provider", "beta"));

        SignalFingerprinter.Compute(alpha, Cluster).Should().NotBe(SignalFingerprinter.Compute(beta, Cluster));
        SignalFingerprinter.CorrelationKey(alpha, Cluster).Should().NotBe(SignalFingerprinter.CorrelationKey(beta, Cluster));
    }

    [Fact]
    public void A_warning_that_turns_critical_is_the_same_incident()
    {
        var warning = Map(("alertname", "TooFewArticles"), ("provider", "alpha"), ("severity", "warning"));
        var critical = Map(("alertname", "TooFewArticles"), ("provider", "alpha"), ("severity", "critical"));

        SignalFingerprinter.Compute(warning, Cluster).Should().Be(SignalFingerprinter.Compute(critical, Cluster));
    }

    [Fact]
    public void An_alert_about_no_object_is_a_pipeline_alert_whatever_its_name_says()
    {
        Map(("alertname", "ArticlesSlowRestartPending")).Kind.Should().Be(SignalKind.Pipeline);
    }

    [Fact]
    public void A_stated_kind_still_wins()
    {
        Map(("alertname", "Anything"), ("hephaisto_kind", "HighLatency")).Kind.Should().Be(SignalKind.HighLatency);
    }

    [Fact]
    public void The_distinguishing_labels_leave_out_what_every_series_shares()
    {
        AlertIdentity.Distinguishing(Labels(
                ("alertname", "A"), ("severity", "warning"), ("provider", "alpha"), ("feed", "articles"),
                ("instance", "10.0.0.1:9090"), ("job", "exporter")))
            .Should().Be("feed=articles, provider=alpha");
    }

    // --- Cluster (#131) -----------------------------------------------------------------------

    [Fact]
    public void The_cluster_label_is_read_into_the_target()
    {
        Map(("alertname", "A"), ("cluster", "eu"), ("namespace", "shop"), ("deployment", "api"))
            .Target.Cluster.Should().Be("eu");
    }

    [Fact]
    public void One_workload_in_two_clusters_is_two_incidents_and_two_correlation_keys()
    {
        var eu = Map(("alertname", "A"), ("cluster", "eu"), ("namespace", "shop"), ("deployment", "api"));
        var us = Map(("alertname", "A"), ("cluster", "us"), ("namespace", "shop"), ("deployment", "api"));

        SignalFingerprinter.Compute(eu, Cluster).Should().NotBe(SignalFingerprinter.Compute(us, Cluster));
        SignalFingerprinter.CorrelationKey(eu, Cluster).Should().NotBe(SignalFingerprinter.CorrelationKey(us, Cluster));
    }

    [Fact]
    public void No_cluster_label_means_the_agents_own()
    {
        var unlabelled = Map(("alertname", "A"), ("namespace", "shop"), ("deployment", "api"));
        var labelled = Map(("alertname", "A"), ("cluster", Cluster), ("namespace", "shop"), ("deployment", "api"));

        SignalFingerprinter.Compute(unlabelled, Cluster).Should().Be(SignalFingerprinter.Compute(labelled, Cluster));
        unlabelled.Target.IsForeignTo(Cluster).Should().BeFalse();
        Map(("alertname", "A"), ("cluster", "eu"), ("deployment", "api")).Target.IsForeignTo(Cluster).Should().BeTrue();
    }

    // --- kube-state-metrics (#126) ------------------------------------------------------------

    [Fact]
    public void A_kube_state_metrics_series_about_a_deployment_targets_the_deployment()
    {
        var signal = Map(
            ("alertname", "KubeDeploymentReplicasMismatch"), ("namespace", "shop"), ("deployment", "checkout"),
            ("job", "kube-state-metrics"), ("pod", "kube-state-metrics-7d9c8b-x2x4q"), ("container", "kube-state-metrics"));

        signal.Target.Kind.Should().Be("Deployment");
        signal.Target.Name.Should().Be("checkout");
    }

    [Fact]
    public void A_kube_state_metrics_series_about_a_pod_of_the_workload_keeps_the_pod()
    {
        var signal = Map(
            ("alertname", "KubePodCrashLooping"), ("namespace", "shop"), ("deployment", "checkout"),
            ("job", "kube-state-metrics"), ("pod", "checkout-5d7f-aaaaa"));

        signal.Target.Kind.Should().Be("Pod");
        signal.Target.OwnerName.Should().Be("checkout");
    }

    [Fact]
    public void Any_other_source_is_believed_about_its_pod()
    {
        Map(("alertname", "A"), ("namespace", "shop"), ("deployment", "checkout"), ("pod", "sidecar-x"))
            .Target.Kind.Should().Be("Pod");
    }

    // --- Status (#129) and the watchdog (#151) --------------------------------------------------

    [Fact]
    public void A_resolved_alert_says_so_and_keeps_its_alert_key()
    {
        var firing = AlertmanagerEndpoints.ToSignal(Alert("firing", ("alertname", "A"), ("pod", "p")), new AlertmanagerWebhook());
        var resolved = AlertmanagerEndpoints.ToSignal(Alert("resolved", ("alertname", "A"), ("pod", "p")), new AlertmanagerWebhook());

        firing.Status.Should().Be(SignalStatus.Firing);
        resolved.Status.Should().Be(SignalStatus.Resolved);
        resolved.AlertKey.Should().Be(firing.AlertKey).And.NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Watchdog", true)]
    [InlineData("watchdog", true)]
    [InlineData("ConsumerWatchdogStalled", false)]
    [InlineData("WatchdogMissing", false)]
    public void Only_the_watchdog_is_the_watchdog(string name, bool isWatchdog)
    {
        AlertmanagerEndpoints.IsWatchdog(Alert("firing", ("alertname", name))).Should().Be(isWatchdog);
    }

    // --- Word boundaries in the classifier (#134) ----------------------------------------------

    [Theory]
    [InlineData("KubePodCrashLooping", "crashloop", true)]
    [InlineData("KubeContainerOOMKilled", "oom", true)]
    [InlineData("BloomFilterFull", "oom", false)]
    [InlineData("TooFewArticlesSlow", "slo", false)]
    [InlineData("CheckoutSloBurn", "slo", true)]
    [InlineData("Http5xxErrorRate", "5xx", true)]
    [InlineData("KubePodNotReady", "notready", true)]
    [InlineData("crashloopbackoff", "crashloop", true)]
    [InlineData("disk_pvc_full", "pvc", true)]
    [InlineData("ArticlesPvcsomething", "pvc", false)]
    public void A_keyword_matches_a_word_not_a_run_of_letters(string name, string keyword, bool matches)
    {
        AlertClassifier.Mentions(name, keyword).Should().Be(matches);
    }

    [Fact]
    public void A_slow_feed_is_not_a_latency_fault()
    {
        AlertClassifier.Kind("FeedIngestSlow", new Dictionary<string, string>()).Should().Be(SignalKind.Unknown);
    }
}
