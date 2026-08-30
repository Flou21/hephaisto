using System.Globalization;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// The links a message carries, built at render rather than stored.
/// </summary>
/// <remarks>
/// Derived rather than frozen into the snapshot on purpose: a base URL that turns out to be
/// wrong - and it is the one setting the pod cannot check for itself - is then fixed by editing
/// a value, not by re-queuing every row already waiting behind a failed endpoint.
/// </remarks>
public static class NotificationLinks
{
    /// <summary>The incident in Hephaisto's own console, which is where approval happens.</summary>
    public static string? Incident(string? baseUrl, Guid? incidentId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || incidentId is not { } id)
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/incidents/{id}";
    }

    /// <summary>
    /// Grafana, scoped to the hour around the event.
    /// </summary>
    /// <remarks>
    /// Built here rather than through grafana-mcp's <c>generate_deeplink</c>: that is a tool for
    /// the model, and putting an MCP round trip on the delivery path would make a notification
    /// depend on a service that this one is quite possibly being sent because of.
    /// </remarks>
    public static string? Grafana(string? grafanaUrl, NotificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(grafanaUrl))
        {
            return null;
        }

        var from = snapshot.At.AddMinutes(-30).ToUnixTimeMilliseconds();
        var to = snapshot.At.AddMinutes(30).ToUnixTimeMilliseconds();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{grafanaUrl.TrimEnd('/')}/d/hephaisto/hephaisto?from={from}&to={to}");
    }
}
