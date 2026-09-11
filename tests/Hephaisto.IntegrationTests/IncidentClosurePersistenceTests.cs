using Microsoft.EntityFrameworkCore;

using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// Closing and acknowledging, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// These are not a second copy of <c>IncidentStateMachineTests</c>, which already pins the
/// transitions in memory. They exist for the two things only Postgres can answer: that the four
/// new columns round-trip, and that the <c>IncidentEvent</c> the state machine appends actually
/// lands as a row.
/// </para>
/// <para>
/// That second one is a real trap rather than a hypothetical. Incident children carry
/// client-assigned <c>Guid.CreateVersion7</c> keys, so EF concludes the row already exists and
/// emits an UPDATE matching nothing - silently saving the parent and dropping the child.
/// <c>TrackNewIncidentChildren</c> has to run BEFORE any save touches the graph, and an
/// in-memory test cannot tell you whether the caller remembered.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class IncidentClosurePersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Closing_an_escalated_incident_persists_the_closer_and_the_transition()
    {
        await pg.ResetAsync();

        var id = await SeedEscalatedAsync();

        await using (var db = pg.CreateContext())
        {
            var machine = new IncidentStateMachine(new FixedClock(Now));
            var audit = new AuditRepository(db, new FixedClock(Now));

            var incident = await db.Incidents
                .Include(i => i.Events)
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            var eventsBefore = incident.Events.Count;

            machine.Close(incident, "rolled back by hand", "flo");

            // Before the save, for the reason in the class remarks.
            db.TrackNewIncidentChildren(incident, eventsBefore);

            audit.Enlist(new AuditEvent
            {
                At = Now,
                Type = "incident.closed",
                IncidentId = id,
                Actor = "flo",
                Summary = "closed from Escalated",
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // A fresh context: asserting on the one that wrote would only read its change tracker.
        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents
                .Include(i => i.Events)
                .AsNoTracking()
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            incident.State.Should().Be(IncidentState.Closed);
            incident.IsOpen.Should().BeFalse();
            incident.ClosedBy.Should().Be("flo");
            incident.ClosedAt.Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));

            incident.Events.Should().ContainSingle(e => e.To == IncidentState.Closed)
                .Which.From.Should().Be(IncidentState.Escalated);

            var audit = await db.AuditEvents
                .AsNoTracking()
                .Where(e => e.IncidentId == id && e.Type == "incident.closed")
                .ToListAsync(TestContext.Current.CancellationToken);

            audit.Should().ContainSingle().Which.Actor.Should().Be("flo");
        }
    }

    /// <summary>
    /// The open-incident count is the number this whole feature exists to bring down, and it is
    /// computed from <c>HephaistoDbContext.OpenStates</c> in SQL rather than from
    /// <c>Incident.IsOpen</c> - so this asserts the query, not the property.
    /// </summary>
    [Fact]
    public async Task A_closed_incident_leaves_the_open_set_as_the_database_counts_it()
    {
        await pg.ResetAsync();

        var id = await SeedEscalatedAsync();

        await using (var db = pg.CreateContext())
        {
            (await db.Incidents.CountAsync(
                    i => HephaistoDbContext.OpenStates.Contains(i.State),
                    TestContext.Current.CancellationToken))
                .Should().Be(1, "an escalated incident is still open");
        }

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents
                .Include(i => i.Events)
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            var before = incident.Events.Count;
            new IncidentStateMachine(new FixedClock(Now)).Close(incident, "handled", "flo");
            db.TrackNewIncidentChildren(incident, before);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            (await db.Incidents.CountAsync(
                    i => HephaistoDbContext.OpenStates.Contains(i.State),
                    TestContext.Current.CancellationToken))
                .Should().Be(0);
        }
    }

    /// <summary>Acknowledging writes two columns and no transition row.</summary>
    [Fact]
    public async Task Acknowledging_persists_the_holder_without_touching_the_state()
    {
        await pg.ResetAsync();

        var id = await SeedEscalatedAsync();

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            new IncidentStateMachine(new FixedClock(Now)).Acknowledge(incident, "flo");

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents
                .Include(i => i.Events)
                .AsNoTracking()
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            incident.AcknowledgedBy.Should().Be("flo");
            incident.AcknowledgedAt.Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));
            incident.State.Should().Be(IncidentState.Escalated, "acknowledging is not a transition");
            incident.Events.Should().NotContain(e => e.To == IncidentState.Closed);
        }
    }

    /// <summary>Reopening a closed incident clears the closure, durably.</summary>
    [Fact]
    public async Task Reinvestigating_a_closed_incident_clears_the_closure_columns()
    {
        await pg.ResetAsync();

        var id = await SeedEscalatedAsync();

        await using (var db = pg.CreateContext())
        {
            var machine = new IncidentStateMachine(new FixedClock(Now));
            var incident = await db.Incidents
                .Include(i => i.Events)
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            var before = incident.Events.Count;
            machine.Close(incident, "handled", "flo");
            machine.Reinvestigate(incident, "it came back", "flo");
            db.TrackNewIncidentChildren(incident, before);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents
                .AsNoTracking()
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            incident.State.Should().Be(IncidentState.Investigating);
            incident.ClosedBy.Should().BeNull();
            incident.ClosedAt.Should().BeNull();
        }
    }

    // ----------------------------------------------------------------------------------

    /// <summary>
    /// An incident in the state an Observe install leaves everything in: investigated, refused by
    /// the policy engine, escalated, and nobody's to close until now.
    /// </summary>
    private async Task<Guid> SeedEscalatedAsync()
    {
        await using var db = pg.CreateContext();
        var machine = new IncidentStateMachine(new FixedClock(Now));

        var incident = new Incident
        {
            CorrelationKey = "shop/Deployment/checkout",
            Title = "CrashLoopBackOff on checkout",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Warning,
            Target = new TargetRef
            {
                Namespace = "shop",
                Kind = "Pod",
                Name = "checkout-abc",
                OwnerKind = "Deployment",
                OwnerName = "checkout",
            },
            OpenedAt = Now,
            LastSignalAt = Now,
        };

        machine.Triage(incident, "seed");
        machine.BeginInvestigation(incident, "seed");
        machine.Escalate(incident, EscalationReason.PolicyDenied, "observe mode");

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return incident.Id;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
