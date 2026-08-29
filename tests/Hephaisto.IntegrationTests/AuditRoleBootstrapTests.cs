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
