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
            .ValidateOnStart();

        services.AddScoped<IAgentEventNotifier, AgentEventNotifier>();

        return services;
    }
}
