using Hephaisto.Agent.Web;

namespace Hephaisto.Tests;

/// <summary>
/// What an alert's labels are allowed to become a node name.
/// </summary>
/// <remarks>
/// <para>
/// <c>ResolveTarget</c> read <c>node</c> and fell back to <c>instance</c>. <c>instance</c> is a
/// scrape target address - <c>10.244.0.6:8080</c> for anything scraped per-pod - so an alert
/// without a <c>node</c> label produced a <see cref="Hephaisto.Core.Domain.TargetRef"/> whose
/// node name was an IP and a port.
/// </para>
/// <para>
/// <b>That is not a cosmetic field.</b> <c>ClusterFactsGatherer.ReadNodeAsync</c> asks the API
/// server for a Node by that name, gets a 404, and throws; the gatherer converts any throw into
/// <c>ClusterFactsUnavailable</c>; and that is default-denied. So every action proposed on an
/// alert without a <c>node</c> label was refused - permanently, and with the reason "cluster
/// facts could not be read, so no action can be judged", which names neither the label nor the
/// node. It was found on the third cluster run of c13, where the planner proposed a correct
/// RestartPod four times out of four and the policy engine refused two of them. See
/// <c>docs/backlog.md</c> #92.
/// </para>
/// <para>
/// The Kubernetes watch path never had the fallback - <c>SignalMapper</c> reads <c>node</c> and
/// stops - so the two ingestion paths disagreed about what a node name is, and only one of them
/// could poison an incident.
/// </para>
/// </remarks>
public class AlertTargetNodeNameTests
{
    private static AlertmanagerWebhook Payload() => new() { Receiver = "hephaisto", Status = "firing" };

    private static AlertmanagerAlert Alert(params (string Key, string Value)[] labels) => new()
    {
        Status = "firing",
        Labels = labels.ToDictionary(l => l.Key, l => l.Value),
        Annotations = [],
        StartsAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void An_instance_label_is_never_a_node_name()
    {
        var signal = AlertmanagerEndpoints.ToSignal(
            Alert(
                ("alertname", "ChaosPodCrashLooping"),
                ("namespace", "hephaisto-chaos"),
                ("pod", "c13-wedged-lock-6778bccbd9-6s4wg"),
                ("instance", "10.244.0.6:8080")),
            Payload());

        signal.Target.NodeName.Should().BeNull(
            "instance is a scrape target address; a TargetRef carrying one denies every action "
            + "proposed on the incident");
    }

    [Fact]
    public void A_real_node_label_is_still_used()
    {
        var signal = AlertmanagerEndpoints.ToSignal(
            Alert(
                ("alertname", "ChaosPodCrashLooping"),
                ("namespace", "hephaisto-chaos"),
                ("pod", "c13-wedged-lock-6778bccbd9-6s4wg"),
                ("node", "hephaisto-e2e-control-plane"),
                ("instance", "10.244.0.6:8080")),
            Payload());

        signal.Target.NodeName.Should().Be("hephaisto-e2e-control-plane");
    }

    /// <summary>
    /// The rest of the target must be unaffected - this fix removes a fallback, and removing
    /// the wrong one would take the namespace repair of #33 with it.
    /// </summary>
    [Fact]
    public void The_namespace_fallbacks_are_untouched()
    {
        var signal = AlertmanagerEndpoints.ToSignal(
            Alert(
                ("alertname", "ChaosPodCrashLooping"),
                ("k8s_namespace_name", "hephaisto-chaos"),
                ("pod", "c13-wedged-lock-6778bccbd9-6s4wg"),
                ("instance", "10.244.0.6:8080")),
            Payload());

        signal.Target.Namespace.Should().Be("hephaisto-chaos");
        signal.Target.NodeName.Should().BeNull();
    }
}
