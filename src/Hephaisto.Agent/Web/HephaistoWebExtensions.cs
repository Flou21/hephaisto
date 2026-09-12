using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Hephaisto.Core.Abstractions;
using Hephaisto.Agent.Observability;

namespace Hephaisto.Agent.Web;

/// <summary>
/// The human-surface stream's contribution to the composition root: one AddXxx and one
/// MapXxx, so Program.cs stays one readable page.
/// </summary>
public static class HephaistoWebExtensions
{
    public static IServiceCollection AddHephaistoWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Enums as names on the wire, matching how they are stored in Postgres. An API that
        // answers `"state": 8` forces every consumer to keep a copy of the enum's numbering,
        // and renumbering it later silently changes the meaning of every recorded response.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Registered here as well as in AddHephaistoPersistence so the two are order-
        // independent; TryAdd means whichever runs first wins and there is still exactly one.
        services.TryAddSingleton<IClock>(SystemClock.Instance);

        services.TryAddSingleton<IIncidentNotifier, IncidentNotifier>();
        services.TryAddSingleton<WatchdogMonitor>();
        services.TryAddSingleton<IncidentQueries>();

        // The connections panel (#111). Every one of these dependencies already describes itself
        // at startup and the description is thrown into a log line, so "is it actually working?"
        // was answerable only by reading pod logs. The probes re-ask on a timer and the cache
        // serves the last answer with its timestamp.
        //
        // Registered unconditionally, including the probes for things that may be unconfigured:
        // NotConfigured is an answer the panel needs to give, and a missing row would read as
        // "fine". Each resolves what it needs and reports rather than throwing.
        services.TryAddSingleton<IConnectionProbe, PostgresProbe>();
        services.AddSingleton<IConnectionProbe, KubernetesProbe>();
        services.AddSingleton<IConnectionProbe, GrafanaMcpProbe>();
        services.AddSingleton<IConnectionProbe, NotificationChannelProbe>();
        services.AddHttpClient<IConnectionProbe, GrafanaAnnotationProbe>();

        services.TryAddSingleton<ConnectionHealthCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ConnectionHealthCache>());

        // TryAdd, so the ingest stream can register the real sink before this runs and this
        // will not overwrite it. The no-op logs and drops - see ISignalSink for why that is
        // the right failure shape for a webhook.
        services.TryAddSingleton<ISignalSink, LoggingSignalSink>();

        return services;
    }

    /// <summary>
    /// Every HTTP route this stream owns. Call once from Program.cs.
    /// </summary>
    public static WebApplication MapHephaistoEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapAlertmanagerEndpoints();
        app.MapIncidentEndpoints();
        app.MapStatusEndpoints();
        app.MapModeEndpoints();
        app.MapVersionEndpoints();

        return app;
    }
}
