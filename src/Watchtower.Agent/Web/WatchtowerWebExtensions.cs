using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Watchtower.Core.Abstractions;

namespace Watchtower.Agent.Web;

/// <summary>
/// The human-surface stream's contribution to the composition root: one AddXxx and one
/// MapXxx, so Program.cs stays one readable page.
/// </summary>
public static class WatchtowerWebExtensions
{
    public static IServiceCollection AddWatchtowerWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Enums as names on the wire, matching how they are stored in Postgres. An API that
        // answers `"state": 8` forces every consumer to keep a copy of the enum's numbering,
        // and renumbering it later silently changes the meaning of every recorded response.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Registered here as well as in AddWatchtowerPersistence so the two are order-
        // independent; TryAdd means whichever runs first wins and there is still exactly one.
        services.TryAddSingleton<IClock>(SystemClock.Instance);

        services.TryAddSingleton<IIncidentNotifier, IncidentNotifier>();
        services.TryAddSingleton<WatchdogMonitor>();
        services.TryAddSingleton<IncidentQueries>();

        // TryAdd, so the ingest stream can register the real sink before this runs and this
        // will not overwrite it. The no-op logs and drops - see ISignalSink for why that is
        // the right failure shape for a webhook.
        services.TryAddSingleton<ISignalSink, LoggingSignalSink>();

        return services;
    }

    /// <summary>
    /// Every HTTP route this stream owns. Call once from Program.cs.
    /// </summary>
    public static WebApplication MapWatchtowerEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapAlertmanagerEndpoints();
        app.MapIncidentEndpoints();
        app.MapStatusEndpoints();

        return app;
    }
}
