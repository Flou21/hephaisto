using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The outbox, against a real database, because the property it exists for is a transactional
/// one and an in-memory test cannot observe it.
/// </summary>
/// <remarks>
/// The claim this milestone makes is that an incident cannot reach <c>Escalated</c> without a
/// delivery existing to carry that outward. That claim rests on the two being written by one
/// <c>SaveChangesAsync</c> - so the test that matters here is the rollback one: if the state
/// change is discarded, the delivery must be discarded with it, or the system would page a human
/// about an escalation that never happened.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class NotificationOutboxTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_enlisted_delivery_and_its_incident_commit_together()
    {
        await pg.ResetAsync();

        var incident = Incident();

        await using (var db = pg.CreateContext())
        {
            db.Incidents.Add(incident);
            new NotificationOutbox(db, new FixedClock(Now)).Enlist(Delivery(incident.Id));

            // One save, both rows. This is the whole design.
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            var row = await read.NotificationDeliveries
                .SingleAsync(d => d.IncidentId == incident.Id, TestContext.Current.CancellationToken);

            row.Status.Should().Be(DeliveryStatus.Pending);
            row.Channel.Should().Be("webhook");
            row.Snapshot.Kind.Should().Be(SignalKind.CrashLoopBackOff);
            row.Snapshot.Title.Should().Be("api is crash looping");
        }
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_orphan_delivery()
    {
        await pg.ResetAsync();

        var incident = Incident();

        await using (var db = pg.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            db.Incidents.Add(incident);
            new NotificationOutbox(db, new FixedClock(Now)).Enlist(Delivery(incident.Id));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            // Nobody is paged about an escalation that did not happen.
            (await read.NotificationDeliveries.CountAsync(TestContext.Current.CancellationToken))
                .Should().Be(0);
            (await read.Incidents.CountAsync(TestContext.Current.CancellationToken))
                .Should().Be(0);
        }
    }

    [Fact]
    public async Task The_snapshot_survives_a_round_trip_through_jsonb()
    {
        await pg.ResetAsync();

        var incident = Incident();
        var delivery = Delivery(incident.Id);

        await using (var db = pg.CreateContext())
        {
            db.Incidents.Add(incident);
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            var row = await read.NotificationDeliveries.SingleAsync(TestContext.Current.CancellationToken);

            // Value equality on the record, so a field silently dropped by the converter fails
            // here rather than showing up as a card with an empty line in it.
            row.Snapshot.Should().Be(delivery.Snapshot);
        }
    }

    [Fact]
    public async Task Due_returns_only_pending_rows_whose_backoff_has_elapsed()
    {
        await pg.ResetAsync();

        var incident = Incident();

        await using (var db = pg.CreateContext())
        {
            db.Incidents.Add(incident);

            var ready = Delivery(incident.Id);
            ready.NextAttemptAt = Now.AddMinutes(-1);

            var backingOff = Delivery(incident.Id);
            backingOff.NextAttemptAt = Now.AddMinutes(5);

            var alreadySent = Delivery(incident.Id);
            alreadySent.Status = DeliveryStatus.Delivered;
            alreadySent.NextAttemptAt = Now.AddMinutes(-10);

            var gaveUp = Delivery(incident.Id);
            gaveUp.Status = DeliveryStatus.Failed;
            gaveUp.NextAttemptAt = Now.AddMinutes(-10);

            db.NotificationDeliveries.AddRange(ready, backingOff, alreadySent, gaveUp);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var due = await new NotificationOutbox(db, new FixedClock(Now))
                .DueAsync(20, Now, TestContext.Current.CancellationToken);

            due.Should().ContainSingle();
            due[0].NextAttemptAt.Should().Be(Now.AddMinutes(-1));
        }
    }

    [Fact]
    public async Task The_budget_counts_what_the_rate_limit_needs()
    {
        await pg.ResetAsync();

        var incident = Incident();

        await using (var db = pg.CreateContext())
        {
            db.Incidents.Add(incident);

            var sent = Delivery(incident.Id);
            sent.Status = DeliveryStatus.Delivered;
            sent.DeliveredAt = Now.AddMinutes(-5);

            var stale = Delivery(incident.Id);
            stale.Status = DeliveryStatus.Delivered;
            stale.DeliveredAt = Now.AddHours(-3);

            var otherChannel = Delivery(incident.Id);
            otherChannel.Channel = "teams";
            otherChannel.Status = DeliveryStatus.Delivered;
            otherChannel.DeliveredAt = Now.AddMinutes(-5);

            var swallowed = Delivery(incident.Id);
            swallowed.Status = DeliveryStatus.Suppressed;
            swallowed.CreatedAt = Now.AddMinutes(-2);

            db.NotificationDeliveries.AddRange(sent, stale, otherChannel, swallowed);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var budget = await new NotificationOutbox(db, new FixedClock(Now))
                .BudgetAsync("webhook", "hephaisto-chaos/Deployment/api", Now, TestContext.Current.CancellationToken);

            // The three-hour-old one is outside the window; the Teams one is another channel.
            budget.DeliveredOnChannelLastHour.Should().Be(1);
            budget.LastDeliveryForKey.Should().Be(Now.AddMinutes(-5));
            budget.SuppressedSinceLastDelivery.Should().Be(1);
        }
    }

    [Fact]
    public async Task A_verbose_endpoint_error_is_truncated_rather_than_refused()
    {
        await pg.ResetAsync();

        var incident = Incident();
        var delivery = Delivery(incident.Id);

        await using (var db = pg.CreateContext())
        {
            db.Incidents.Add(incident);
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // An HTML error page, which is what a proxy in front of a webhook actually returns.
            await new NotificationOutbox(db, new FixedClock(Now)).MarkFailedAsync(
                delivery,
                new string('x', 5_000),
                TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            var row = await read.NotificationDeliveries.SingleAsync(TestContext.Current.CancellationToken);

            row.Status.Should().Be(DeliveryStatus.Failed);
            row.LastError.Should().HaveLength(HephaistoDbContext.MaxErrorLength);
        }
    }

    private static Incident Incident() => new()
    {
        CorrelationKey = "hephaisto-chaos/Deployment/api",
        Title = "api is crash looping",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        State = IncidentState.Escalated,
        Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Deployment", Name = "api" },
        OpenedAt = Now,
        LastSignalAt = Now,
    };

    private static NotificationDelivery Delivery(Guid incidentId) => new()
    {
        Event = NotificationEvent.IncidentEscalated,
        IncidentId = incidentId,
        Channel = "webhook",
        CorrelationKey = "hephaisto-chaos/Deployment/api",
        Status = DeliveryStatus.Pending,
        CreatedAt = Now,
        NextAttemptAt = Now,
        Snapshot = new NotificationSnapshot
        {
            Event = NotificationEvent.IncidentEscalated,
            IncidentId = incidentId,
            CorrelationKey = "hephaisto-chaos/Deployment/api",
            Title = "api is crash looping",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Critical,
            State = IncidentState.Escalated,
            PreviousState = IncidentState.Investigating,
            EscalationReason = EscalationReason.NoPlanProduced,
            Namespace = "hephaisto-chaos",
            Target = "hephaisto-chaos/Deployment/api",
            Summary = "the image tag does not exist",
            Reason = "no plan produced",
            At = Now,
        },
    };

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
