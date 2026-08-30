using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The oscillation detector's quarantine, which is recorded per WORKLOAD rather than per
/// incident and has to survive the incident it was learned from.
/// </summary>
/// <remarks>
/// That distinction is the whole feature. A recurrence is a new incident - fingerprints are
/// per-signal and dedup opens a fresh row once the old one closes - so a quarantine written
/// onto the incident lapses at exactly the moment the loop would otherwise continue. This
/// asserts the column is where the admission transaction can see it, against a real database,
/// because the lock row is taken with raw SQL and an ORM-only test would not prove it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class WorkloadQuarantineTests(PostgresFixture pg)
{
    private const string Workload = "hephaisto-chaos/Deployment/c2-crashloop";

    [Fact]
    public async Task A_quarantine_is_recorded_against_the_workload_and_read_back()
    {
        await pg.ResetAsync();

        var until = DateTimeOffset.UtcNow.AddHours(24);

        await using (var db = pg.CreateContext())
        {
            db.WorkloadActionLocks.Add(new WorkloadActionLock
            {
                WorkloadKey = Workload,
                UpdatedAt = DateTimeOffset.UtcNow,
                QuarantinedUntil = until,
                QuarantineReason = "restart_pod 3 times in 2h and the incident reopened",
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var row = await db.WorkloadActionLocks
                .FirstAsync(w => w.WorkloadKey == Workload, TestContext.Current.CancellationToken);

            row.QuarantinedUntil.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
            row.QuarantineReason.Should().Contain("reopened");
        }
    }

    [Fact]
    public async Task The_upsert_admission_uses_does_not_clear_a_quarantine()
    {
        // Admission takes this row with `INSERT ... ON CONFLICT (workload_key) DO UPDATE SET
        // updated_at = ...` at the top of every transaction. If that upsert touched the
        // quarantine columns it would erase the quarantine on the very next attempt to act on
        // the workload - which is the one moment it exists to survive.
        await pg.ResetAsync();

        var until = DateTimeOffset.UtcNow.AddHours(24);

        await using var db = pg.CreateContext();

        db.WorkloadActionLocks.Add(new WorkloadActionLock
        {
            WorkloadKey = Workload,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            QuarantinedUntil = until,
            QuarantineReason = "oscillating",
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO workload_action_locks (workload_key, updated_at)
            VALUES ({0}, now())
            ON CONFLICT (workload_key) DO UPDATE SET updated_at = EXCLUDED.updated_at
            """.Replace("{0}", $"'{Workload}'"),
            TestContext.Current.CancellationToken);

        var reloaded = await db.WorkloadActionLocks
            .AsNoTracking()
            .FirstAsync(w => w.WorkloadKey == Workload, TestContext.Current.CancellationToken);

        reloaded.QuarantinedUntil.Should().NotBeNull();
        reloaded.QuarantineReason.Should().Be("oscillating");
    }
}
