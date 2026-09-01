using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hephaisto.Agent.Demo;

/// <summary>
/// Wiring for the demo seed. The composition root calls this and nothing else.
/// </summary>
public static class DemoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the seeder. It is a no-op unless <c>Demo:Seed</c> is true, and refuses on any
    /// database that already holds an incident.
    /// </summary>
    /// <remarks>
    /// Registered unconditionally rather than behind an <c>if</c> on the flag, so that turning
    /// the flag on is a configuration change and not a different composition. The decision is
    /// made once, inside the service, next to the guard that makes it safe.
    /// </remarks>
    public static IServiceCollection AddHephaistoDemo(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DemoOptions>(configuration.GetSection(DemoOptions.SectionName));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, DemoSeeder>());

        return services;
    }
}
