using System.Collections.Concurrent;

using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.Observability;

/// <summary>
/// Re-probes every dependency on a timer and serves the last answer to the status page.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this fixes is that the answers were computed once and discarded.</b>
/// <c>OutboundStartupReport</c> already asks every outbound dependency to describe itself and
/// logs the result at startup - and says in its own remarks why that is not enough: "the failure
/// mode of the whole feature is that nothing happens, and 'nothing happened' looks identical
/// whether it was never switched on or is broken". <c>RbacSelfCheck</c> fires forty access
/// reviews and only logs. The v0.7.0 missing-tool warning only logs. None of it survives to
/// anywhere a person looks.
/// </para>
/// <para>
/// <b>Cached rather than probed per request.</b> The status page polls, and several of these
/// probes make a network call - serving them live would turn one reader with a browser tab open
/// into a steady load against Grafana and the API server.
/// </para>
/// <para>
/// <b>Probed in parallel, and each one isolated.</b> A dependency that hangs must not delay the
/// others, and one that throws must not blank the panel: the panel is most needed exactly when
/// something is broken.
/// </para>
/// </remarks>
public sealed class ConnectionHealthCache(
    IEnumerable<IConnectionProbe> probes,
    IClock clock,
    ILogger<ConnectionHealthCache> logger) : BackgroundService
{
    /// <summary>
    /// Chosen against the status page's own refresh rather than against how fast a dependency
    /// fails: a row that is up to a minute stale is fine when it carries its own timestamp, and
    /// the alternative is probing far more often than anyone reads the answer.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, ConnectionReport> _reports = new(StringComparer.Ordinal);

    /// <summary>
    /// The last answer from each probe, in a stable order so the panel does not reshuffle itself
    /// between refreshes.
    /// </summary>
    public IReadOnlyList<ConnectionReport> Current =>
        [.. _reports.Values.OrderBy(r => r.Name, StringComparer.Ordinal)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs every probe once. Internal so a test can drive it without a host.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(probes.Select(p => SafeProbeAsync(p, ct)));

        foreach (var report in results)
        {
            _reports[report.Name] = report;
        }
    }

    private async Task<ConnectionReport> SafeProbeAsync(IConnectionProbe probe, CancellationToken ct)
    {
        try
        {
            return await probe.ProbeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe is contractually not supposed to throw. If one does, that is itself worth
            // showing rather than swallowing - and worth showing as Unreachable rather than as a
            // gap in the list, because a missing row reads as "fine".
            logger.LogWarning(ex, "Connection probe {Probe} threw; reporting it as unreachable.", probe.Name);

            return new ConnectionReport(
                probe.Name,
                ConnectionState.Unreachable,
                $"The probe itself failed: {PostgresProbe.Trim(ex.Message)}",
                clock.UtcNow);
        }
    }
}
