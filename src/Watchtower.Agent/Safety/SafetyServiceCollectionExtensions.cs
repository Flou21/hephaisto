namespace Watchtower.Agent.Safety;

public static class SafetyServiceCollectionExtensions
{
    /// <summary>
    /// Environment variable naming the directory the switch ConfigMap is projected into.
    /// Absent means there is no ConfigMap arm here, which is normal outside Kubernetes.
    /// </summary>
    public const string SwitchDirectoryVariable = "WATCHTOWER_SWITCHES_DIR";

    public static IServiceCollection AddWatchtowerSafety(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<KillSwitchOptions>()
            .Bind(configuration.GetSection(KillSwitchOptions.SectionName))
            .Configure(o =>
            {
                // The env var wins over the config section: it is what the manifest sets,
                // and the manifest is the thing an operator edits.
                var fromEnvironment = configuration[SwitchDirectoryVariable];

                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                {
                    o.SwitchDirectory = fromEnvironment;
                }
            });

        services.AddSingleton<ModeSnapshot>();
        services.AddSingleton<IKillSwitch, KillSwitch>();
        services.AddHostedService<SwitchWatcher>();

        return services;
    }
}
