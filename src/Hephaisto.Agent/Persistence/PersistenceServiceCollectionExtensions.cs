using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Agent.Telemetry;

namespace Hephaisto.Agent.Persistence;

/// <summary>
/// The persistence stream's single contribution to the composition root, so Program.cs stays
/// one readable page.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddHephaistoPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.Configure<LlmBudgetOptions>(configuration.GetSection(LlmBudgetOptions.SectionName));

        var persistence = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>()
            ?? new PersistenceOptions();

        var connectionString =
            configuration.GetConnectionString(persistence.ConnectionStringName)
            ?? persistence.ConnectionString
            ?? throw new InvalidOperationException(
                $"No connection string: set ConnectionStrings:{persistence.ConnectionStringName} "
                + $"or {PersistenceOptions.SectionName}:{nameof(PersistenceOptions.ConnectionString)}.");

        services.AddDbContext<HephaistoDbContext>(o =>
        {
            o.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                npgsql.CommandTimeout((int)persistence.CommandTimeout.TotalSeconds);

                // Note what is NOT here: EnableRetryOnFailure. An execution strategy refuses
                // to run a user-initiated transaction unless the whole thing is wrapped in
                // it, which would break TryAdmitActionAsync - and that method already owns a
                // retry loop written to fail closed. A generic retry that silently replays a
                // serialization failure is the wrong behaviour on the path that mutates a
                // cluster.
            });
        });

        // Someone else's stream may already have registered the clock; either way there must
        // be exactly one, because every window in this layer is measured against it.
        services.TryAddSingleton<IClock>(SystemClock.Instance);

        services.AddScoped<IIncidentRepository, IncidentRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IActionRepository, ActionRepository>();
        services.AddScoped<IAgentModeStore, AgentModeStore>();

        services.AddScoped<LlmBudgetService>();
        services.AddScoped<IncidentSearch>();

        // Singleton by construction (BackgroundService), so it resolves its own scope per
        // sweep rather than holding a DbContext for the lifetime of the process.
        services.AddHostedService<RetentionService>();

        // Same shape, and for the same reason: the budget gauge's callback is synchronous
        // while the value behind it is a database read, so a poller does the awaiting and the
        // callback reads what it cached. Visibility only - CheckAsync re-reads the windows
        // itself for every decision, so losing this stops the dashboard, not the spending.
        services.AddSingleton<BudgetUtilizationSnapshot>();
        services.AddHostedService<BudgetGaugePublisher>();

        return services;
    }
}

/// <summary>
/// Applies pending EF Core migrations at startup.
/// </summary>
/// <remarks>
/// <para>
/// Doing this in-process is normally the wrong answer - concurrent replicas racing the same
/// migration is how you corrupt a schema. It is the right answer <b>here</b>, and for a
/// reason already load-bearing elsewhere in the design: the agent is deliberately a single
/// Deployment with <c>replicas: 1</c> and <c>strategy: Recreate</c>, because the executor's
/// budget, cooldown and kill-switch checks have to share one transaction. That same
/// constraint means there can never be two migrators.
/// </para>
/// <para>
/// It <b>fails fast</b> rather than starting degraded. An agent whose tables do not exist is
/// not a diagnostician with a broken database - it is an agent that cannot write an audit
/// row, and "no audit, no action" is an invariant here. Starting anyway would present a
/// working UI and a healthy pod while silently recording nothing, which is strictly worse
/// than not starting: the failure would surface later as an empty incident list that looks
/// like a quiet cluster.
/// </para>
/// <para>
/// The Postgres init ConfigMap creates the extensions and the schema; only the tables come
/// from here. Both are needed, and neither substitutes for the other.
/// </para>
/// </remarks>
public static class PersistenceHostExtensions
{
    public static async Task MigrateHephaistoDatabaseAsync(this IHost host, CancellationToken ct = default)
    {
        await using var scope = host.Services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Hephaisto.Agent.Persistence.Migrations");

        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date; no migrations pending.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} pending migration(s): {Migrations}",
            pending.Count,
            string.Join(", ", pending));

        await db.Database.MigrateAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Database schema is now current.");
    }
}
