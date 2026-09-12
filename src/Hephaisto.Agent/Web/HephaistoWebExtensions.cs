using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Hephaisto.Core.Abstractions;
using Hephaisto.Agent.Observability;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Options;

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

        // Which port the webhook answers on. Bound here rather than in Program.cs so the whole
        // web surface is configured in one place.
        services.AddOptions<WebOptions>()
            .BindConfiguration(WebOptions.SectionName)
            .ValidateOnStart();

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

        // Its own HttpClient rather than a shared one: the discovery fetch must not inherit the
        // Grafana probe's bearer token, and an IdP that 401s a request carrying someone else's
        // credential would report Unreachable for a reason that is not about the IdP.
        services.AddHttpClient<IConnectionProbe, OidcProbe>();

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
    /// <summary>
    /// Refuses a request that arrived on the wrong listener.
    /// </summary>
    /// <remarks>
    /// 404 rather than 403: from the caller's side the endpoint genuinely does not exist on that
    /// port, and saying "forbidden" would confirm it exists somewhere - which is the one thing
    /// the split is trying not to advertise about the webhook.
    /// </remarks>
    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>>
        OnPort(int port) =>
        async (context, next) =>
            context.HttpContext.Connection.LocalPort == port
                ? await next(context)
                : Results.NotFound();

    public static WebApplication MapHephaistoEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The webhook is the one unauthenticated surface, and with WebhookPort set it answers on
        // its own port so a NetworkPolicy can protect it without also deciding who may read the
        // console. See WebOptions for why they shared a port and what that cost.
        var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;

        // MapGroup("") adds no prefix and is both a route builder and a convention builder, so it
        // is the seam that lets a host constraint be applied to a set of endpoints without every
        // Map* method having to return one.
        var webhooks = app.MapGroup(string.Empty);
        webhooks.MapAlertmanagerEndpoints();

        var console = app.MapGroup(string.Empty);
        console.MapIncidentEndpoints();
        console.MapStatusEndpoints();
        console.MapModeEndpoints();
        console.MapVersionEndpoints();

        // Authentication (#110). The webhook group is the ONE surface that stays anonymous, and
        // it has to: Alertmanager has no field for a credential, which is why it needs its own
        // port and a NetworkPolicy in front of it. Everything else requires a signed-in user.
        webhooks.AllowAnonymous();
        console.RequireAuthorization(AuthenticationExtensions.ReadPolicy);

        if (web.WebhookPortIsSeparate)
        {
            // The LISTENING port, not RequireHost.
            //
            // RequireHost matches the HOST HEADER, which is a different thing that happens to
            // look the same in a curl against 127.0.0.1:8080. Through a port-forward the header
            // carries the LOCAL port - `localhost:18100` - and behind an ingress it carries
            // whatever the ingress was reached on, so the constraint never matches and the whole
            // API 404s. The e2e gate found exactly that: `/api/version reports ''`.
            //
            // Connection.LocalPort is the socket the request actually arrived on. Nothing
            // upstream can rewrite it.
            webhooks.AddEndpointFilter(OnPort(web.WebhookPort));

            // And the reverse, which is the half that is easy to forget. If the API still
            // answered on the webhook port, the tight policy in front of that port would be
            // guarding one door with the wall missing beside it - anything allowed to deliver an
            // alert could also read every incident and approve an action.
            console.AddEndpointFilter(OnPort(web.MainPort));
        }

        return app;
    }
}
