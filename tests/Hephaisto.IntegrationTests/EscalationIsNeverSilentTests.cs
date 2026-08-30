using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The claim v0.3.0 makes: an incident cannot reach a notifiable state without an outbox row
/// being written by the same transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test the milestone is for.</b> Everything else demonstrates that a message can
/// be sent; this asserts that one is always queued, whichever code path got there. It is written
/// against the real interceptor and a real database because the property is transactional, and
/// against <see cref="IncidentStateMachine"/> rather than a hand-built row because the state
/// machine is the only thing in the system that may write <c>Incident.State</c>.
/// </para>
/// <para>
/// The failure it guards is already in the codebase's history in miniature: two escalation paths
/// in <c>IncidentTriage</c> - the self-signal arm and the storm circuit breaker - publish no live
/// event at all, and nobody noticed because nothing asserted it. An eleventh call site added next
/// year would go the same way if enqueueing were a call rather than a consequence.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class EscalationIsNeverSilentTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every escalation reason, including the two whose paths reach <c>Escalated</c> without
    /// ever touching the live-event fan-out.
    /// </summary>
    [Theory]
    [InlineData(EscalationReason.NoPlanProduced, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.PolicyDenied, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.BudgetExhausted, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.InvestigationFailed, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.LowConfidence, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.GroundingRejected, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.ClusterWideEvent, NotificationEvent.IncidentEscalated)]
    [InlineData(EscalationReason.ApprovalTimedOut, NotificationEvent.IncidentEscalated)]

    // IncidentTriage.cs:126 - escalates and publishes nothing.
    [InlineData(EscalationReason.SelfSignal, NotificationEvent.IncidentEscalated)]

    // IncidentTriage.cs:151 - the storm breaker, escalates in bulk and publishes nothing. The
    // one case where being silent is most expensive.
    [InlineData(EscalationReason.StormCircuitBreaker, NotificationEvent.IncidentEscalated)]

    // The three GiveUpAsync outcomes. "The agent tried and was wrong" is a different thing to
    // learn than "the agent declined to try", so these classify differently on purpose.
    [InlineData(EscalationReason.VerificationFailed, NotificationEvent.VerificationFailed)]
    [InlineData(EscalationReason.RollbackPerformed, NotificationEvent.VerificationFailed)]
    [InlineData(EscalationReason.Quarantined, NotificationEvent.VerificationFailed)]
    public async Task Every_escalation_leaves_an_outbox_row(EscalationReason reason, NotificationEvent expected)
    {
        await pg.ResetAsync();

        var incident = Detected();

        await using (var db = Context())
        {
            db.Incidents.Add(incident);

            new IncidentStateMachine(new FixedClock(Now)).Escalate(incident, reason, "because");

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            var row = await read.NotificationDeliveries.SingleAsync(TestContext.Current.CancellationToken);

            row.Event.Should().Be(expected);
            row.IncidentId.Should().Be(incident.Id);
            row.Status.Should().Be(DeliveryStatus.Pending);
            row.Snapshot.EscalationReason.Should().Be(reason);
        }
    }

    [Fact]
    public async Task Awaiting_approval_and_resolution_are_queued_too()
    {
        await pg.ResetAsync();

        var awaiting = Detected();
        var resolved = Detected();

        await using (var db = Context())
        {
            db.Incidents.AddRange(awaiting, resolved);

            // Walked rather than jumped: the state machine refuses illegal predecessors, so
            // the only way to reach these states is the way production reaches them. The
            // Triaging and Investigating hops on the way add transitions of their own, and
            // none of them is notifiable - which is half of what this asserts.
            var machine = new IncidentStateMachine(new FixedClock(Now));

            machine.Triage(awaiting, "detected");
            machine.BeginInvestigation(awaiting, "picked up");
            machine.AwaitApproval(awaiting, "restart_pod needs a human");

            machine.Triage(resolved, "detected");
            machine.BeginInvestigation(resolved, "picked up");
            machine.Resolve(resolved, "verified", IncidentStateMachine.VerifierActor);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            var rows = await read.NotificationDeliveries.ToListAsync(TestContext.Current.CancellationToken);

            rows.Should().HaveCount(2);
            rows.Select(r => r.Event).Should().BeEquivalentTo(
                [NotificationEvent.ApprovalRequired, NotificationEvent.IncidentResolved]);
        }
    }

    [Fact]
    public async Task One_row_per_channel_so_one_outage_cannot_block_the_other()
    {
        await pg.ResetAsync();

        var incident = Detected();

        await using (var db = Context(Options("webhook", "teams")))
        {
            db.Incidents.Add(incident);
            new IncidentStateMachine(new FixedClock(Now))
                .Escalate(incident, EscalationReason.NoPlanProduced, "because");

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            var rows = await read.NotificationDeliveries.ToListAsync(TestContext.Current.CancellationToken);

            rows.Select(r => r.Channel).Should().BeEquivalentTo(["webhook", "teams"]);

            // Same message, different rows: the retry state has to be per-channel or a Teams
            // outage would hold up the webhook behind it.
            rows.Select(r => r.Id).Distinct().Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task The_agent_working_is_not_a_notification()
    {
        await pg.ResetAsync();

        var incident = Detected();

        await using (var db = Context())
        {
            db.Incidents.Add(incident);

            var machine = new IncidentStateMachine(new FixedClock(Now));
            machine.Triage(incident, "detected");
            machine.BeginInvestigation(incident, "the agent picked it up");

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            // A channel that reported every step is one people mute, and the escalation gets
            // muted along with it.
            (await read.NotificationDeliveries.CountAsync(TestContext.Current.CancellationToken))
                .Should().Be(0);
        }
    }

    [Fact]
    public async Task With_no_routes_configured_nothing_is_queued()
    {
        await pg.ResetAsync();

        var incident = Detected();

        await using (var db = Context(new NotificationOptions()))
        {
            db.Incidents.Add(incident);
            new IncidentStateMachine(new FixedClock(Now))
                .Escalate(incident, EscalationReason.NoPlanProduced, "because");

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            // The shipped default. A stock install notifies nowhere, in the same direction as
            // an empty AllowedNamespaces and mode: Observe.
            (await read.NotificationDeliveries.CountAsync(TestContext.Current.CancellationToken))
                .Should().Be(0);

            // ...and the incident itself is entirely unaffected.
            (await read.Incidents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        }
    }

    [Fact]
    public async Task A_rolled_back_escalation_queues_nothing()
    {
        await pg.ResetAsync();

        var incident = Detected();

        await using (var db = Context())
        {
            await using var tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            db.Incidents.Add(incident);
            new IncidentStateMachine(new FixedClock(Now))
                .Escalate(incident, EscalationReason.NoPlanProduced, "because");

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var read = pg.CreateContext())
        {
            // The other direction of the same guarantee: no page about an escalation that was
            // never committed.
            (await read.NotificationDeliveries.CountAsync(TestContext.Current.CancellationToken))
                .Should().Be(0);
        }
    }

    private static NotificationOptions Options(params string[] channels) => new()
    {
        BaseUrl = "https://hephaisto.example",
        Routes =
        [
            .. channels.Select(c => new NotificationRoute
            {
                Channel = c,
                Events =
                [
                    NotificationEvent.IncidentEscalated,
                    NotificationEvent.VerificationFailed,
                    NotificationEvent.ApprovalRequired,
                    NotificationEvent.IncidentResolved,
                ],
            }),
        ],
    };

    private HephaistoDbContext Context(NotificationOptions? options = null)
    {
        var interceptor = new NotificationEnqueueInterceptor(
            new StaticOptions(options ?? Options("webhook")),
            new FixedClock(Now),
            NullLogger<NotificationEnqueueInterceptor>.Instance);

        var built = new DbContextOptionsBuilder<HephaistoDbContext>()
            .UseNpgsql(pg.ConnectionString, o => o.UseVector())
            .AddInterceptors(interceptor)
            .Options;

        return new HephaistoDbContext(built);
    }

    private static Incident Detected() => new()
    {
        CorrelationKey = "hephaisto-chaos/Deployment/api",
        Title = "api is crash looping",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        State = IncidentState.Detected,
        Target = new TargetRef { Namespace = "hephaisto-chaos", Kind = "Deployment", Name = "api" },
        OpenedAt = Now,
        LastSignalAt = Now,
    };

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StaticOptions(NotificationOptions value) : IOptionsMonitor<NotificationOptions>
    {
        public NotificationOptions CurrentValue => value;

        public NotificationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NotificationOptions, string?> listener) => null;
    }
}
