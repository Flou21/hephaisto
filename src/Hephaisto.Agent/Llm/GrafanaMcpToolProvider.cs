using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.Llm;

public sealed class GrafanaOptions
{
    public const string SectionName = "Grafana";

    /// <summary>The grafana-mcp streamable-http endpoint, e.g. <c>http://grafana-mcp:8000/mcp</c>.</summary>
    public string? McpUrl { get; set; }

    /// <summary>A Grafana service-account token. Read-only in the Grafana org, by convention.</summary>
    public string? ServiceAccountToken { get; set; }

    /// <summary>
    /// Grafana itself, e.g. <c>http://grafana.hephaisto-obs</c>. Only used for annotations.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="McpUrl"/> because they are different services: grafana-mcp
    /// speaks MCP and is what the model queries through, while annotations go to Grafana's own
    /// HTTP API. Unset disables annotation and the agent says so once, at startup.
    /// </remarks>
    public string? Url { get; set; }

    /// <summary>
    /// A Grafana token that may WRITE annotations. Deliberately not
    /// <see cref="ServiceAccountToken"/>.
    /// </summary>
    /// <remarks>
    /// Every other Grafana credential in this system is read-only on purpose, and reusing one
    /// here would either fail with "Permission denied" on every transition or quietly push the
    /// read-only convention into a role that can write. Keeping it a separate value makes the
    /// one credential that needs write privileges visible as such.
    /// </remarks>
    public string? AnnotationToken { get; set; }

    /// <summary>
    /// Short on purpose: annotation happens on the ingest path, and a Grafana that accepts
    /// connections but never replies must not hold an incident behind it.
    /// </summary>
    public TimeSpan AnnotationTimeout { get; set; } = TimeSpan.FromSeconds(5);

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
    /// <para>
    /// grafana-mcp exposes fifty-odd tools. Handing all of them over costs input tokens on
    /// every single turn and - the part that actually hurts - measurably degrades tool
    /// selection, because the model has to discriminate between a dozen near-synonyms before
    /// it can start investigating. This list is the set that answers the questions the
    /// runbooks actually ask.
    /// </para>
    /// <para>
    /// <b>Every name here was read from <c>tools/list</c> of mcp-grafana 2.0.1</b> (chart
    /// grafana-mcp 0.27.1, started as <c>infra/observability/grafana-mcp.values.yaml</c> starts
    /// it), on 2026-10-08. That is the only way a name gets onto this list. A name the server
    /// does not offer is not an error anywhere: the tool is simply not handed to the model, the
    /// status row turns Degraded, and the investigation concludes without it. Until that day the
    /// list carried four Tempo names no server this repo has met ever offered, and
    /// <c>grafana_api_request</c>; production's server offered none of the five, and every
    /// investigation there ran without traces.
    /// </para>
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

        // Traces, in the order a runbook uses them: find the failing or slow requests with a
        // TraceQL query, read one of them span by span, and - when the query matched nothing -
        // find out what the attributes are called here and which values they hold. All four
        // take `datasourceUid`, so an install should name its Tempo in
        // Investigation:Environment:DatasourceUids (chart: grafanaMcp.datasourceUids.tempo).
        //
        // Three more exist on the server and are left out on purpose. `get_tempo_traceql_docs`
        // is a manual, not a question a runbook asks: the runbooks carry the queries they
        // need, and its declaration would be paid for on every turn of every investigation,
        // including the ones without a trace in them. `query_tempo_metrics` is the near-synonym
        // this list exists to keep away - span metrics are in Prometheus already, under names
        // the runbooks give. `diff_tempo_traces` answers nothing a runbook asks.
        "search_tempo_traces",
        "get_tempo_trace",
        "list_tempo_attribute_names",
        "list_tempo_attribute_values",

