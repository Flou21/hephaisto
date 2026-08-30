using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Observability;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// Says, once at startup, what this process can and cannot send outward.
/// </summary>
/// <remarks>
/// <para>
/// Everything outbound in this codebase degrades silently when it is not configured, which is the
/// right behaviour per delivery and a bad one overall: the failure mode of the whole feature is
/// that nothing happens, and "nothing happened" looks identical whether it was never switched on
/// or is broken.
/// </para>
/// <para>
/// <b>It also fixes a claim that was not true.</b> <c>GrafanaAnnotator</c>'s own remarks say the
/// absence "is reported once at startup by <c>GrafanaAnnotator.Describe</c>" - and nothing
/// anywhere called that method. A one-line startup report that was documented and never wired is
/// the same class of defect as backlog #3 and #14, and it is now the caller.
/// </para>
/// </remarks>
public sealed class OutboundStartupReport(
    IServiceScopeFactory scopes,
    IOptionsMonitor<NotificationOptions> notifications,
    IOptionsMonitor<GrafanaOptions> grafana,
    IAlertSilencer silencer,
    ILogger<OutboundStartupReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("{Status}", GrafanaAnnotator.Describe(grafana.CurrentValue));

        // Says whether SilenceAlert can work at all. Without it the action is refused with
        // "unsupported", which reads like a missing capability rather than a missing setting.
        logger.LogInformation("{Status}", silencer.Describe());

        using var scope = scopes.CreateScope();

        var channels = scope.ServiceProvider.GetServices<INotificationChannel>().ToList();
        var o = notifications.CurrentValue;

        foreach (var channel in channels)
        {
            logger.LogInformation("{Status}", channel.Describe());
        }

        if (o.Routes.Count == 0)
        {
            // Not a warning. Shipping unable to notify is the same deliberate default as an
            // empty AllowedNamespaces and mode: Observe, and warning about it on every start
            // would train people to ignore this log line on exactly the installs that chose it.
            logger.LogInformation(
                "Notifications are OFF: no routes are configured, so no incident will reach anybody "
                    + "outside this process.");

            return Task.CompletedTask;
        }

        var registered = channels.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphaned = o.Routes
            .Select(r => r.Channel)
            .Where(c => !registered.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orphaned.Count > 0)
        {
            // Startup validation refuses this, so reaching here means the routing table was
            // hot-reloaded into it. Loud, because the table looks correct and delivers nothing.
            logger.LogError(
                "Notification routes name channels that are not registered: {Channels}. "
                    + "Anything routed to them will fail rather than reach a person.",
                string.Join(", ", orphaned));
        }

        logger.LogInformation(
            "Notifications are ON: {Routes} route(s) over {Channels}.",
            o.Routes.Count,
            string.Join(", ", registered));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
