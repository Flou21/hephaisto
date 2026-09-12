using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using k8s;

using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Options;
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
/// <para>
/// Listing one namespace rather than calling <c>/version</c>: the version endpoint answers for an
/// unauthenticated caller too, so it would report healthy on a cluster where the ServiceAccount's
/// RBAC had been removed - which is a failure this panel exists to show.
/// </para>
/// <para>
/// <b>It takes the options and a provider rather than <c>IKubernetes</c> itself, and that is not
/// style.</b> When <c>Kubernetes:Enabled</c> is false the client is still registered - as a
/// factory that THROWS a sentence naming the setting, deliberately, so every call site fails the
/// same explanatory way. Injecting it here would resolve that factory while the container is
/// building the probe list and take the whole host down on startup in the demo and UI-only
/// configuration. Which it did, and only running the process found it: every unit test passed.
/// </para>
/// </remarks>
public sealed class KubernetesProbe(
    IServiceProvider services,
    IOptionsMonitor<KubernetesOptions> options,
    IClock clock) : IConnectionProbe
{
    public string Name => "kubernetes";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;

        if (!options.CurrentValue.Enabled)
        {
            return ConnectionReport.NotConfigured(
                Name,
                "Kubernetes:Enabled is false: this process has no cluster client, which is the "
                + "demo and UI-only configuration.",
                at);
        }

        try
        {
            var api = services.GetRequiredService<IKubernetes>();

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
/// The identity provider, probed by fetching the discovery document.
/// </summary>
/// <remarks>
/// <b>Why this row is worth a panel entry of its own.</b> Authentication fails CLOSED - if the IdP
/// is unreachable the console is unreachable, which is the behaviour that was explicitly asked for
/// and is the right one. But it means the failure presents as "Hephaisto is down", and the first
/// question an operator then has is whether Hephaisto or Keycloak is the thing that broke. Without
/// this row that question is answered by reading pod logs.
///
/// The discovery document, specifically, rather than a liveness endpoint on the IdP: it is the
/// exact resource the OIDC handler fetches, over the same network path, so a row that says Healthy
/// here means sign-in can actually resolve its signing keys. An IdP that is up but serving a realm
/// this deployment is not configured for would answer a health check and fail discovery.
///
/// <c>NotConfigured</c> when <c>Auth:Enabled</c> is false, which is the default and the state every
/// e2e run and every pre-v0.8.0 install is in. That is not a warning - it is the honest answer to
/// "is an IdP wired up", and the panel's four states exist so it does not have to be dressed up as
/// a failure.
/// </remarks>
public sealed class OidcProbe(
    HttpClient http,
    IOptionsMonitor<AuthOptions> options,
    IClock clock) : IConnectionProbe
{
    public string Name => "oidc";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;
        var o = options.CurrentValue;

        if (!o.Enabled)
        {
            return ConnectionReport.NotConfigured(
                Name, "Auth:Enabled is false; the console is open and actors are self-declared.", at);
        }

        // Enabled with no authority cannot start at all - AddHephaistoAuth throws on it - so
        // reaching this branch would mean the options changed under a running host.
        if (string.IsNullOrWhiteSpace(o.Authority))
        {
            return new ConnectionReport(
                Name, ConnectionState.Unreachable, "Auth:Enabled is true but Auth:Authority is empty.", at);
        }

        try
        {
            // Built the way the handler builds it. `new Uri(base, relative)` would drop a realm
            // path segment when the authority has no trailing slash, and point at the host root.
            var discovery = new Uri($"{o.Authority.TrimEnd('/')}/.well-known/openid-configuration");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await http.GetAsync(discovery, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionReport(
                    Name,
                    ConnectionState.Unreachable,
                    $"{discovery} answered HTTP {(int)response.StatusCode}.",
                    at);
            }

            // A 200 that is not a discovery document is the reverse-proxy-error-page case, and it
            // would otherwise read as Healthy while sign-in fails on a parse error.
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            return body.Contains("\"jwks_uri\"", StringComparison.Ordinal)
                ? new ConnectionReport(Name, ConnectionState.Healthy, $"{o.Authority} published its keys.", at)
                : new ConnectionReport(
                    Name,
                    ConnectionState.Degraded,
                    $"{discovery} answered 200 but published no jwks_uri.",
                    at);
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