        // The alert's own rule: its expression, its `for:`, its annotations. See
        // AlertRulesCaveat below for the one argument that decides whether it answers.
        //
        // Until 2026-10-08 this entry was `grafana_api_request`, the server's raw door to
        // Grafana's HTTP API and then the only way to the rules. It still exists, in the
        // category `api`, and production starts its server with --disable-api: any verb against
        // any endpoint undoes every other restriction there. Do not put it back.
        "alerting_rules_read",
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
    ILoggerFactory loggerFactory) : IGrafanaToolProvider, IAsyncDisposable
{
    /// <summary>
    /// Surfaced in the environment card because it is a trap that costs a whole
    /// investigation: mcp-grafana's <c>alerting_rules_read</c> answers for
    /// <b>Grafana-managed</b> rules unless it is given a datasource. Hephaisto's rules are
    /// PrometheusRule CRs evaluated by Prometheus itself, so a call without one returns
    /// nothing - which reads as "there are no alert rules" rather than "you asked the wrong
    /// index".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against mcp-grafana 2.0.1 and this repo's dev Grafana on 2026-10-08:
    /// <c>{"operation":"list"}</c> returns the text <c>null</c>; with
    /// <c>"datasource_uid":"prometheus"</c> it returns all 37 rules of the stack, 32 kB, each
    /// with its expression, <c>for</c>, labels, annotations and state. <c>search_rule_name</c>
    /// and <c>label_selectors</c> narrow that list (one rule, 1.1 kB); <c>rule_group</c> and
    /// <c>states</c> were accepted and changed nothing for datasource rules, so they are not
    /// recommended here.
    /// </para>
    /// <para>
    /// Before that server the same trap had another name, <c>list_alert_rules</c>, and its way
    /// out was <c>grafana_api_request</c> against Prometheus's rules API through Grafana's
    /// proxy. Neither is on the list any more: the first no longer exists and the second is
    /// switched off where it matters. See <see cref="GrafanaOptions.AllowedTools"/>.
    /// </para>
    /// </remarks>
    public const string AlertRulesCaveat =
        "`alerting_rules_read` answers for Grafana-managed rules unless you name a datasource, "
        + "and will come back EMPTY (`null`) here: our rules are PrometheusRule custom resources "
        + "evaluated by Prometheus. To read them, pass `datasource_uid` with the uid of the "
        + "Prometheus datasource, `operation` `list`, and the alert's name in `search_rule_name` "
        + "- the whole list is long. The rule's expression is in `query`. An empty answer "
        + "without `datasource_uid` is not evidence that no alert exists.";

    private readonly ILogger<GrafanaMcpToolProvider> _logger =
        loggerFactory.CreateLogger<GrafanaMcpToolProvider>();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _unconfiguredWarningLogged;

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
            // Say it once, loudly. This branch returns exactly what an unreachable
            // grafana-mcp returns - an empty tool list - and the investigation then proceeds
            // Kubernetes-only, which looks like a working agent. The two cases are not the
            // same: "your metrics backend is down" is an incident, "you never configured a
            // URL" is a deployment mistake that no alert will ever fire for. Silence here
            // hid a misnamed env var for weeks.
            if (Interlocked.Exchange(ref _unconfiguredWarningLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Grafana:McpUrl is not configured, so the agent has NO metrics, logs or "
                    + "trace tools and every investigation will be Kubernetes-only. Set "
                    + "Grafana__McpUrl (and Grafana__ServiceAccountToken) if this is not "
                    + "deliberate.");
            }

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

                // Driven from AllowedTools, not from the server's list, so the surviving order
                // is the one written here rather than whatever grafana-mcp happened to return.
                // The option is documented as "in order" and until 2026-08-30 the code filtered
                // instead (backlog #17's neighbour, #35), which made the ordering claim false in
                // about the same number of lines it takes to make it true.
                //
                // Whether tool order influences selection at all is an UNVALIDATED hypothesis -
                // nothing here measures it. That is the reason to make the code match the
                // comment rather than to delete the comment: the eval harness exists now and
                // can settle it, and an experiment cannot be run against a lever that does not
                // exist.
                var byName = tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

                var allowed = o.AllowedTools
                    .Where(byName.ContainsKey)
                    .Select(name => (AIFunction)byName[name])
                    .ToArray();

                var missing = o.AllowedTools
                    .Where(name => !byName.ContainsKey(name))
                    .ToArray();

                if (missing.Length > 0)
                {
                    // WARNING, not Information, and it names the capability rather than only
                    // the tool names. See DescribeLostCapabilities: an absent tool is a silent
                    // amputation of an investigation, and at Information it read as routine
                    // startup noise for four releases while c10 could not do the one thing it
                    // exists to prove.
                    //
                    // Still not an error: mcp-grafana's tool set varies with which datasources
                    // and feature flags the server was started with, so this is a statement
                    // about that server's configuration, not a fault here.
                    _logger.LogWarning(
                        "grafana-mcp offers {Available} tools but {Missing} allowlisted tools are "
                        + "absent, so this agent cannot {LostCapabilities}. Any investigation that "
                        + "needed them will conclude without them rather than fail, and the two "
                        + "are indistinguishable from the outside. Absent tools: {Names}",
                        tools.Count,
                        missing.Length,
                        DescribeLostCapabilities(missing),
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

    /// <summary>
    /// Names what the model can no longer do, rather than only which tool names went missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tool names alone are not actionable by the person reading the log. Nobody scanning
    /// startup output knows that <c>search_tempo_traces</c> is the second hop of the five-hop
    /// correlation the c10 fixture exists to prove; they know that traces are missing, if you
    /// tell them that traces are missing.
    /// </para>
    /// <para>
    /// This is backlog #31's cheap half. Until 2026-10-08 the allowlist named four Tempo tools
    /// that neither this repo's grafana-mcp nor, after its upgrade, production's ever
    /// registered, so c10 - the fixture whose own header calls it "THE IMPORTANT ONE", built to
    /// prove alert to exemplar to trace to log to cause - could not reach hops two, three and
    /// four. Recording it spent thirteen steps and sixteen tool calls and produced no primary
    /// finding, the only fixture of eight to produce none, and every replay of that recording
    /// still scores NoFinding: a cassette holds the tools of the day it was made. The line that
    /// would have explained all of that was logged at Information and read like routine
    /// startup noise. The names are the server's now; the message stays, because the next
    /// rename will look the same.
    /// </para>
    /// <para>
    /// Matching is by substring on the tool name because the allowlist is grouped by backend
    /// and the names carry the backend in them. A tool that matches nothing falls into the
    /// generic clause rather than being dropped, so a new allowlist entry cannot silently
    /// produce a warning that names no capability at all.
    /// </para>
    /// </remarks>
    internal static string DescribeLostCapabilities(IReadOnlyCollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);

        // Ordered by how much the investigation loses, not alphabetically.
        (string Fragment, string Capability)[] families =
        [
            ("tempo", "follow an exemplar into a trace, which is how a metric is tied to the request that caused it"),
            ("loki", "read logs"),
            ("prometheus", "query metrics"),
            ("alert", "read its own alert rules"),
            ("dashboard", "read the queries behind a dashboard panel"),
            ("datasources", "discover which datasources exist"),
            ("deeplink", "hand a human a link back into Grafana"),
        ];

        var lost = families
            .Where(f => missing.Any(n => n.Contains(f.Fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Capability)
            .ToList();

        var unrecognised = missing
            .Where(n => !families.Any(f => n.Contains(f.Fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unrecognised.Count > 0)
        {
            lost.Add($"use {string.Join(", ", unrecognised)}");
        }

        return lost.Count > 0 ? string.Join("; nor ", lost) : "use some allowlisted capability";
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
