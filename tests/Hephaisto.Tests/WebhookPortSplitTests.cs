using Hephaisto.Agent.Options;

namespace Hephaisto.Tests;

/// <summary>
/// When the webhook gets its own port, and when it deliberately does not (#108).
/// </summary>
/// <remarks>
/// <para>
/// The routing itself is asserted by <c>charts/hephaisto/ci/negative-tests.sh</c> against a
/// rendered chart, and end to end by the e2e suite. What is worth pinning here is the predicate
/// that decides whether to split at all, because both of its failure directions are silent.
/// </para>
/// <para>
/// Splitting when nothing listens on the second port makes every alert vanish - the endpoint
/// simply stops matching, and Alertmanager's deliveries 404 into a log nobody reads. Not
/// splitting when the deployment expected it leaves the console answering on the port the
/// NetworkPolicy is guarding, which is the hole the split exists to close.
/// </para>
/// </remarks>
public sealed class WebhookPortSplitTests
{
    /// <summary>
    /// Zero means "one port", which is the previous behaviour and the right default.
    /// </summary>
    /// <remarks>
    /// A developer running <c>dotnet run</c>, the eval harness, and any install that has not set
    /// the value all bind one address. A split that happened anyway would make the webhook
    /// unreachable on every one of them.
    /// </remarks>
    [Fact]
    public void Zero_keeps_everything_on_one_port() =>
        new WebOptions { WebhookPort = 0 }.WebhookPortIsSeparate.Should().BeFalse();

    [Fact]
    public void A_distinct_port_splits() =>
        new WebOptions { WebhookPort = 8081, MainPort = 8080 }.WebhookPortIsSeparate.Should().BeTrue();

    /// <summary>
    /// The same port twice is not a split, and must not be treated as one.
    /// </summary>
    /// <remarks>
    /// Otherwise the two host constraints contradict each other on one listener - webhooks
    /// requiring <c>*:8080</c> and the console requiring <c>*:8080</c> is fine, but the intent
    /// was clearly a single port and reporting a split would make the chart's own assertions
    /// pass while describing something untrue.
    /// </remarks>
    [Fact]
    public void The_same_port_twice_is_not_a_split() =>
        new WebOptions { WebhookPort = 8080, MainPort = 8080 }.WebhookPortIsSeparate.Should().BeFalse();

    /// <summary>The default is the safe one: no split unless asked.</summary>
    [Fact]
    public void The_default_does_not_split() =>
        new WebOptions().WebhookPortIsSeparate.Should().BeFalse();
}
