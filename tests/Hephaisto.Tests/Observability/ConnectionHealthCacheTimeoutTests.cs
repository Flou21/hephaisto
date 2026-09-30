using Microsoft.Extensions.Logging.Abstractions;
using Hephaisto.Agent.Observability;
using Hephaisto.Tests.Investigations;

namespace Hephaisto.Tests.Observability;

/// <summary>
/// Production, 2026-09-29: an OIDC probe's 5 s HTTP timeout during a cluster-wide network stall
/// surfaced as a TaskCanceledException, escaped the probe loop, and StopHost turned it into a
/// crash-looping agent. A probe that times out is a probe that is unreachable - nothing more.
/// </summary>
public sealed class ConnectionHealthCacheTimeoutTests
{
    private sealed class TimingOutProbe : IConnectionProbe
    {
        public string Name => "oidc";

        public Task<ConnectionReport> ProbeAsync(CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
    }

    [Fact]
    public async Task A_probe_that_times_out_is_unreachable_and_does_not_escape()
    {
        var cache = new ConnectionHealthCache([new TimingOutProbe()], new TestClock(), NullLogger<ConnectionHealthCache>.Instance);

        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        cache.Current.Should().ContainSingle().Which.State.Should().Be(ConnectionState.Unreachable);
    }

    [Fact]
    public async Task The_hosts_own_stop_still_stops_it()
    {
        var cache = new ConnectionHealthCache([new TimingOutProbe()], new TestClock(), NullLogger<ConnectionHealthCache>.Instance);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        var act = () => cache.RefreshAsync(stopped.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
