using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Agent.Options;
using Watchtower.Agent.Persistence.Repositories;
using Watchtower.Core.Abstractions;

namespace Watchtower.Agent.Persistence;

/// <summary>
/// The persistence stream's single contribution to the composition root, so Program.cs stays
/// one readable page.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddWatchtowerPersistence(
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

        services.AddDbContext<WatchtowerDbContext>(o =>
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

        return services;
    }
}
