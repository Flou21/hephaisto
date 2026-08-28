using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Watchtower.Core.Abstractions;

namespace Watchtower.Agent.Llm;

public sealed class GrafanaOptions
{
    public const string SectionName = "Grafana";

    /// <summary>The grafana-mcp streamable-http endpoint, e.g. <c>http://grafana-mcp:8000/mcp</c>.</summary>
    public string? McpUrl { get; set; }

    /// <summary>A Grafana service-account token. Read-only in the Grafana org, by convention.</summary>
    public string? ServiceAccountToken { get; set; }

    /// <summary>
    /// How long a tool list is trusted. Short enough that a grafana-mcp restart with a
    /// different tool set is picked up within an incident, long enough that a burst of
    /// incidents does not re-list on every one.
    /// </summary>
    public TimeSpan ToolCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The tools actually exposed to the model, in order. Everything grafana-mcp offers
    /// beyond this list is dropped.
    /// </summary>
    /// <remarks>
    /// grafana-mcp exposes fifty-odd tools. Handing all of them over costs input tokens on
    /// every single turn and - the part that actually hurts - measurably degrades tool
    /// selection, because the model has to discriminate between a dozen near-synonyms before
    /// it can start investigating. This list is the set that answers the questions the
    /// runbooks actually ask.
    /// </remarks>
    public List<string> AllowedTools { get; set; } =
    [
        "query_prometheus",
        "query_prometheus_histogram",
        "list_prometheus_metric_names",
        "list_prometheus_label_values",
        "query_loki_logs",
        "query_loki_stats",
        "query_loki_patterns",
        "list_loki_label_names",
        "list_loki_label_values",
        "list_datasources",
        "search_dashboards",
        "get_dashboard_panel_queries",
        "generate_deeplink",
        "query_tempo_traces",
        "query_tempo_traceql",
        "list_tempo_tag_names",
        "list_tempo_tag_values",

        // The escape hatch, and the only way to read our alert rules at all - see
        // AlertRulesCaveat below.
        "grafana_api_request",
    ];
}

/// <summary>
/// Connects to grafana-mcp over streamable-http and hands the allowlisted tools to the model.
/// </summary>
/// <remarks>
/// <para>
/// <c>McpClientTool</c> derives from <see cref="AIFunction"/>, so the tools this returns go
/// straight into <c>ChatOptions.Tools</c>. There is deliberately no adapter layer: one would
/// be a second place for the schema to drift from what the server declares.
/// </para>
/// <para>
/// <b>Fails open to Kubernetes-only investigation.</b> If grafana-mcp is unreachable this
/// returns an empty list and logs, rather than throwing. The reasoning is about which failure
/// is worse: observability being down is one of the incidents this agent exists to
/// investigate, and an agent that refuses to look at a cluster because its metrics backend is
/// the thing that broke has failed at precisely the moment it was needed. A Kubernetes-only
/// investigation is degraded, not useless - events, describes and previous-container logs
/// diagnose most of the chaos fixtures on their own.
/// </para>
/// </remarks>
public sealed class GrafanaMcpToolProvider(
    IOptionsMonitor<GrafanaOptions> options,
    IClock clock,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    /// <summary>
    /// Surfaced in the environment card because it is a trap that costs a whole
    /// investigation: mcp-grafana's <c>list_alert_rules</c> returns <b>Grafana-managed</b>
    /// rules only. Watchtower's rules are PrometheusRule CRs evaluated by Prometheus itself,
    /// so that tool returns an empty list - which reads as "there are no alert rules" rather
    /// than "you asked the wrong index".
    /// </summary>
    public const string AlertRulesCaveat =
        "`list_alert_rules` returns Grafana-managed rules only and will come back EMPTY here: "
        + "our rules are PrometheusRule custom resources evaluated by Prometheus. To read them, "
        + "call `grafana_api_request` with path "
        + "`/api/datasources/proxy/uid/{prometheusUid}/api/v1/rules` (or "
        + "`/api/prometheus/{prometheusUid}/api/v1/rules`). An empty `list_alert_rules` is not "
        + "evidence that no alert exists.";

    private readonly ILogger<GrafanaMcpToolProvider> _logger =
        loggerFactory.CreateLogger<GrafanaMcpToolProvider>();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpClient? _client;
    private IReadOnlyList<AIFunction> _cached = [];
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    /// <summary>True once a connection attempt has succeeded at least once this process.</summary>
    public bool Connected => _client is not null;

    public async Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.McpUrl))
        {
            return [];
        }

        if (clock.UtcNow < _cachedUntil)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (clock.UtcNow < _cachedUntil)
            {
                return _cached;
            }

            _cached = await ListAsync(o, ct).ConfigureAwait(false);
            _cachedUntil = clock.UtcNow + o.ToolCacheDuration;

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AIFunction>> ListAsync(GrafanaOptions o, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var client = _client ??= await ConnectAsync(o, ct).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);

                var allowed = tools
                    .Where(t => o.AllowedTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                    .Cast<AIFunction>()
                    .ToArray();

                var missing = o.AllowedTools
                    .Where(name => !tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                if (missing.Length > 0)
                {
                    // Not an error: mcp-grafana's tool set varies with which datasources and
                    // feature flags the server was started with. Worth saying out loud once
                    // per cache window, because "the model never queried traces" and "the
                    // trace tools were never offered" look identical from the outside.
                    _logger.LogInformation(
                        "grafana-mcp offers {Available} tools; {Missing} allowlisted tools are absent: {Names}",
                        tools.Count,
                        missing.Length,
                        string.Join(", ", missing));
                }

                _logger.LogDebug("grafana-mcp: exposing {Count} of {Total} tools", allowed.Length, tools.Count);

                return allowed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // First failure is assumed to be a stale session - grafana-mcp restarts, the
                // session id it gave us is gone, and every call 404s until we reconnect.
                // Drop the client and try once more before giving up.
                await DisposeClientAsync().ConfigureAwait(false);

                if (attempt == 1)
                {
                    _logger.LogWarning(
                        ex,
                        "grafana-mcp at {Url} is unreachable; continuing with Kubernetes-only tools. "
                        + "Metrics, logs and traces will be unavailable to this investigation.",
                        o.McpUrl);

                    return [];
                }
            }
        }

        return [];
    }

    private async Task<McpClient> ConnectAsync(GrafanaOptions o, CancellationToken ct)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(o.McpUrl!),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "grafana-mcp",
            ConnectionTimeout = o.ConnectTimeout,
        };

        if (!string.IsNullOrWhiteSpace(o.ServiceAccountToken))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {o.ServiceAccountToken}",
            };
        }

        var transport = new HttpClientTransport(transportOptions, loggerFactory);

        return await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async ValueTask DisposeClientAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);

        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disposing the faulted grafana-mcp client threw; ignoring");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
