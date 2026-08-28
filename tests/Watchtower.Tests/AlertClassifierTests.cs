using Watchtower.Core.Classification;
using Watchtower.Core.Domain;

namespace Watchtower.Tests;

/// <summary>
/// These lock the table that both alert entry points now share. The reason it is shared is
/// the reason these matter: <see cref="SignalKind"/> selects the runbook, so if the webhook
/// path and the non-HTTP path ever disagree, an investigation silently gets the wrong
/// instructions depending on which door the alert came through.
/// </summary>
public class AlertClassifierTests
{
    private static readonly Dictionary<string, string> None = [];

    [Theory]
    // The upstream kube-prometheus names, which is what our PrometheusRule files ship.
    [InlineData("KubePodCrashLooping", SignalKind.CrashLoopBackOff)]
    [InlineData("KubeContainerWaiting", SignalKind.Unknown)]
    [InlineData("KubeJobFailed", SignalKind.JobFailed)]
    [InlineData("NodeMemoryPressure", SignalKind.NodePressure)]
    [InlineData("TargetDown", SignalKind.TargetDown)]
    // Names somebody else might plausibly write for the same conditions.
    [InlineData("PodCrashLoopBackOff", SignalKind.CrashLoopBackOff)]
    [InlineData("ContainerOOMKilled", SignalKind.OomKilled)]
    [InlineData("ChaosPodOOMKilled", SignalKind.OomKilled)]
    [InlineData("ImagePullFailure", SignalKind.ImagePullBackOff)]
    [InlineData("ErrImagePullDetected", SignalKind.ImagePullBackOff)]
    [InlineData("PodUnschedulable", SignalKind.Unschedulable)]
    [InlineData("ServiceHighErrorRate", SignalKind.HighErrorRate)]
    [InlineData("ServiceLatencyBudgetBurnFast", SignalKind.HighLatency)]
    [InlineData("PvcNearlyFull", SignalKind.PvcNearlyFull)]
    public void Classifies_alertnames_by_keyword(string alertName, SignalKind expected) =>
        AlertClassifier.Kind(alertName, None).Should().Be(expected);

    [Fact]
    public void Matching_is_case_insensitive() =>
        AlertClassifier.Kind("KUBEPODCRASHLOOPING", None).Should().Be(SignalKind.CrashLoopBackOff);

    [Fact]
    public void Unrecognised_alertname_is_Unknown_rather_than_a_guess() =>
        AlertClassifier.Kind("SomethingNobodyAnticipated", None).Should().Be(SignalKind.Unknown);

    [Fact]
    public void An_explicit_watchtower_kind_label_beats_the_alertname()
    {
        // The escape hatch for a rule whose name carries no usable keyword. It has to win,
        // or the label is decorative.
        var labels = new Dictionary<string, string>
        {
            [AlertClassifier.KindLabel] = nameof(SignalKind.ConfigError),
        };

        AlertClassifier.Kind("KubePodCrashLooping", labels).Should().Be(SignalKind.ConfigError);
    }

    [Fact]
    public void An_unparseable_kind_label_falls_back_to_the_alertname()
    {
        var labels = new Dictionary<string, string> { [AlertClassifier.KindLabel] = "NotAKind" };

        AlertClassifier.Kind("KubePodCrashLooping", labels).Should().Be(SignalKind.CrashLoopBackOff);
    }

    [Theory]
    [InlineData("critical", Severity.Critical)]
    [InlineData("page", Severity.Critical)]
    [InlineData("warning", Severity.Warning)]
    [InlineData("info", Severity.Info)]
    public void Severity_label_wins_when_present(string label, Severity expected) =>
        AlertClassifier
            .SeverityOf(new Dictionary<string, string> { ["severity"] = label }, SignalKind.Unknown)
            .Should().Be(expected);

    [Fact]
    public void An_unlabelled_alert_falls_back_to_the_kind_not_to_Info()
    {
        // Deliberate asymmetry: an unclassified alert that turns out to matter is worse than
        // one investigated for nothing, and in observe mode the latter costs a few cents.
        AlertClassifier.SeverityOf(None, SignalKind.OomKilled).Should().Be(Severity.Critical);
        AlertClassifier.SeverityOf(None, SignalKind.ReadinessFlapping).Should().Be(Severity.Warning);
    }

    [Fact]
    public void An_empty_severity_label_is_treated_as_absent() =>
        AlertClassifier
            .SeverityOf(new Dictionary<string, string> { ["severity"] = "  " }, SignalKind.TargetDown)
            .Should().Be(Severity.Critical);
}
