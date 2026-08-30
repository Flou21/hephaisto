using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// The outbound delivery stream: routing, the outbox dispatcher and the channels.
/// </summary>
/// <remarks>
/// One <c>AddXxx</c> per stream, so <c>Program.cs</c> stays one readable page - which matters
/// because "what is actually switched on in this process" is a security question here rather
/// than a stylistic one. This stream in particular: it is the only one that sends anything
/// outward.
/// </remarks>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddHephaistoNotifications(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))

            // A route naming a channel that does not exist is the failure this whole stream is
            // about, wearing a different hat: the table looks correct and delivers nothing. It
            // is refused at startup rather than discovered the first time something escalates.
            .Validate(
                o => o.Routes.TrueForAll(r => !string.IsNullOrWhiteSpace(r.Channel)),
                "Notifications:Routes contains a route with no channel name.")

            // Every message this stream sends exists to make somebody open a link. Without a
            // base URL the pod cannot build one - it knows the address it binds, not the one a
            // person reaches it on - and the cards would ship with nothing to click.
            .Validate(
                o => o.Routes.Count == 0 || !string.IsNullOrWhiteSpace(o.BaseUrl),
                "Notifications:BaseUrl must be set when any route is configured, or every message "
                    + "ships without a link back to the incident.")
            .Validate(
                o => o.MaxAttempts > 0,
                "Notifications:MaxAttempts must be at least 1, or nothing is ever delivered.")
            .Validate(
                o => o.DispatchBatchSize > 0,
                "Notifications:DispatchBatchSize must be at least 1, or the dispatcher reads nothing.")

            // The failure this whole stream exists to remove, wearing a different hat: a route
            // to a channel nobody configured looks entirely correct and delivers nothing.
            .Validate(
                o =>
                {
                    var configured = o.ConfiguredChannels().ToHashSet(StringComparer.OrdinalIgnoreCase);

                    return o.Routes.TrueForAll(r => configured.Contains(r.Channel));
                },
                "Notifications:Routes names a channel that is not configured. Set "
                    + "Notifications:Webhook:Url or Notifications:Teams:WorkflowUrl, or remove the route.")
            .ValidateOnStart();

        var configured = configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()
            ?? new NotificationOptions();

        // Registered only when configured, the same shape as IGrafanaAnnotator. A channel is a
        // startup decision; a ROUTE is not, and hot-reloads freely.
        if (!string.IsNullOrWhiteSpace(configured.Webhook.Url))
        {
            // ServiceDefaults applies AddStandardResilienceHandler to every client the factory
            // builds. Left on, each outbox attempt would silently become three or more HTTP
            // attempts against an endpoint that is already struggling, and the backoff written
            // in NotificationBackoff would describe something the system does not do. The
            // outbox is the retry authority because it is the only one that survives a restart.
            //
            // RemoveAllResilienceHandlers is [Experimental], and the suppression is deliberate
            // and narrow. The alternative is constructing the HttpClient by hand the way the
            // Gemini client does - which this repo already records as a downside, because a
            // hand-built client misses the factory's handler rotation and the OTel HTTP
            // instrumentation. An experimental removal is the smaller cost, and if it is ever
            // withdrawn this line fails to compile rather than silently re-enabling the retries.
#pragma warning disable EXTEXP0001
            services.AddHttpClient<HttpNotificationChannel>()
                .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

            services.AddTransient<INotificationChannel>(sp => sp.GetRequiredService<HttpNotificationChannel>());
        }

        services.AddScoped<IAgentEventNotifier, AgentEventNotifier>();

        // Registered unconditionally, even with no routes configured. Its tick is two indexed
        // reads that find nothing, which is cheap enough to be worth the property it buys: a
        // route added by a ConfigMap edit starts delivering without a pod restart, so turning
        // notifications on is not a thing that can appear to work and quietly not.
        services.AddHostedService<NotificationDispatcher>();

        // Says once, at startup, what this process can and cannot send outward - because every
        // outbound thing here degrades silently, and "nothing happened" looks the same whether
        // it was never configured or is broken.
        services.AddHostedService<OutboundStartupReport>();

        return services;
    }
}
