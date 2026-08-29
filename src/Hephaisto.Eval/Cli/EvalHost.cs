using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Pipeline;

namespace Hephaisto.Eval.Cli;

/// <summary>
/// Builds the slice of the agent's composition root that each subcommand needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>A container, never a host.</b> Both <c>AddHephaistoKubernetes</c> and
/// <c>AddHephaistoPersistence</c> register <c>IHostedService</c>s - the cluster watcher, the
/// retention sweep, the budget gauge - and this deliberately builds a bare
/// <see cref="ServiceProvider"/> instead of an <c>IHost</c>, so none of them ever start. That is
/// not a shortcut: an eval process that started the watcher would ingest live signals and open
/// incidents, and one that started the retention sweep would delete rows out from under the
/// running agent. The harness must be able to read the dev cluster without becoming a second
/// agent in it.
/// </para>
/// <para>
/// Both commands construct <c>InvestigationRunner</c> by hand rather than resolving it, for the
/// same reason the tests do: recording and replaying both work by <i>substituting the tool
/// surface</i>, and passing it to a constructor is a great deal clearer than overriding two
/// registrations and hoping nothing else resolved the originals first.
/// </para>
/// </remarks>
internal static class EvalHost
{
    /// <summary>
    /// Configuration in the same order the agent reads it, plus <c>--set</c> on top.
    /// </summary>
    /// <remarks>
    /// The overrides sit last so an experiment arm is one command line rather than an exported
    /// environment variable that outlives the run that wanted it - which is exactly how a
    /// "baseline" gets recorded with the previous arm's settings still in the shell.
    /// </remarks>
    public static IConfigurationRoot BuildConfiguration(IReadOnlyList<string> overrides)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables();

        if (overrides.Count > 0)
        {
            builder.AddInMemoryCollection(ParseOverrides(overrides));
        }

        return builder.Build();
    }

    /// <summary>
    /// Turns <c>--set Llm:Investigation:MaxSteps=20</c> into a configuration entry.
    /// </summary>
    /// <remarks>
    /// <c>__</c> is accepted as a separator alongside <c>:</c> so that the key from a Helm
    /// <c>extraEnv</c> block can be pasted here unchanged. Being able to run the same arm locally
    /// and in the cluster by copying one string is worth the three lines.
    /// </remarks>
    internal static IEnumerable<KeyValuePair<string, string?>> ParseOverrides(IReadOnlyList<string> overrides)
    {
        foreach (var entry in overrides)
        {
            var split = entry.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0)
            {
                throw new ArgumentException($"--set needs Key=Value, got '{entry}'");
            }

            yield return new KeyValuePair<string, string?>(
                entry[..split].Replace("__", ":", StringComparison.Ordinal),
                entry[(split + 1)..]);
        }
    }

    /// <summary>
    /// Everything needed to run the investigation loop against a substituted tool surface: the
    /// model, the prompt composer and the per-investigation budgets. No database and no cluster.
    /// </summary>
    public static ServiceProvider BuildForReplay(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        AddLogging(services, configuration);

        // Without persistence: there is no Postgres to count a rolling spend window in, so the
        // per-investigation ceilings are the only budget. That is a smaller cap, not no cap.
        services.AddHephaistoLlmWithoutPersistence(configuration);
        services.AddSingleton<InvestigationTracker>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The above plus the live cluster and the incident database, for recording.
    /// </summary>
    public static ServiceProvider BuildForRecording(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        AddLogging(services, configuration);

        services.AddHephaistoPersistence(configuration);
        services.AddHephaistoKubernetes(configuration);
        services.AddHephaistoLlmWithoutPersistence(configuration);
        services.AddSingleton<InvestigationTracker>();

        // The same bridge the agent's Program.cs builds, and it has to be built here too: the
        // runner consumes IEnumerable<AIFunction> without knowing where the tools came from, and
        // AddHephaistoKubernetes deliberately does not register that shape.
        services.AddSingleton<IEnumerable<AIFunction>>(sp =>
            sp.GetRequiredService<KubernetesReadTools>().CreateFunctions());

        return services.BuildServiceProvider();
    }

    private static void AddLogging(IServiceCollection services, IConfiguration configuration) =>
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));

            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });

            // Every log line to stderr, so a report piped to a file stays a report. The
            // interesting output of this tool is on stdout and is meant to be read or diffed.
            builder.Services.Configure<ConsoleLoggerOptions>(
                o => o.LogToStandardErrorThreshold = LogLevel.Trace);

            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddFilter("Hephaisto", LogLevel.Information);
        });
}
