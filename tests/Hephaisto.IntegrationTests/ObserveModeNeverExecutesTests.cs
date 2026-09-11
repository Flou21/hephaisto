using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Core.Safety;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The promise an Observe-mode install is made on: nothing executes, ever.
/// </summary>
/// <remarks>
/// <para>
/// There are two independent gates, and only one of them had coverage. <c>PolicyEngine</c>
/// denies on <see cref="AgentMode.Observe"/> and <c>PolicyEngineTests</c> asserts it - but the
/// policy engine is advisory. The gate that actually stands between a plan and a
/// <c>kubectl delete</c> is the mode re-resolution inside
/// <see cref="IActionRepository.TryAdmitActionAsync"/>'s Serializable transaction, and it was
/// asserted only indirectly, through the resolver's own unit tests.
/// </para>
/// <para>
/// That distinction is the whole reason this file exists. An in-memory test of the resolver
/// proves the arms combine correctly; it does not prove the repository consults them, that the
/// refusal is durable, or that it survives the one request shape which is designed to bypass
/// every other gate - a rollback. These run against a real Postgres because the refusal and
/// its audit row are committed together, and an ORM-only test would not prove that either.
/// </para>
/// <para>
/// Each test asserts on the refusal itself rather than on some downstream absence, so a
/// failure names the gate that opened rather than the symptom.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ObserveModeNeverExecutesTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Workload = "shop/Deployment/checkout";

    [Theory]
    [InlineData("Observe")]
    [InlineData("observe")]
    [InlineData("Off")]
    public async Task The_admission_transaction_refuses_every_action_and_says_which_arm_bound_it(string envMode)
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using var db = pg.CreateContext();

        var admission = await Repository(db, envMode).TryAdmitActionAsync(
            Action(incidentId), new PolicyOptions(), TestContext.Current.CancellationToken);

        admission.Admitted.Should().BeFalse();
        admission.Refusal.Should().Be(AdmissionRefusal.KillSwitch);

        // The reason has to name the mode AND the arm that decided it. An operator reading
        // "refused" with no attribution cannot tell a working kill switch from a broken agent,
        // and the arms exist precisely so that whichever one they reached for is the one that
        // answers.
        admission.Reasons.Should().ContainSingle()
            .Which.Should().ContainAll("no action may execute", KillSwitch.EnvironmentArm);
    }

    /// <summary>
    /// The request shape built to bypass everything else.
    /// </summary>
    /// <remarks>
    /// A rollback skips the budget and cooldown gates by design - "you must always be able to
    /// undo" - and <c>PolicyEngine</c> additionally exempts it from the grounding check. That
    /// makes it the one action which, if the kill switch were consulted anywhere but here,
    /// could reach the cluster with every other gate standing open.
    /// </remarks>
    [Fact]
    public async Task A_rollback_bypasses_the_budgets_and_is_still_refused()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using var db = pg.CreateContext();

        var rollback = Action(incidentId);
        rollback.IsRollbackOf = Guid.CreateVersion7();

        var admission = await Repository(db, "Observe").TryAdmitActionAsync(
            rollback, new PolicyOptions(), TestContext.Current.CancellationToken);

        admission.Admitted.Should().BeFalse();
        admission.Refusal.Should().Be(AdmissionRefusal.KillSwitch);
    }

    /// <summary>
    /// A refusal leaves an audit trail and no action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter and they pull in opposite directions. The transaction is rolled
    /// BACK, so no <c>agent_actions</c> row survives - a refused action must not exist as a
    /// row at all, because every budget window in this class counts rows and a refusal that
    /// consumed budget would let a stream of denials starve the agent. The audit event is
    /// then appended outside that transaction, which is what makes "the agent declined to
    /// act, and why" answerable months later rather than only from a log line that has since
    /// rotated.
    /// </para>
    /// <para>
    /// So this asserts the absence and the record together. Either one alone passes for the
    /// wrong reason: no row and no audit event is a silent refusal, and a row plus an audit
    /// event is a refusal that spends budget.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_refusal_leaves_an_audit_trail_and_no_action_row()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();
        var actionId = Guid.CreateVersion7();

        await using (var db = pg.CreateContext())
        {
            var action = Action(incidentId);
            action.Id = actionId;

            await Repository(db, "Observe").TryAdmitActionAsync(
                action, new PolicyOptions(), TestContext.Current.CancellationToken);
        }

        // A fresh context: asserting against the one that wrote the rows would only read them
        // back out of its own change tracker.
        await using (var db = pg.CreateContext())
        {
            (await db.AgentActions.AsNoTracking().AnyAsync(
                    a => a.Id == actionId, TestContext.Current.CancellationToken))
                .Should().BeFalse("the admitting transaction was rolled back, so no action exists");

            var audit = await db.AuditEvents
                .AsNoTracking()
                .Where(e => e.ActionId == actionId)
                .ToListAsync(TestContext.Current.CancellationToken);

            audit.Should().ContainSingle().Which.Type.Should().Be("action.refused");
            audit.Single().Summary.Should().Contain(nameof(AdmissionRefusal.KillSwitch));
        }
    }

    /// <summary>
    /// The env arm alone is enough, whatever the database says.
    /// </summary>
    /// <remarks>
    /// <see cref="ModeResolver"/> takes the most restrictive arm, so an unlatched row is
    /// deliberately SILENT rather than declaring Auto. This asserts the composition from the
    /// repository's side: the chart's <c>mode: Observe</c> binds admission even with a mode
    /// row present and unlatched, which is the state every migrated database is in.
    /// </remarks>
    [Fact]
    public async Task An_unlatched_mode_row_cannot_lift_Observe()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using (var db = pg.CreateContext())
        {
            // The row is not inserted: InitialCreate SEEDS the singleton, which is the whole
            // reason the arm has to be silent rather than declaring its mode column. Updating
            // it is therefore the state a real migrated database is actually in.
            var row = await db.AgentModeRows.SingleAsync(
                m => m.Id == AgentModeRow.SingletonId, TestContext.Current.CancellationToken);

            row.RunawayLatched = false;
            row.ChangedAt = Now;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = pg.CreateContext())
        {
            var admission = await Repository(db, "Observe").TryAdmitActionAsync(
                Action(incidentId), new PolicyOptions(), TestContext.Current.CancellationToken);

            admission.Admitted.Should().BeFalse();
            admission.Refusal.Should().Be(AdmissionRefusal.KillSwitch);
        }
    }

    /// <summary>
    /// A typo is not permission.
    /// </summary>
    /// <remarks>
    /// <c>HEPHAISTO_MODE=atuo</c> must read as Observe rather than as silence. Silence would
    /// let the remaining arms decide, and on a cluster where the ConfigMap is absent that is
    /// how a misspelling becomes an autonomy upgrade.
    /// </remarks>
    [Fact]
    public async Task A_malformed_mode_is_refused_rather_than_ignored()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using var db = pg.CreateContext();

        var admission = await Repository(db, "atuo").TryAdmitActionAsync(
            Action(incidentId), new PolicyOptions(), TestContext.Current.CancellationToken);

        admission.Admitted.Should().BeFalse();
        admission.Refusal.Should().Be(AdmissionRefusal.KillSwitch);
    }

    /// <summary>
    /// The positive control, and the test that gives every assertion above its meaning.
    /// </summary>
    /// <remarks>
    /// Everything else here asserts a refusal, and a refusal is the easy thing to get by
    /// accident - a missing incident, an unseeded fixture, a typo in a workload key all
    /// produce one. Without this, a suite of five passing refusals is equally consistent with
    /// "the kill switch works" and "admission refuses everything for an unrelated reason".
    /// Flipping ONLY the env arm and getting an admission is what isolates the mode as the
    /// cause.
    /// </remarks>
    [Fact]
    public async Task The_same_action_in_Auto_is_admitted_which_is_what_makes_the_refusals_mean_something()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using var db = pg.CreateContext();

        var admission = await Repository(db, "Auto").TryAdmitActionAsync(
            Action(incidentId), new PolicyOptions(), TestContext.Current.CancellationToken);

        admission.Refusal.Should().Be(AdmissionRefusal.None);
        admission.Admitted.Should().BeTrue(
            "only the env arm changed, so anything still refusing here would mean the Observe "
            + "assertions above were passing for some other reason");
    }

    // ----------------------------------------------------------------------------------

    /// <summary>
    /// A real <see cref="KillSwitch"/> rather than a stub of it, built the way the pod builds
    /// it: the mode arrives on the environment variable the chart sets from
    /// <c>.Values.mode</c>, and no ConfigMap is projected.
    /// </summary>
    private ActionRepository Repository(HephaistoDbContext db, string envMode)
    {
        // No row: the database arm reads that as UNREADABLE, which floors at Observe. The
        // arm is not what is under test here - the env arm is - and this keeps the database
        // out of the resolution so a failure cannot be blamed on it.
        var store = new NoModeRow();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HEPHAISTO_MODE"] = envMode })
            .Build();

        var scopes = new ServiceCollection()
            .AddScoped(_ => store)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var killSwitch = new KillSwitch(
            configuration,
            new OptionsStub<KillSwitchOptions>(new KillSwitchOptions { SwitchDirectory = null }),
            scopes,
            NullLogger<KillSwitch>.Instance);

        return new ActionRepository(
            killSwitch,
            db,
            new AuditRepository(db, new FixedClock(Now)),
            new FixedClock(Now),
            new OptionsStub<PersistenceOptions>(new PersistenceOptions()),
            NullLogger<ActionRepository>.Instance);
    }

    private static AgentAction Action(Guid incidentId) =>
        new()
        {
            IncidentId = incidentId,
            Type = ActionType.RestartPod,
            Risk = RiskTier.Low,
            State = ActionState.Proposed,
            Decision = PolicyDecision.Allow,
            Target = new TargetRef
            {
                Namespace = "shop",
                Kind = "Pod",
                Name = "checkout-abc",
                OwnerKind = "Deployment",
                OwnerName = "checkout",
            },
        };

    private async Task<Guid> SeedIncidentAsync()
    {
        await using var db = pg.CreateContext();

        var incident = new Incident
        {
            CorrelationKey = Workload,
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

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return incident.Id;
    }

    private sealed class OptionsStub<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Hand-rolled rather than a mock: this project does not reference NSubstitute,
    /// and adding it for four throwing members would be the larger change.</summary>
    private sealed class NoModeRow : IAgentModeStore
    {
        public Task<AgentModeRow> GetAsync(CancellationToken ct) =>
            Task.FromResult(new AgentModeRow { Id = AgentModeRow.SingletonId });

        public Task<AgentModeRow?> GetRowOrDefaultAsync(CancellationToken ct) =>
            Task.FromResult<AgentModeRow?>(null);

        public Task LatchAsync(string reason, CancellationToken ct) =>
            throw new NotSupportedException("the test never latches");

        public Task ReArmAsync(string actor, CancellationToken ct) =>
            throw new NotSupportedException("the test never re-arms");
    }
}
