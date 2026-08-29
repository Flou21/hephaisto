using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        // The connection the agent SERVES on. Falls back to the owner when unset, which is the
        // pre-existing behaviour and the reason an upgrade does not fail closed on a Secret
        // that predates the key - EnsureAuditImmutabilityAsync then says so at WARN.
        var appConnectionString =
            UsableAppConnection(configuration.GetConnectionString(persistence.AppConnectionStringName));

        services.AddSingleton(new DatabaseRoles(connectionString, appConnectionString));

        services.AddDbContext<HephaistoDbContext>(o =>
        {
            o.UseNpgsql(appConnectionString ?? connectionString, npgsql =>
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

    /// <summary>
    /// Null unless the app connection string is actually usable.
    /// </summary>
    /// <remarks>
    /// The chart composes this string with <c>Password=$(POSTGRES_APP_PASSWORD)</c>, and the
    /// kubelet leaves an unresolvable <c>$(VAR)</c> reference <b>literally in place</b> rather
    /// than substituting an empty string. So a Secret that predates the key does not yield a
    /// blank password - it yields the eight-character text "$(POSTGRES_APP_PASSWORD)", which
    /// is a perfectly well-formed connection string that fails authentication at the first
    /// query, long after startup has reported success. Catching it here turns that into the
    /// documented fallback.
    /// </remarks>
    private static string? UsableAppConnection(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var password = new NpgsqlConnectionStringBuilder(connectionString).Password;

        return string.IsNullOrWhiteSpace(password) || password.StartsWith("$(", StringComparison.Ordinal)
            ? null
            : connectionString;
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
    /// <summary>
    /// The whole startup database sequence, in the one order that works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the order is not obvious and getting it wrong is not survivable.
    /// The role has to be created BEFORE migrating, migration has to run as the OWNER, and the
    /// grants have to be applied AFTER migrating because they name tables that migration
    /// creates. Exposing three methods and trusting the caller to sequence them is how
    /// v0.1.0-rc2 shipped a chart that could not install: the DbContext had been repointed at
    /// the serving role, migrations run through that DbContext, and on a fresh database that
    /// role does not exist yet - so the agent failed to start and the Deployment never became
    /// Available. Nothing caught it locally, because every developer database already had the
    /// role.
    /// </para>
    /// </remarks>
    public static async Task PrepareDatabaseAsync(this IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        // 1. The role first: migration connects as it on every boot after this one, and it
        //    cannot be created by a connection that is already trying to authenticate as it.
        await host.EnsureAppRoleAsync(ct).ConfigureAwait(false);

        // 2. Migrate as the owner. The serving role is deliberately not allowed to do this.
        await host.MigrateHephaistoDatabaseAsync(ct).ConfigureAwait(false);

        // 3. Grants last, because they name tables step 2 may have just created.
        await host.EnsureAuditImmutabilityAsync(ct).ConfigureAwait(false);
    }

    public static async Task MigrateHephaistoDatabaseAsync(this IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Hephaisto.Agent.Persistence.Migrations");

        // The OWNER connection, built here rather than resolved from DI. The registered
        // DbContext serves as the non-owner role, which holds no DDL rights and - on a
        // database being migrated for the first time - does not exist yet.
        var roles = scope.ServiceProvider.GetRequiredService<DatabaseRoles>();

        var options = new DbContextOptionsBuilder<HephaistoDbContext>()
            .UseNpgsql(roles.OwnerConnectionString, npgsql => npgsql.UseVector())
            .Options;

        await using var db = new HephaistoDbContext(options);

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

    /// <summary>
    /// Creates (or re-passwords) the role the agent serves as. Safe before the schema exists.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the grants, and deliberately first. A role is not a table,
    /// so this needs nothing to exist yet - whereas the grants name tables that migration has
    /// to create, and migration itself now connects as this role.
    /// </remarks>
    public static async Task EnsureAppRoleAsync(this IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<DatabaseRoles>();

        if (roles.AppConnectionString is null)
        {
            return;
        }

        var app = Validated(roles);

        await using var connection = new NpgsqlConnection(roles.OwnerConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var exists = await Scalar<bool>(
            connection,
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)",
            ct,
            ("role", app.Username));

        // ALTER rather than skip, so rotating the password in the Secret takes effect on the
        // next roll instead of leaving the role on a credential nothing knows any more.
        var ddl = await Scalar<string>(
            connection,
            exists
                ? "SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', @role, @password)"
                : "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', @role, @password)",
            ct,
            ("role", app.Username),
            ("password", app.Password));

        await Execute(connection, ddl, ct);
    }

    /// <summary>
    /// Creates the serving role and makes <c>audit_events</c> append-only for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this runs at startup rather than living in a migration.</b> The GRANT in
    /// <c>InitialCreate</c> is wrapped in <c>IF EXISTS (SELECT 1 FROM pg_roles ...)</c>, so on
    /// every database where the role did not exist yet - which was all of them, including the
    /// deployed one - it logged a NOTICE and did nothing. A migration also runs exactly once,
    /// so any later migration that adds a table silently leaves it ungranted, and its own
    /// comment says as much: "A later migration that adds a table has to repeat the grant."
    /// Re-applying the whole block on every boot removes both failure modes.
    /// </para>
    /// <para>
    /// It runs on the OWNER connection, after migrating, because only the owner may GRANT and
    /// because the tables have to exist before they can be granted on.
    /// </para>
    /// <para>
    /// <b>The REVOKE is the point.</b> A role that owns a table can always grant itself back,
    /// so enforcement only means something for a role that is not the owner. This is the
    /// database-side half of "no audit, no action"; <c>HephaistoDbContext.SaveChangesAsync</c>
    /// throwing on a Modified or Deleted audit entry is the application-side half, and neither
    /// is sufficient alone.
    /// </para>
    /// </remarks>
    public static async Task EnsureAuditImmutabilityAsync(this IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();

        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Hephaisto.Agent.Persistence.AuditImmutability");

        var roles = scope.ServiceProvider.GetRequiredService<DatabaseRoles>();

        if (roles.AppConnectionString is null)
        {
            // Deliberately loud, and deliberately not fatal. Refusing to boot would turn a
            // missing Secret key into an outage on upgrade; saying nothing would let the
            // headline guarantee of the audit trail quietly not hold.
            logger.LogWarning(
                "No {Name} connection string: the agent is serving as the database OWNER, so "
                + "audit_events immutability rests entirely on HephaistoDbContext and NOT on "
                + "Postgres privileges. Set it to the application role to enforce it.",
                "ConnectionStrings:hephaisto_app");

            return;
        }

        var app = Validated(roles);

        // Idempotent, and here so this method is still correct called on its own - which is
        // how the tests drive it. PrepareDatabaseAsync has already done it by this point.
        await host.EnsureAppRoleAsync(ct).ConfigureAwait(false);

        // Identifiers and passwords cannot be bound as parameters in DDL, and a DO block is
        // no help: its body is a dollar-quoted string literal, so a placeholder inside one is
        // just text and never gets bound at all.
        //
        // So the statement is composed server-side by format(), whose %I and %L do Postgres's
        // own identifier and literal quoting, with the values travelling as real parameters.
        // Building the DDL by string concatenation in C# would put a password the caller
        // supplied straight into a command, which is the one thing worth avoiding here.
        await using var connection = new NpgsqlConnection(roles.OwnerConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // WHICH SCHEMA the tables are in is looked up, never assumed - and "public" would have
        // been wrong exactly where it matters.
        //
        // Nothing calls HasDefaultSchema, so EF emits unqualified DDL and Postgres puts the
        // tables wherever search_path points. That is `"$user", public`. On a developer's
        // throwaway container no schema is named after the role, so they land in `public`; in
        // the cluster the Postgres init script creates a schema called `hephaisto` and the role
        // is also called `hephaisto`, so `"$user"` resolves and they land in `hephaisto`
        // instead. Granting on `public` by name therefore covers everything locally and
        // NOTHING in the deployment - which is the same shape as the bug this method exists to
        // fix, and it would have locked the agent out of its own database rather than merely
        // failing to protect it.
        var schema = await Scalar<string>(
            connection,
            """
            SELECT table_schema FROM information_schema.tables
            WHERE table_name = 'audit_events'
            ORDER BY (table_schema = current_schema()) DESC
            LIMIT 1
            """,
            ct);

        // ORDER BY is load-bearing: the blanket grant hands out UPDATE and DELETE on every
        // table including audit_events, and the REVOKE has to land after it.
        //
        // The search_path line is not optional either. The app role's own `"$user"` names a
        // schema that does not exist, so without this it would resolve unqualified table names
        // to `public`, find nothing, and fail every query with "relation does not exist".
        var grantDdl = await Scalar<string>(
            connection,
            """
            SELECT string_agg(stmt, '; ' ORDER BY ord)
            FROM (VALUES
                (1, format('GRANT USAGE ON SCHEMA %I TO %I', @schema, @role)),
                (2, format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', @schema, @role)),
                (3, format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', @schema, @role)),
                (4, format('ALTER ROLE %I SET search_path = %I, public', @role, @schema)),
                (5, format('REVOKE UPDATE, DELETE, TRUNCATE ON %I.audit_events FROM %I', @schema, @role))
            ) AS t(ord, stmt)
            """,
            ct,
            ("role", app.Username),
            ("schema", schema));

        await Execute(connection, grantDdl, ct);

        logger.LogInformation(
            "Serving as {Role} on schema {Schema}; audit_events is append-only for it "
            + "(UPDATE, DELETE and TRUNCATE revoked).",
            app.Username,
            schema);
    }

    /// <summary>
    /// The serving-role connection, checked. Throws rather than returning something unusable.
    /// </summary>
    /// <remarks>
    /// The owner check is the one that matters. Pointing this at the owner would work - every
    /// query would succeed - while enforcing nothing at all, because Postgres cannot restrain
    /// a table's owner. A configuration that reports success while protecting nothing is the
    /// exact failure this whole path exists to remove, so it is refused rather than accepted.
    /// </remarks>
    private static (string Username, string Password) Validated(DatabaseRoles roles)
    {
        var app = new NpgsqlConnectionStringBuilder(roles.AppConnectionString);

        if (string.IsNullOrWhiteSpace(app.Username) || string.IsNullOrWhiteSpace(app.Password))
        {
            throw new InvalidOperationException(
                "The hephaisto_app connection string must carry a Username and a Password: the "
                + "role is created from them, so an incomplete one would produce a role nobody "
                + "can log in as.");
        }

        var owner = new NpgsqlConnectionStringBuilder(roles.OwnerConnectionString).Username;

        if (string.Equals(app.Username, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The serving role and the owner are both '{app.Username}'. Postgres cannot "
                + "restrain a table's owner - it may grant itself back - so this configuration "
                + "would report success while enforcing nothing.");
        }

        // Returned as plain non-null strings rather than the builder: every caller passes
        // them as command parameters, and handing back nullable properties makes each of
        // those a nullability warning that -warnaserror turns into a failed release.
        return (app.Username, app.Password);
    }

    private static async Task<T> Scalar<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return (T)result!;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The two database identities: the owner that migrates, and the role the agent serves as.
/// </summary>
/// <remarks>
/// Held as a singleton so <see cref="PersistenceServiceCollectionExtensions.EnsureAuditImmutabilityAsync"/>
/// can reach the owner connection after the DbContext has been pointed at the other one.
/// </remarks>
/// <param name="OwnerConnectionString">Owns the schema and applies migrations.</param>
/// <param name="AppConnectionString">
/// What the agent serves as. Null means it serves as the owner, which enforces nothing.
/// </param>
public sealed record DatabaseRoles(string OwnerConnectionString, string? AppConnectionString);
