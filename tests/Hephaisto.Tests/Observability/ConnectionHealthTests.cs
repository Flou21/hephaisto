using Hephaisto.Agent.Observability;
using Hephaisto.Core.Abstractions;
using Hephaisto.Tests.TestData;

using Microsoft.Extensions.Logging.Abstractions;

namespace Hephaisto.Tests.Observability;

/// <summary>
/// The connections panel's cache (#111).
/// </summary>
/// <remarks>
/// The probes themselves mostly make network calls and are covered where that is possible; what
/// is worth pinning without a network is the cache's contract, because every clause of it exists
/// to stop the panel lying. A panel that blanks when a dependency breaks, or drops a row, or
/// reshuffles between refreshes, is worse than no panel - it is most needed at exactly the moment
/// something is broken.
/// </remarks>
public sealed class ConnectionHealthTests
{
    private static ConnectionHealthCache Cache(params IConnectionProbe[] probes) =>
        new(probes, Given.Clock(), NullLogger<ConnectionHealthCache>.Instance);

    [Fact]
    public async Task Every_probe_gets_a_row()
    {
        var cache = Cache(Stub("postgres", ConnectionState.Healthy), Stub("kubernetes", ConnectionState.Healthy));

        await cache.RefreshAsync(CancellationToken.None);

        cache.Current.Select(r => r.Name).Should().BeEquivalentTo(["kubernetes", "postgres"]);
    }

    /// <summary>
    /// A throwing probe becomes an Unreachable row, never a missing one.
    /// </summary>
    /// <remarks>
    /// A missing row reads as "fine" - the reader sees no red and moves on. That is the failure
    /// this whole panel exists to prevent, so it must not be the panel's own failure mode.
    /// </remarks>
    [Fact]
    public async Task A_probe_that_throws_is_reported_rather_than_dropped()
    {
        var cache = Cache(Throwing("grafana-mcp"), Stub("postgres", ConnectionState.Healthy));

        await cache.RefreshAsync(CancellationToken.None);

        cache.Current.Should().HaveCount(2);

        var failed = cache.Current.Single(r => r.Name == "grafana-mcp");
        failed.State.Should().Be(ConnectionState.Unreachable);
        failed.Detail.Should().Contain("probe itself failed");
    }

    /// <summary>One broken dependency must not take the healthy rows with it.</summary>
    [Fact]
    public async Task A_throwing_probe_does_not_blank_the_others()
    {
        var cache = Cache(Throwing("grafana-mcp"), Stub("postgres", ConnectionState.Healthy));

        await cache.RefreshAsync(CancellationToken.None);

        cache.Current.Single(r => r.Name == "postgres").State.Should().Be(ConnectionState.Healthy);
    }

    /// <summary>
    /// Stable order, so the panel does not reshuffle itself under a reader between refreshes.
    /// </summary>
    [Fact]
    public async Task Rows_come_back_in_a_stable_order()
    {
        var cache = Cache(
            Stub("postgres", ConnectionState.Healthy),
            Stub("grafana-mcp", ConnectionState.Degraded),
            Stub("kubernetes", ConnectionState.Healthy));

        await cache.RefreshAsync(CancellationToken.None);
        var first = cache.Current.Select(r => r.Name).ToArray();

        await cache.RefreshAsync(CancellationToken.None);

        cache.Current.Select(r => r.Name).Should().Equal(first);
        first.Should().BeInAscendingOrder();
    }

    /// <summary>A later sweep replaces the row rather than adding a second one.</summary>
    [Fact]
    public async Task Refreshing_replaces_the_previous_answer()
    {
        var probe = new Toggling("grafana-mcp");
        var cache = Cache(probe);

        await cache.RefreshAsync(CancellationToken.None);
        cache.Current.Single().State.Should().Be(ConnectionState.Healthy);

        await cache.RefreshAsync(CancellationToken.None);

        cache.Current.Should().ContainSingle()
            .Which.State.Should().Be(ConnectionState.Unreachable);
    }

    /// <summary>Before the first sweep the panel is empty rather than falsely green.</summary>
    [Fact]
    public void Nothing_is_reported_before_the_first_sweep() =>
        Cache(Stub("postgres", ConnectionState.Healthy)).Current.Should().BeEmpty();

    // ----------------------------------------------------------------------------------

    private static IConnectionProbe Stub(string name, ConnectionState state) =>
        new StubProbe(name, state);

    private static IConnectionProbe Throwing(string name) => new ThrowingProbe(name);

    private sealed class StubProbe(string name, ConnectionState state) : IConnectionProbe
    {
        public string Name => name;

        public Task<ConnectionReport> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ConnectionReport(name, state, "stub", DateTimeOffset.UnixEpoch));
    }

    private sealed class ThrowingProbe(string name) : IConnectionProbe
    {
        public string Name => name;

        public Task<ConnectionReport> ProbeAsync(CancellationToken ct) =>
            throw new InvalidOperationException("the transport exploded");
    }

    private sealed class Toggling(string name) : IConnectionProbe
    {
        private int _calls;

        public string Name => name;

        public Task<ConnectionReport> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ConnectionReport(
                name,
                _calls++ == 0 ? ConnectionState.Healthy : ConnectionState.Unreachable,
                "stub",
                DateTimeOffset.UnixEpoch));
    }
}
