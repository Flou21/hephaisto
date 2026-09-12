using Microsoft.EntityFrameworkCore;

using Hephaisto.Agent.Options;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The sweeper's two queries, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// The sweeper is what finally gives <c>Expire()</c> and <c>ApprovalTimedOut</c> producers
/// (backlog #109, #44) - three of the ten <c>IncidentState</c> members had none, so the open
/// count could only ever rise. Its transitions are pinned in memory elsewhere; what needs a
/// database is the SELECT that decides WHICH incidents it touches, because every mistake
/// available here is a mistake about rows.
/// </para>
/// <para>
/// The predicates are asserted directly rather than through the <c>BackgroundService</c>. Driving
/// the hosted service would need a host, a timer and a way to stop it, and would test
/// <c>Task.Delay</c> rather than the thing that can be wrong.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class IncidentSweeperTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly IncidentSweepOptions Options = new()
    {
        Enabled = true,
        ExpireAfter = TimeSpan.FromDays(3),
        ApprovalTimeout = TimeSpan.FromHours(24),
        MaxPerPass = 200,
    };

    /// <summary>
    /// The case the whole feature exists for: an Observe install's escalated backlog.
    /// </summary>
    [Fact]
    public async Task An_old_escalated_incident_is_a_candidate_and_expiring_it_empties_the_open_set()
    {
        await pg.ResetAsync();

        var id = await SeedAsync(IncidentState.Escalated, lastSignal: Now.AddDays(-5));

        await using (var db = pg.CreateContext())
        {
            var candidates = await ExpiryCandidatesAsync(db);
            candidates.Should().ContainSingle().Which.Should().Be(id);

            var incident = await db.Incidents
                .Include(i => i.Events)
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            var before = incident.Events.Count;
            new IncidentStateMachine(new FixedClock(Now)).Expire(incident, "nobody came");
            db.TrackNewIncidentChildren(incident, before);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents
                .Include(i => i.Events)
                .AsNoTracking()
                .FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);

            incident.State.Should().Be(IncidentState.Expired);

            incident.Events.Should().ContainSingle(e => e.To == IncidentState.Expired)
                .Which.From.Should().Be(
                    IncidentState.Escalated,
                    "the event log is what keeps 'the agent escalated this' from being lost when it expires");

            (await db.Incidents.CountAsync(
                    i => HephaistoDbContext.OpenStates.Contains(i.State),
                    TestContext.Current.CancellationToken))
                .Should().Be(0);
        }
    }

    /// <summary>
    /// Acknowledging must protect an incident from the timer, or it is worse than useless.
    /// </summary>
    [Fact]
    public async Task An_acknowledged_incident_is_never_a_candidate_however_old()
    {
        await pg.ResetAsync();

        var id = await SeedAsync(IncidentState.Escalated, lastSignal: Now.AddDays(-90));

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.FirstAsync(i => i.Id == id, TestContext.Current.CancellationToken);
            new IncidentStateMachine(new FixedClock(Now)).Acknowledge(incident, "flo");
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            (await ExpiryCandidatesAsync(db)).Should().BeEmpty(
                "somebody said they were on it; removing it from their list on a timer is the one "
                + "behaviour that would make acknowledging pointless");
        }
    }

    /// <summary>
    /// Measured from the last SIGNAL, not from when the incident opened.
    /// </summary>
    /// <remarks>
    /// A fault still firing keeps its incident alive however old the incident is. Getting this
    /// backwards would expire live problems, which is the worst thing this component could do.
    /// </remarks>
    [Fact]
    public async Task A_still_firing_incident_is_not_a_candidate_however_long_it_has_been_open()
    {
        await pg.ResetAsync();

        await SeedAsync(IncidentState.Escalated, lastSignal: Now.AddMinutes(-1), openedAt: Now.AddDays(-60));

        await using var db = pg.CreateContext();

        (await ExpiryCandidatesAsync(db)).Should().BeEmpty();
    }

    /// <summary>
    /// AwaitingApproval belongs to the approval window, not the expiry one.
    /// </summary>
    /// <remarks>
    /// Expiring it here would skip the escalation that tells somebody the approval went
    /// unanswered - the incident would vanish instead of asking louder.
    /// </remarks>
    [Fact]
    public async Task An_incident_awaiting_approval_is_excluded_from_expiry()
    {
        await pg.ResetAsync();

        await SeedAsync(IncidentState.AwaitingApproval, lastSignal: Now.AddDays(-30));

        await using var db = pg.CreateContext();

        (await ExpiryCandidatesAsync(db)).Should().BeEmpty();
        (await ApprovalCandidatesAsync(db)).Should().HaveCount(1, "it belongs to the other pass");
    }

    [Theory]
    [InlineData(IncidentState.Resolved)]
    [InlineData(IncidentState.Suppressed)]
    [InlineData(IncidentState.Expired)]
    [InlineData(IncidentState.Closed)]
    public async Task A_settled_incident_is_never_a_candidate(IncidentState state)
    {
        await pg.ResetAsync();

        await SeedAsync(state, lastSignal: Now.AddDays(-365));

        await using var db = pg.CreateContext();

        (await ExpiryCandidatesAsync(db)).Should().BeEmpty();
    }

    /// <summary>A young incident is left alone.</summary>
    [Fact]
    public async Task An_incident_inside_the_window_is_left_alone()
    {
        await pg.ResetAsync();

        await SeedAsync(IncidentState.Escalated, lastSignal: Now.AddHours(-2));

        await using var db = pg.CreateContext();

        (await ExpiryCandidatesAsync(db)).Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------
    // These call IncidentSweeper's OWN predicates. They started out as copies, which was the
    // wrong instinct: a copied predicate tests the author's understanding, and once the two
    // drift the test passes while the sweeper selects the wrong rows.
    // ----------------------------------------------------------------------------------

    private static Task<List<Guid>> ExpiryCandidatesAsync(HephaistoDbContext db) =>
        IncidentSweeper
            .ExpiryCandidates(db, Now.Subtract(Options.ExpireAfter), Options.MaxPerPass)
            .Select(i => i.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    private static Task<List<Guid>> ApprovalCandidatesAsync(HephaistoDbContext db) =>
        IncidentSweeper
            .ApprovalTimeoutCandidates(db, Now.Subtract(Options.ApprovalTimeout), Options.MaxPerPass)
            .Select(i => i.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Writes the state directly rather than walking the diagram: several of these states are
    /// not reachable by a legal path from Detected, which is the point of testing them.
    /// </summary>
    private async Task<Guid> SeedAsync(
        IncidentState state,
        DateTimeOffset lastSignal,
        DateTimeOffset? openedAt = null)
    {
        await using var db = pg.CreateContext();

        var incident = new Incident
        {
            CorrelationKey = $"shop/Deployment/checkout-{Guid.NewGuid():N}",
            Title = "CrashLoopBackOff on checkout",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Warning,
            State = state,
            Target = new TargetRef { Namespace = "shop", Kind = "Pod", Name = "checkout-abc" },
            OpenedAt = openedAt ?? lastSignal,
            LastSignalAt = lastSignal,
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
