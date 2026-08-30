using Hephaisto.Agent.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// <see cref="PersistenceServiceCollectionExtensions.EnsureAuditImmutabilityAsync"/>, against a
/// real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// This runs at every startup and issues DDL, so it is the highest-consequence code added for
/// v0.1.0: getting it wrong does not merely fail to protect the audit trail, it can revoke the
/// agent's access to its own database.
/// </para>
/// <para>
/// <b>One case here is not hypothetical.</b> The first version granted on <c>SCHEMA public</c>
/// by name. Nothing calls <c>HasDefaultSchema</c>, so EF emits unqualified DDL and the tables
/// land wherever <c>search_path</c> - <c>"$user", public</c> - points. On a developer's
/// container that is <c>public</c>; in the cluster the Postgres init script creates a schema
/// named <c>hephaisto</c> and the owning role is also <c>hephaisto</c>, so <c>"$user"</c>
/// resolves and the tables are there instead. The grant would have covered nothing in the one
/// environment it was written for.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditRoleBootstrapTests(PostgresFixture pg)
{
    private const string Role = "hephaisto_boot_test";
    private const string Password = "boot-test-pw";

    /// <summary>
    /// The whole point: the serving role may append to <c>audit_events</c> and may not rewrite
    /// it, while ordinary tables stay fully writable.
    /// </summary>
    [Fact]
    public async Task The_serving_role_can_append_to_the_audit_trail_but_not_rewrite_it()
    {
        await RunBootstrapAsync();

        (await PrivilegeAsync("INSERT", "audit_events")).Should().BeTrue("the agent must be able to write audit rows");
        (await PrivilegeAsync("SELECT", "audit_events")).Should().BeTrue("the console reads them back");

        (await PrivilegeAsync("UPDATE", "audit_events")).Should().BeFalse();
        (await PrivilegeAsync("DELETE", "audit_events")).Should().BeFalse();

        // The asymmetry is the design. If this were false too, the "fix" would just be an
        // outage that happens to satisfy the audit assertion.
        (await PrivilegeAsync("UPDATE", "incidents")).Should().BeTrue("everything else stays writable");
    }

    /// <summary>
    /// A table added by a <b>later</b> migration is granted too.
    /// </summary>
    /// <remarks>
    /// backlog #6's trap in its general form, and the reason the grant is re-applied on every
    /// boot rather than written into a migration. <c>InitialCreate</c>'s GRANT block is wrapped
    /// in <c>IF EXISTS (SELECT 1 FROM pg_roles ...)</c> and its own comment notes that a later
    /// migration adding a table has to repeat it - so every table added after that point would
    /// be ungranted, and the symptom would be the agent unable to write a feature that had just
    /// shipped, on a deployment that started up reporting success.
    /// <c>notification_deliveries</c> arrives in v0.3.0, long after <c>InitialCreate</c>, so it
    /// is live proof rather than an assertion about intent.
    /// </remarks>
    [Fact]
    public async Task A_table_added_by_a_later_migration_is_granted_too()
    {
        await RunBootstrapAsync();

        (await PrivilegeAsync("INSERT", "notification_deliveries")).Should().BeTrue();
        (await PrivilegeAsync("SELECT", "notification_deliveries")).Should().BeTrue();

        // Unlike an audit row, a delivery is meant to be rewritten - the attempt count and the
        // backoff move on every retry. The REVOKE is deliberately narrow to audit_events, and
        // this is what says so.
        (await PrivilegeAsync("UPDATE", "notification_deliveries")).Should().BeTrue();
    }

    /// <summary>
    /// The grants land on the schema the tables are actually in.
    /// </summary>
    /// <remarks>
    /// The regression test for the bug described on the class. Asserting through
    /// <c>has_table_privilege</c> with an unqualified name would resolve via the CALLER's
    /// search_path and pass against a hardcoded `public`, so this pins the schema explicitly.
    /// </remarks>
    [Fact]
    public async Task The_grants_follow_the_schema_the_tables_are_really_in()
    {
        await RunBootstrapAsync();

        var schema = await pg.SchemaAsync();

        (await PrivilegeAsync("SELECT", $"{Quote(schema)}.audit_events")).Should().BeTrue();
        (await PrivilegeAsync("UPDATE", $"{Quote(schema)}.audit_events")).Should().BeFalse();
    }

    /// <summary>
    /// The role's <c>search_path</c> is set, or every query it makes fails.
    /// </summary>
    /// <remarks>
    /// EF emits unqualified table names. The serving role's own <c>"$user"</c> names a schema
    /// that does not exist, so without this it resolves them against <c>public</c>, finds
    /// nothing, and every single query fails with "relation does not exist" - at runtime, on a
    /// deployment that started up reporting success.
    /// </remarks>
    [Fact]
    public async Task The_serving_role_gets_a_search_path_that_finds_the_tables()
    {
        await RunBootstrapAsync();

        var schema = await pg.SchemaAsync();

        await using var db = pg.CreateContext();

        var configured = await db.Database
            .SqlQuery<string>($"""
                select unnest(rolconfig) as "Value" from pg_roles where rolname = {Role}
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        configured.Should().Contain(c => c.StartsWith("search_path=", StringComparison.Ordinal) && c.Contains(schema));
    }

    /// <summary>Running twice is normal - it runs on every boot - and must not throw.</summary>
    [Fact]
    public async Task Running_it_again_rotates_the_password_rather_than_failing()
    {
        await RunBootstrapAsync();
        await RunBootstrapAsync(password: "rotated-pw");

        // ALTER rather than skip, so a password rotated in the Secret takes effect on the next
        // roll instead of leaving the role on a credential nothing knows.
        var connection = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = Role,
            Password = "rotated-pw",
        }.ConnectionString;

        await using var probe = new NpgsqlConnection(connection);

        await probe.Invoking(c => c.OpenAsync(TestContext.Current.CancellationToken))
            .Should().NotThrowAsync("the second run should have set the new password");
    }

    /// <summary>
    /// Pointing the serving role at the owner is refused rather than silently accepted.
    /// </summary>
    /// <remarks>
    /// It would "work" - every query would succeed - while enforcing nothing at all, because
    /// Postgres cannot restrain a table's owner: it can grant itself back at will. A
    /// configuration that reports success while protecting nothing is the exact failure mode
    /// this whole item is about.
    /// </remarks>
    [Fact]
    public async Task Serving_as_the_owner_is_refused()
    {
        var owner = new NpgsqlConnectionStringBuilder(pg.ConnectionString).Username!;

        var app = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = owner,
            Password = "irrelevant",
        }.ConnectionString;

        using var host = BuildHost(pg.ConnectionString, app);

        await host.Invoking(h => h.EnsureAuditImmutabilityAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner*");
    }

    /// <summary>No app connection means it degrades, not throws.</summary>
    /// <remarks>
    /// Upgrading a release whose Secret predates <c>POSTGRES_APP_PASSWORD</c> must not turn
    /// into a crash loop; the agent serves as the owner and logs a warning instead.
    /// </remarks>
    [Fact]
    public async Task Without_an_app_connection_it_warns_instead_of_failing()
    {
        using var host = BuildHost(pg.ConnectionString, appConnectionString: null);

        await host.Invoking(h => h.EnsureAuditImmutabilityAsync(TestContext.Current.CancellationToken))
            .Should().NotThrowAsync();
    }

    /// <summary>
    /// Migration must work when the serving role does not exist yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression test for the defect that broke v0.1.0-rc2's chart install. The DbContext
    /// was repointed at the serving role, and <c>MigrateHephaistoDatabaseAsync</c> resolved the
    /// DbContext from DI - so on a database being migrated for the first time it tried to
    /// authenticate as a role that nothing had created yet. The agent failed to start, the
    /// Deployment never became Available, and <c>helm install</c> timed out.
    /// </para>
    /// <para>
    /// It passed everywhere it was tried beforehand because every existing database already
    /// had the role. So this test asserts the property directly: with the DbContext registered
    /// on a connection whose role <b>does not exist at all</b>, migrating still succeeds -
    /// which it can only do by using the owner connection.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Migration_runs_as_the_owner_even_when_the_serving_role_does_not_exist()
    {
        const string missing = "hephaisto_role_that_does_not_exist";

        await using (var db = pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                $"DROP ROLE IF EXISTS {missing}", TestContext.Current.CancellationToken);
        }

        var app = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = missing,
            Password = "never-set",
        }.ConnectionString;

        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(new DatabaseRoles(pg.ConnectionString, app));

        // Exactly how production registers it: the DbContext serves as the non-owner role.
        builder.Services.AddDbContext<HephaistoDbContext>(o => o.UseNpgsql(app, n => n.UseVector()));

        using var host = builder.Build();

        await host.Invoking(h => h.MigrateHephaistoDatabaseAsync(TestContext.Current.CancellationToken))
            .Should().NotThrowAsync(
                "migration must use the owner connection - the serving role may not exist yet");
    }

    /// <summary>
    /// The full startup sequence works against a role that has never existed.
    /// </summary>
    /// <remarks>
    /// The ordering is the thing under test: create the role, migrate as the owner, then grant.
    /// Any other order fails, and each failure looks like a different problem.
    /// </remarks>
    [Fact]
    public async Task The_startup_sequence_creates_the_role_migrates_and_grants_in_that_order()
    {
        const string fresh = "hephaisto_prepare_test";

        // Start from "this role has never existed". DROP OWNED is required before DROP ROLE
        // because a previous run left grants behind, and a role holding privileges cannot be
        // dropped - the error names dependent objects rather than the grants, which is why
        // this is spelled out rather than a bare DROP ROLE IF EXISTS.
        await using (var db = pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                $"""
                 DO $$
                 BEGIN
                     IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{fresh}') THEN
                         EXECUTE 'DROP OWNED BY {fresh}';
                         EXECUTE 'DROP ROLE {fresh}';
                     END IF;
                 END
                 $$;
                 """,
                TestContext.Current.CancellationToken);
        }

        var app = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = fresh,
            Password = "prepare-pw",
        }.ConnectionString;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new DatabaseRoles(pg.ConnectionString, app));
        builder.Services.AddDbContext<HephaistoDbContext>(o => o.UseNpgsql(app, n => n.UseVector()));

        using var host = builder.Build();

        await host.PrepareDatabaseAsync(TestContext.Current.CancellationToken);

        await using var check = pg.CreateContext();

        var canInsert = await check.Database
            .SqlQuery<bool>($"""select has_table_privilege({fresh}, 'audit_events', 'INSERT') as "Value" """)
            .SingleAsync(TestContext.Current.CancellationToken);

        var canUpdate = await check.Database
            .SqlQuery<bool>($"""select has_table_privilege({fresh}, 'audit_events', 'UPDATE') as "Value" """)
            .SingleAsync(TestContext.Current.CancellationToken);

        canInsert.Should().BeTrue();
        canUpdate.Should().BeFalse();
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private async Task RunBootstrapAsync(string password = Password)
    {
        var app = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = Role,
            Password = password,
        }.ConnectionString;

        using var host = BuildHost(pg.ConnectionString, app);

        await host.EnsureAuditImmutabilityAsync(TestContext.Current.CancellationToken);
    }

    private static IHost BuildHost(string owner, string? appConnectionString)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(new DatabaseRoles(owner, appConnectionString));

        return builder.Build();
    }

    private async Task<bool> PrivilegeAsync(string privilege, string table)
    {
        await using var db = pg.CreateContext();

        return await db.Database
            .SqlQuery<bool>($"""
                select has_table_privilege({Role}, {table}, {privilege}) as "Value"
                """)
            .SingleAsync(TestContext.Current.CancellationToken);
    }
}
