using System.Diagnostics.Metrics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Hephaisto.Agent;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Pipeline;
using Hephaisto.Agent.Web;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// One alert, one incident, for as long as it fires (#129, #130, #147, #149).
/// </summary>
/// <remarks>
/// Triage against a real database, because every rule here is a query: "the open incident with
/// this fingerprint whatever its age", "the row for this alert instance", "anything else on it
/// still firing". The pager suite asserts the same sentences on an installed agent; these pin
/// them where a failure names the query.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AlertLifecycleTests(PostgresFixture pg) : IDisposable
{
    private const string Cluster = "lifecycle-test";

    private readonly MutableClock clock = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));

    private readonly ServiceProvider meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

    private static readonly IngestOptions Options = new()
    {
        ClusterName = Cluster,
        BurstWindow = TimeSpan.FromMinutes(5),
        CorrelationWindow = TimeSpan.FromMinutes(10),
        FlapThreshold = 3,
        FlapWindow = TimeSpan.FromHours(1),
        ReopenWindow = TimeSpan.FromHours(24),
    };

    public void Dispose() => meters.Dispose();

    [Fact]
    public async Task Three_firings_are_one_incident_with_one_row_counted_three_times()
    {
        await pg.ResetAsync();

        var first = await TriageAsync(Firing("Orders", ("deployment", "orders")));
        clock.Advance(TimeSpan.FromMinutes(1));
        await TriageAsync(Firing("Orders", ("deployment", "orders")));
        clock.Advance(TimeSpan.FromMinutes(1));
        var third = await TriageAsync(Firing("Orders", ("deployment", "orders")));

        third.IncidentId.Should().Be(first.IncidentId);
        third.Outcome.Should().Be(TriageOutcome.Deduplicated);

        await using var db = pg.CreateContext();
        var rows = await db.Signals.Where(s => s.IncidentId == first.IncidentId).ToListAsync(Ct);
        rows.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    [Fact]
    public async Task A_repeat_hours_later_is_absorbed_while_the_incident_is_open()
    {
        await pg.ResetAsync();

        var first = await TriageAsync(Firing("Search", ("deployment", "search")));
        await EscalateAsync(first.IncidentId);
        clock.Advance(TimeSpan.FromHours(12));

        var repeat = await TriageAsync(Firing("Search", ("deployment", "search")));

        repeat.IncidentId.Should().Be(first.IncidentId, "a repeat_interval is hours, and it is the same alert");
        (await CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_resolve_closes_the_incident_as_Alertmanager()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Firing("Mailer", ("deployment", "mailer")));
        await EscalateAsync(opened.IncidentId);

        var cleared = await TriageAsync(Resolved("Mailer", ("deployment", "mailer")));

        cleared.Outcome.Should().Be(TriageOutcome.Cleared);
        var incident = await LoadAsync(opened.IncidentId);
        incident.State.Should().Be(IncidentState.Closed);
        incident.ClosedBy.Should().Be(IncidentStateMachine.AlertmanagerActor);
        (await CountAsync()).Should().Be(1, "a resolve opens nothing");
    }

    [Fact]
    public async Task One_pod_clearing_leaves_the_incident_open_while_its_sibling_fires()
    {
        await pg.ResetAsync();

        var a = await TriageAsync(Firing("Web", ("deployment", "web"), ("pod", "web-1")));
        var b = await TriageAsync(Firing("Web", ("deployment", "web"), ("pod", "web-2")));
        b.IncidentId.Should().Be(a.IncidentId);
        await EscalateAsync(a.IncidentId);

        await TriageAsync(Resolved("Web", ("deployment", "web"), ("pod", "web-1")));
        (await LoadAsync(a.IncidentId)).State.Should().Be(IncidentState.Escalated);

        await TriageAsync(Resolved("Web", ("deployment", "web"), ("pod", "web-2")));
        (await LoadAsync(a.IncidentId)).State.Should().Be(IncidentState.Closed);
    }

    [Fact]
    public async Task A_resolve_for_an_alert_never_seen_opens_nothing()
    {
        await pg.ResetAsync();

        var result = await TriageAsync(Resolved("Billing", ("deployment", "billing")));

        result.Outcome.Should().Be(TriageOutcome.Ignored);
        (await CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_refire_inside_the_window_reopens_the_same_incident_and_clears_the_acknowledgement()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Firing("Payments", ("deployment", "payments")));
        await EscalateAsync(opened.IncidentId, acknowledgedBy: "flo");
        await TriageAsync(Resolved("Payments", ("deployment", "payments")));
        clock.Advance(TimeSpan.FromHours(3));

        var again = await TriageAsync(Firing("Payments", ("deployment", "payments")));

        again.IncidentId.Should().Be(opened.IncidentId);
        again.Outcome.Should().Be(TriageOutcome.Investigate, "a reopened incident is decided like a new one");
        var incident = await LoadAsync(opened.IncidentId);
        incident.State.Should().Be(IncidentState.Investigating);
        incident.ReopenedAt.Should().Be(clock.UtcNow);
        incident.AcknowledgedBy.Should().BeNull();
        (await CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_refire_after_the_window_is_a_new_incident()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Firing("Reports", ("deployment", "reports")));
        await EscalateAsync(opened.IncidentId);
        await TriageAsync(Resolved("Reports", ("deployment", "reports")));
        clock.Advance(TimeSpan.FromHours(25));

        var again = await TriageAsync(Firing("Reports", ("deployment", "reports")));

        again.IncidentId.Should().NotBe(opened.IncidentId);
        (await CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_person_closing_a_still_firing_alert_is_not_paged_by_its_repeats()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Firing("Ledger", ("deployment", "ledger")));
        await EscalateAsync(opened.IncidentId);
        await CloseAsync(opened.IncidentId, "flo");
        clock.Advance(TimeSpan.FromHours(30));

        var repeat = await TriageAsync(Firing("Ledger", ("deployment", "ledger")));

        repeat.IncidentId.Should().Be(opened.IncidentId);
        repeat.Outcome.Should().Be(TriageOutcome.Deduplicated);
        (await LoadAsync(opened.IncidentId)).State.Should().Be(IncidentState.Closed);
        (await CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Four_clusters_are_four_incidents_and_none_is_suppressed()
    {
        await pg.ResetAsync();

        foreach (var cluster in new[] { "a", "b", "c", "d" })
        {
            await TriageAsync(Firing("Api", ("cluster", cluster), ("deployment", "api")));
        }

        await using var db = pg.CreateContext();
        var states = await db.Incidents.Select(i => i.State).ToListAsync(Ct);
        states.Should().HaveCount(4).And.NotContain(IncidentState.Suppressed);
    }

    [Fact]
    public async Task A_flapping_alert_is_escalated_not_suppressed()
    {
        await pg.ResetAsync();

        for (var i = 0; i < 4; i++)
        {
            await TriageAsync(Firing($"Flappy{i}", ("deployment", "flappy")));
            clock.Advance(TimeSpan.FromMinutes(11));
        }

        await using var db = pg.CreateContext();
        var states = await db.Incidents.OrderBy(x => x.OpenedAt).Select(x => x.State).ToListAsync(Ct);
        states.Should().NotContain(IncidentState.Suppressed, "an alert suppressed here is a page nobody receives (#147)");
        states.Last().Should().Be(IncidentState.Escalated);
    }

    [Fact]
    public async Task An_incident_with_an_action_in_flight_is_left_to_the_verifier()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Firing("Cart", ("deployment", "cart")));

        await using (var db = pg.CreateContext())
        {
            var incident = await db.Incidents.FirstAsync(i => i.Id == opened.IncidentId, Ct);
            var machine = new IncidentStateMachine(clock);
            machine.BeginActing(incident, "policy allowed");
            db.TrackNewIncidentChildren(incident, 0);
            await db.SaveChangesAsync(Ct);
        }

        await TriageAsync(Resolved("Cart", ("deployment", "cart")));

        (await LoadAsync(opened.IncidentId)).State.Should().Be(IncidentState.Acting);
    }

    // --- plumbing -------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // The watcher's resolve (#158)
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_healed_workload_closes_the_incident_the_watcher_opened()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Watched(SignalKind.OomKilled, "OOMKilled"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await TriageAsync(Watched(SignalKind.CrashLoopBackOff, "BackOff"));
        await EscalateAsync(opened.IncidentId);
        clock.Advance(TimeSpan.FromHours(2));

        var healed = await TriageAsync(Watched(SignalKind.OomKilled, "OOMKilled", SignalStatus.Resolved));

        healed.Outcome.Should().Be(TriageOutcome.Cleared);
        healed.IncidentId.Should().Be(opened.IncidentId);

        var incident = await LoadAsync(opened.IncidentId);
        incident.State.Should().Be(IncidentState.Closed);
        incident.ClosedBy.Should().Be(IncidentStateMachine.WatcherActor);

        await using var db = pg.CreateContext();
        var rows = await db.Signals.Where(s => s.IncidentId == opened.IncidentId).ToListAsync(Ct);
        rows.Should().HaveCount(2, "the resolve is a statement about the rows that exist, not a third one");
        rows.Should().OnlyContain(s => s.Status == SignalStatus.Resolved, "one workload healed, whatever it was reported as");
        (await new IncidentRepository(db, clock).GetOpenWatchedAsync(10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_watcher_is_asked_about_what_it_opened_and_nothing_else()
    {
        await pg.ResetAsync();

        var watched = await TriageAsync(Watched(SignalKind.CrashLoopBackOff, "CrashLoopBackOff"));
        await TriageAsync(Firing("Mailer", ("deployment", "mailer")));

        await using var db = pg.CreateContext();
        var open = await new IncidentRepository(db, clock).GetOpenWatchedAsync(10, Ct);

        var only = open.Should().ContainSingle().Subject;
        only.Id.Should().Be(watched.IncidentId);
        only.Target.OwnerName.Should().Be("orders");
        only.Fingerprint.Should().Be(Watched(SignalKind.CrashLoopBackOff, "CrashLoopBackOff").Fingerprint);
    }

    [Fact]
    public async Task A_healed_workload_with_an_alert_still_firing_stays_open()
    {
        await pg.ResetAsync();

        var opened = await TriageAsync(Watched(SignalKind.CrashLoopBackOff, "CrashLoopBackOff"));
        clock.Advance(TimeSpan.FromMinutes(1));
        var alert = await TriageAsync(Firing("OrdersDown", ("namespace", "shop"), ("deployment", "orders")));
        alert.IncidentId.Should().Be(opened.IncidentId, "the alert is a facet of the same workload");

        var healed = await TriageAsync(Watched(SignalKind.CrashLoopBackOff, "CrashLoopBackOff", SignalStatus.Resolved));

        healed.Outcome.Should().Be(TriageOutcome.Cleared);
        (await LoadAsync(opened.IncidentId)).State.Should().NotBe(IncidentState.Closed, "Alertmanager has not said it is over");

        var cleared = await TriageAsync(Resolved("OrdersDown", ("namespace", "shop"), ("deployment", "orders")));

        cleared.Outcome.Should().Be(TriageOutcome.Cleared);
        (await LoadAsync(opened.IncidentId)).State.Should().Be(IncidentState.Closed);
    }

    [Fact]
    public async Task A_resolve_for_nothing_the_watcher_has_open_is_ignored()
    {
        await pg.ResetAsync();

        var healed = await TriageAsync(Watched(SignalKind.OomKilled, "OOMKilled", SignalStatus.Resolved));

        healed.Outcome.Should().Be(TriageOutcome.Ignored);
        (await CountAsync()).Should().Be(0);
    }

    private static Signal Watched(SignalKind kind, string reason, SignalStatus status = SignalStatus.Firing)
    {
        var signal = new Signal
        {
            Source = SignalSource.KubernetesWatch,
            Kind = kind,
            Status = status,
            Severity = Severity.Critical,
            Reason = reason,
            Message = status == SignalStatus.Resolved ? "1 pod(s) of shop/Deployment/orders running and ready" : reason,
            Target = new TargetRef
            {
                Cluster = Cluster,
                Namespace = "shop",
                Kind = "Pod",
                Name = "orders-5d9-x",
                OwnerKind = "Deployment",
                OwnerName = "orders",
            },
        };

        signal.Fingerprint = SignalFingerprinter.Compute(signal, Cluster);
        return signal;
    }

    private static Signal Firing(string name, params (string Key, string Value)[] labels) => Map("firing", name, labels);

    private static Signal Resolved(string name, params (string Key, string Value)[] labels) => Map("resolved", name, labels);

    private static Signal Map(string status, string name, (string Key, string Value)[] labels)
    {
        var all = labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        all["alertname"] = name;
        all.TryAdd("namespace", "shop");

        var signal = AlertmanagerEndpoints.ToSignal(
            new AlertmanagerAlert { Status = status, Labels = all, StartsAt = DateTimeOffset.UtcNow },
            new AlertmanagerWebhook());

        // What SignalIngestPipeline does before triage.
        if (signal.Target.Cluster.Length == 0)
        {
            signal.Target.Cluster = Cluster;
        }

        signal.Fingerprint = SignalFingerprinter.Compute(signal, Cluster);
        return signal;
    }

    private async Task<TriageResult> TriageAsync(Signal signal)
    {
        signal.FirstSeen = clock.UtcNow;
        signal.LastSeen = clock.UtcNow;

        await using var db = pg.CreateContext();
        var triage = new IncidentTriage(
            new IncidentRepository(db, clock),
            new AuditRepository(db, clock),
            new IncidentStateMachine(clock),
            clock,
            new StaticMonitor<IngestOptions>(Options),
            new HephaistoMetrics(meters.GetRequiredService<IMeterFactory>()),
            new NullGrafanaAnnotator(),
            NullLogger<IncidentTriage>.Instance);

        return await triage.TriageAsync(signal, Ct);
    }

    /// <summary>What the coordinator does when the model gives up: the state Observe leaves.</summary>
    private async Task EscalateAsync(Guid id, string? acknowledgedBy = null)
    {
        await using var db = pg.CreateContext();
        var incident = await db.Incidents.FirstAsync(i => i.Id == id, Ct);
        var machine = new IncidentStateMachine(clock);

        if (incident.State == IncidentState.Investigating)
        {
            machine.Escalate(incident, EscalationReason.LowConfidence);
            db.TrackNewIncidentChildren(incident, 0);
        }

        if (acknowledgedBy is not null)
        {
            machine.Acknowledge(incident, acknowledgedBy);
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task CloseAsync(Guid id, string by)
    {
        await using var db = pg.CreateContext();
        var incident = await db.Incidents.FirstAsync(i => i.Id == id, Ct);
        new IncidentStateMachine(clock).Close(incident, "handled", by);
        db.TrackNewIncidentChildren(incident, 0);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Incident> LoadAsync(Guid id)
    {
        await using var db = pg.CreateContext();
        return await db.Incidents.AsNoTracking().FirstAsync(i => i.Id == id, Ct);
    }

    private async Task<int> CountAsync()
    {
        await using var db = pg.CreateContext();
        return await db.Incidents.CountAsync(Ct);
    }

    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
