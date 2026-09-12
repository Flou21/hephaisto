using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using k8s;

using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.Observability;

/// <summary>
/// Postgres. The one dependency whose absence stops everything.
/// </summary>
/// <remarks>
/// There is no NotConfigured branch: the agent cannot start without a connection string, so a
/// row saying "not configured" here would describe a process that is not running.
/// </remarks>
public sealed class PostgresProbe(IServiceScopeFactory scopes, IClock clock) : IConnectionProbe
{
    public string Name => "postgres";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            return await db.Database.CanConnectAsync(ct)
                ? new ConnectionReport(Name, ConnectionState.Healthy, "Connected.", at)
                : new ConnectionReport(Name, ConnectionState.Unreachable, "Did not answer.", at);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionReport(Name, ConnectionState.Unreachable, Trim(ex.Message), at);
        }
    }

    internal static string Trim(string message) =>
        message.Length <= 200 ? message : message[..200] + "…";
}

/// <summary>
/// The Kubernetes API. A read, because reads are all the agent is granted in Observe.
/// </summary>
/// <remarks>
/// Listing one namespace rather than calling <c>/version</c>: the version endpoint answers for an
/// unauthenticated caller too, so it would report healthy on a cluster where the ServiceAccount's
/// RBAC had been removed - which is a failure this panel exists to show.
/// </remarks>
public sealed class KubernetesProbe(IKubernetes? api, IClock clock) : IConnectionProbe
{
    public string Name => "kubernetes";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;

        if (api is null)
        {
            return ConnectionReport.NotConfigured(
                Name, "No cluster client: the agent is running outside Kubernetes.", at);
        }

        try
        {
            var namespaces = await api.CoreV1.ListNamespaceAsync(limit: 1, cancellationToken: ct);

            return new ConnectionReport(
                Name,
                ConnectionState.Healthy,
                $"Reachable; list namespaces returned {namespaces.Items.Count}.",
                at);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionReport(Name, ConnectionState.Unreachable, PostgresProbe.Trim(ex.Message), at);
        }
    }
}

/// <summary>
/// grafana-mcp, reported by TOOL COVERAGE rather than by reachability.
/// </summary>
/// <remarks>
/// <para>
/// This is the row that earns the <see cref="ConnectionState.Degraded"/> state. A connected
/// grafana-mcp started without Tempo registers none of the four trace tools, and the agent then
/// cannot follow an exemplar into a trace - backlog #31, and the measured reason c10 spent
/// thirteen steps and sixteen tool calls and produced no finding. A binary up/down row would
/// have been green throughout.
/// </para>
/// <para>
/// The provider never throws and returns an empty list when unreachable, so zero tools against a
/// configured URL is the unreachable case.
/// </para>
/// </remarks>
public sealed class GrafanaMcpProbe(
    IGrafanaToolProvider tools,
    IOptionsMonitor<GrafanaOptions> options,
    IClock clock) : IConnectionProbe
{
    public string Name => "grafana-mcp";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;
        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.McpUrl))
        {
            return ConnectionReport.NotConfigured(
                Name,
                "Grafana:McpUrl is not set, so the agent investigates with Kubernetes reads only.",
                at);
        }

        try
        {
            var available = await tools.GetToolsAsync(ct);
            var wanted = o.AllowedTools.Count;

            if (available.Count == 0)
            {
                return new ConnectionReport(
                    Name, ConnectionState.Unreachable, $"No tools from {o.McpUrl}.", at);
            }

            var missing = o.AllowedTools
                .Where(name => !available.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            return missing.Length == 0
                ? new ConnectionReport(Name, ConnectionState.Healthy, $"All {wanted} allowlisted tools present.", at)
                : new ConnectionReport(
                    Name,
                    ConnectionState.Degraded,
                    $"{available.Count} of {wanted} tools; missing {string.Join(", ", missing)}.",
                    at);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionReport(Name, ConnectionState.Unreachable, PostgresProbe.Trim(ex.Message), at);
        }
    }
}

/// <summary>Grafana's own API, which the agent writes incident annotations to.</summary>
public sealed class GrafanaAnnotationProbe(
    HttpClient http,
    IOptionsMonitor<GrafanaOptions> options,
    IClock clock) : IConnectionProbe
{
    public string Name => "grafana-annotations";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;
        var o = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(o.Url))
        {
            return ConnectionReport.NotConfigured(Name, GrafanaAnnotator.Describe(o), at);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(o.Url), "api/health"));

            if (!string.IsNullOrWhiteSpace(o.AnnotationToken))
            {
                request.Headers.Authorization = new("Bearer", o.AnnotationToken);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await http.SendAsync(request, timeout.Token);

            return response.IsSuccessStatusCode
                ? new ConnectionReport(Name, ConnectionState.Healthy, $"{o.Url} answered.", at)
                : new ConnectionReport(
                    Name, ConnectionState.Unreachable, $"{o.Url} answered HTTP {(int)response.StatusCode}.", at);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionReport(Name, ConnectionState.Unreachable, PostgresProbe.Trim(ex.Message), at);
        }
    }
}

/// <summary>
/// The outbound channels, reported from configuration and never probed.
/// </summary>
/// <remarks>
/// <b>Deliberately not a live check.</b> The only way to test a webhook is to post to it, and the
/// endpoints behind these are Teams channels and pager integrations - a panel that verified them
/// by pinging would page somebody every refresh. So this row answers "is a route wired up", which
/// is the question that was actually unanswerable before: a channel that is off and a channel
/// that is broken both looked like nothing happening.
/// </remarks>
public sealed class NotificationChannelProbe(
    IEnumerable<INotificationChannel> channels,
    IClock clock) : IConnectionProbe
{
    public string Name => "notifications";

    public Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;
        var configured = channels.ToArray();

        var report = configured.Length == 0
            ? ConnectionReport.NotConfigured(
                Name, "No channel is configured, so nothing is delivered anywhere.", at)
            : new ConnectionReport(
                Name,
                ConnectionState.Healthy,

                // Describe() is contractually credential-free - a Teams Workflows URL carries its
                // bearer in the query string - so this renders that rather than the raw setting.
                string.Join(" ", configured.Select(c => c.Describe())),
                at);

        return Task.FromResult(report);
    }
}
