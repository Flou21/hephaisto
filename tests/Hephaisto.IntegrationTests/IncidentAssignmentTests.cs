using Microsoft.EntityFrameworkCore;

using Hephaisto.Agent.Persistence;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// Assignment against a real database (#112).
/// </summary>
/// <remarks>
/// The semantics are pinned in memory elsewhere. What needs Postgres is the filter behind the
/// console's "mine" view - it runs on every page load, it is the reason <c>assigned_to</c> is the
/// only actor column with an index, and a predicate that quietly matched the wrong rows would
/// show somebody else's work as theirs.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class IncidentAssignmentTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_mine_filter_returns_only_that_persons_incidents()
    {
        await pg.ResetAsync();

        await SeedAsync(assignedTo: "flo");
        await SeedAsync(assignedTo: "flo");
        await SeedAsync(assignedTo: "someone-else");
        await SeedAsync(assignedTo: null);

        await using var db = pg.CreateContext();

        var mine = await db.Incidents
            .Where(i => i.AssignedTo == "flo")
            .CountAsync(TestContext.Current.CancellationToken);

        mine.Should().Be(2);
    }

    /// <summary>
    /// Unassigned incidents match nobody's filter.
    /// </summary>
    /// <remarks>
    /// The index is filtered on <c>assigned_to IS NOT NULL</c>, so this also confirms the
    /// filtered index cannot change the answer - a NULL row is excluded by the predicate itself,
    /// not only by the index.
    /// </remarks>
    [Fact]
    public async Task An_unassigned_incident_belongs_to_nobody()
    {
        await pg.ResetAsync();

        await SeedAsync(assignedTo: null);

        await using var db = pg.CreateContext();

        (await db.Incidents.CountAsync(i => i.AssignedTo == "flo", TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    /// <summary>Assignment round-trips, including being cleared back to nobody.</summary>
    [Fact]
    public async Task Assigning_and_unassigning_persist()
    {
        await pg.ResetAsync();

        var id = await SeedAsync(assignedTo: null);
        var machine = new IncidentStateMachine(new FixedClock(Now));

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);
            machine.Assign(incident, "flo", "someone-else");
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.AsNoTracking()
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            incident.AssignedTo.Should().Be("flo");
            incident.AssignedBy.Should().Be("someone-else");
            incident.AssignedAt.Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));
            incident.AcknowledgedBy.Should().BeNull();
        }

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);
            machine.Assign(incident, null, "flo");
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            (await db.Incidents.AsNoTracking().FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken))
                .AssignedTo.Should().BeNull();
        }
    }

    /// <summary>
    /// The filtered index exists and covers the query the console runs.
    /// </summary>
    /// <remarks>
    /// Asserted because it is invisible otherwise: the feature works identically without it, at a
    /// hundred incidents. It stops working at a hundred thousand, which is the count an install
    /// that never prunes reaches - and before #109 that was the only direction available.
    /// </remarks>
    [Fact]
    public async Task The_assigned_to_column_is_indexed()
    {
        await pg.ResetAsync();

        await using var db = pg.CreateContext();

        var indexes = await db.Database
            .SqlQuery<string>($"""
                select indexdef as "Value" from pg_indexes
                where tablename = 'incidents' and indexname = 'ix_incidents_assigned_to'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        indexes.Should().ContainSingle().Which.Should().Contain("assigned_to IS NOT NULL");
    }

    private async Task<Guid> SeedAsync(string? assignedTo)
    {
        await using var db = pg.CreateContext();

        var incident = new Incident
        {
            CorrelationKey = $"shop/Deployment/checkout-{Guid.NewGuid():N}",
            Title = "CrashLoopBackOff on checkout",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Warning,
            State = IncidentState.Escalated,
            Target = new TargetRef { Namespace = "shop", Kind = "Pod", Name = "checkout-abc" },
            OpenedAt = Now,
            LastSignalAt = Now,
            AssignedTo = assignedTo,
            AssignedBy = assignedTo is null ? null : "flo",
            AssignedAt = assignedTo is null ? null : Now,
        };

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return incident.Id;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
