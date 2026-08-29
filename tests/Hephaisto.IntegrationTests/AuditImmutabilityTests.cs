using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// "No audit, no action" is a load-bearing invariant, and until this file existed nothing
/// asserted it at any layer.
/// </summary>
/// <remarks>
/// There are two independent defences and they are not equivalent. The application-side
/// guard in <c>HephaistoDbContext.GuardAuditImmutability</c> stops this process rewriting
/// history. The Postgres GRANT/REVOKE stops anything holding the credential from doing it,
/// including a compromised process and a human with psql. The second is the one the design
/// comments claim; the first is what is actually running in the deployed configuration
/// today. Both are tested here, separately, so that "which one is protecting me" is
/// answerable.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditImmutabilityTests(PostgresFixture pg)
{
    [Fact]
    public async Task An_audit_event_can_be_appended()
    {
        await pg.ResetAsync();

        await using var db = pg.CreateContext();
        db.AuditEvents.Add(NewEvent());

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await db.AuditEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task The_context_refuses_to_update_an_audit_event()
    {
        await pg.ResetAsync();

        await using (var seed = pg.CreateContext())
        {
            seed.AuditEvents.Add(NewEvent());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = pg.CreateContext();
        var loaded = await db.AuditEvents.SingleAsync(TestContext.Current.CancellationToken);
        loaded.Summary = "rewritten";

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*append-only*");
    }

    [Fact]
    public async Task The_context_refuses_to_delete_an_audit_event()
    {
        await pg.ResetAsync();

        await using (var seed = pg.CreateContext())
        {
            seed.AuditEvents.Add(NewEvent());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = pg.CreateContext();
        db.AuditEvents.Remove(await db.AuditEvents.SingleAsync(TestContext.Current.CancellationToken));

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*append-only*");
    }

    /// <summary>
    /// The database-level half, which only exists where the migration found a
    /// <c>hephaisto_app</c> role to revoke from.
    /// </summary>
    /// <remarks>
    /// <b>This test is the one that found the gap.</b> The migration reads
    /// "The application connects as hephaisto_app. Immutability of audit_events is enforced
    /// by Postgres, not by convention" - and in the deployed cluster it is not, because the
    /// role does not exist there and the agent connects as the owning role, which holds
    /// UPDATE, DELETE and TRUNCATE. The DO block took its ELSE branch and raised a notice
    /// nobody read.
    ///
    /// It asserts rather than skips when the role is present, and reports the gap explicitly
    /// when it is not, because "the guarantee is absent here" is the finding, not a reason to
    /// stay quiet.
    /// </remarks>
    [Fact]
    public async Task Postgres_itself_refuses_an_update_when_the_restricted_role_exists()
    {
        await pg.ResetAsync();

        await using var db = pg.CreateContext();

        var roleExists = await db.Database
            .SqlQuery<bool>($"""select exists (select 1 from pg_roles where rolname = 'hephaisto_app') as "Value" """)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.SkipUnless(roleExists,
            "no hephaisto_app role in this database, so audit immutability rests entirely on "
            + "HephaistoDbContext. That is the deployed cluster's situation too - see the "
            + "remarks on this test.");

        await using (var seed = pg.CreateContext())
        {
            seed.AuditEvents.Add(NewEvent());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The password is NOT hardcoded, because the two places that create this role choose
        // different ones: scripts/dev-db.sh uses 'dev', and CI uses whatever it exports here.
        // Hardcoding either one makes the test pass in that environment and fail with
        // "password authentication failed for user hephaisto_app" in the other - which is
        // exactly what happened, silently, for every CI run until this was fixed.
        var appPassword = Environment.GetEnvironmentVariable("HEPHAISTO_APP_PASSWORD") ?? "dev";

        var restricted = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = "hephaisto_app",
            Password = appPassword,
        }.ConnectionString;

        await using var conn = new NpgsqlConnection(restricted);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var schema = await pg.SchemaAsync();
        await using var cmd = new NpgsqlCommand($"UPDATE {schema}.audit_events SET summary = 'rewritten'", conn);

        var act = async () => await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // 42501 is insufficient_privilege. Asserting the code, not the message, because the
        // message is localised and the code is the contract.
        (await act.Should().ThrowAsync<PostgresException>())
            .Where(e => e.SqlState == "42501");
    }

    private static AuditEvent NewEvent() => new()
    {
        At = DateTimeOffset.UtcNow,
        Type = "test.event",
        Actor = "test",
        Summary = "original",
    };
}
