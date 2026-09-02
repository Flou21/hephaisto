using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// <c>AuditEvent.Detail</c> is a <c>jsonb</c> column, and that only becomes visible against a
/// real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// This is backlog #72's actual cause, found after three earlier diagnoses had each blamed the
/// layer in front of it. <c>VerificationScheduler</c> assigned <c>result.Detail</c> - a
/// sentence beginning "Deployment/c13-wedged-lock is settled with 1/1 ready" - straight into
/// that column. Postgres answered
/// <c>22P02: invalid input syntax for type json, Token "Deployment" is invalid</c> and failed
/// the whole <c>SaveChanges</c>.
/// </para>
/// <para>
/// <b>The audit row shares a transaction with the state change it describes</b>, deliberately,
/// because that is what makes "no audit, no action" true. So a malformed detail is not a
/// logging bug: it rolls back the transition to Resolved, and it rolls back the verification
/// row's own outcome, so the next poll finds the same verification still pending, runs it,
/// passes again, and fails to save again. The incident stays in Verifying for ever while the
/// workload is demonstrably healthy, and the only trace is an EF error swallowed by the
/// scheduler's own keep-going catch.
/// </para>
/// <para>
/// Nothing in the unit suite could catch it - the in-memory provider has no column types - and
/// nothing in the e2e could, until a fixture existed that the agent would reliably act on.
/// These run against the real schema.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditDetailIsJsonTests(PostgresFixture pg)
{
    private const string NotJson = "Deployment/c13-wedged-lock is settled with 1/1 ready and no container waiting";

    private static AuditEvent NewEvent(string? detail) => new()
    {
        At = DateTimeOffset.UtcNow,
        Type = "incident.resolved",
        Actor = "hephaisto/verifier",
        Summary = "verification passed",
        Detail = detail,
    };

    /// <summary>
    /// The failure exactly as it happened, so the regression has a witness rather than a
    /// description. Writing the entity directly bypasses the repository guard below.
    /// </summary>
    [Fact]
    public async Task A_raw_sentence_in_detail_is_rejected_by_the_column()
    {
        await pg.ResetAsync();

        await using var db = pg.CreateContext();
        db.AuditEvents.Add(NewEvent(NotJson));

        var act = () => db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();

        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("22P02");
    }

    /// <summary>
    /// And the guard that stops a future caller doing it again. Wrapping rather than throwing:
    /// refusing the row would keep the exact failure mode this exists to remove.
    /// </summary>
    [Fact]
    public async Task The_repository_wraps_a_raw_sentence_so_the_transaction_survives()
    {
        await pg.ResetAsync();

        await using var db = pg.CreateContext();
        var repository = new AuditRepository(db, new SystemClock());

        await repository.AppendAsync(NewEvent(NotJson), TestContext.Current.CancellationToken);

        var stored = await db.AuditEvents.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        // A JSON string is valid jsonb, so the text survives and stays queryable.
        stored.Detail.Should().Be($"\"{NotJson}\"");
    }

    /// <summary>
    /// The guard must not touch a caller that did it properly, or every structured detail in
    /// the audit trail becomes a string containing JSON rather than JSON.
    /// </summary>
    [Fact]
    public async Task A_detail_that_is_already_json_is_stored_unchanged()
    {
        await pg.ResetAsync();

        const string Json = """{"detail":"Deployment/c13-wedged-lock is settled","checks":{"ready":1}}""";

        await using var db = pg.CreateContext();
        var repository = new AuditRepository(db, new SystemClock());

        await repository.AppendAsync(NewEvent(Json), TestContext.Current.CancellationToken);

        var stored = await db.AuditEvents.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        stored.Detail.Should().NotBeNull();
        System.Text.Json.JsonDocument.Parse(stored.Detail!).RootElement
            .GetProperty("checks").GetProperty("ready").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_null_detail_is_still_allowed()
    {
        await pg.ResetAsync();

        await using var db = pg.CreateContext();
        var repository = new AuditRepository(db, new SystemClock());

        await repository.AppendAsync(NewEvent(null), TestContext.Current.CancellationToken);

        (await db.AuditEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }
}
