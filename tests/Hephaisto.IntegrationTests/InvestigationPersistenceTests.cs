using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The change-tracking machinery that decides whether an investigation is written at all.
/// </summary>
/// <remarks>
/// <para>
/// This is the layer where every persistence bug in this repository has lived, and until this
/// file existed nothing tested it. Five commits fixed five instances of the same class -
/// 46d7c25, fb8244f, 11591ba, 6961da4, ecff3d5 - and a sixth survived all of them, so that
/// investigations, steps, findings, evidence and agent_actions held zero rows while the agent
/// reported itself healthy.
/// </para>
/// <para>
/// The cause is structural, not a slip: every entity assigns its own key in its initialiser
/// (<c>Guid.CreateVersion7()</c>), and EF Core decides whether a row exists by asking whether
/// the primary key is set. For an entity reached through a navigation on an
/// <b>already-persisted</b> parent, the answer is always yes, so EF emits an UPDATE that
/// matches nothing. A new incident is exempt because <c>Incidents.Add</c> marks the whole
/// graph Added - which is why the happy path looked fine and the second signal for an
/// existing incident did not.
/// </para>
/// <para>
/// Every test here therefore does the same two things: persist a parent first, then reload it
/// in a <b>fresh</b> context before attaching children. An assertion made against the context
/// that wrote the rows proves nothing - it reads them back out of the change tracker.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class InvestigationPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_investigation_attached_to_an_existing_incident_is_written_in_full()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        // A separate scope, exactly as InvestigationWorker creates one per investigation.
        await using (var db = pg.CreateContext())
        {
            var incidents = new IncidentRepository(db, new FixedClock(Now));
            var audit = new AuditRepository(db, new FixedClock(Now));
            var stateMachine = new IncidentStateMachine(new FixedClock(Now));

            var incident = await incidents.GetWithDetailAsync(incidentId, TestContext.Current.CancellationToken);
            incident.Should().NotBeNull();

            var investigation = BuildInvestigation(incident!.Id);
            incident.Investigations.Add(investigation);
            db.AddInvestigationGraph(investigation);

            var eventsBefore = incident.Events.Count;
            stateMachine.Escalate(incident, EscalationReason.LowConfidence, "test");

            // Order is the whole point. TrackNewIncidentChildren must run before anything
            // saves; enlisting the audit row rather than appending it is what keeps that true.
            incidents.TrackNewIncidentChildren(incident, eventsBefore);

            audit.Enlist(new AuditEvent
            {
                At = Now,
                Type = "investigation.completed",
                IncidentId = incident.Id,
                InvestigationId = investigation.Id,
                Actor = IncidentStateMachine.SystemActor,
                Summary = "test",
            });

            await incidents.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Fresh context: the change tracker cannot answer for the database.
        await using var verify = pg.CreateContext();

        var saved = await verify.Investigations
            .Include(i => i.Steps)
            .Include(i => i.Findings).ThenInclude(f => f.Evidence)
            .Include(i => i.Plan!).ThenInclude(p => p.Actions)
            .SingleAsync(TestContext.Current.CancellationToken);

        saved.Steps.Should().HaveCount(2);
        saved.Findings.Should().ContainSingle();
        saved.Findings[0].Evidence.Should().ContainSingle();
        saved.Plan.Should().NotBeNull();
        saved.Plan!.Actions.Should().ContainSingle();

        (await verify.AuditEvents.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(2, "the seeded open event plus the completion enlisted above");

        var incidentAfter = await verify.Incidents
            .Include(i => i.Events)
            .SingleAsync(TestContext.Current.CancellationToken);

        incidentAfter.State.Should().Be(IncidentState.Escalated);
        incidentAfter.Events.Should().HaveCount(3, "the two seeded transitions plus the escalation");
    }

    /// <summary>
    /// The regression test for the bug itself: an audit append in the middle of the unit of
    /// work.
    /// </summary>
    /// <remarks>
    /// <c>AuditRepository.AppendAsync</c> calls <c>SaveChangesAsync</c>, and the repositories
    /// share one context. Calling it after a state transition but before
    /// <c>TrackNewIncidentChildren</c> flushes a graph in which the new IncidentEvent is still
    /// stated Modified, and EF throws. This asserts that shape still throws, so the ordering
    /// in <c>InvestigationCoordinator</c> cannot quietly regress to it - if someone swaps
    /// Enlist back to AppendAsync, this test is what says why they must not.
    /// </remarks>
    [Fact]
    public async Task Appending_audit_mid_transaction_still_breaks_and_is_why_the_code_enlists()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using var db = pg.CreateContext();
        var incidents = new IncidentRepository(db, new FixedClock(Now));
        var audit = new AuditRepository(db, new FixedClock(Now));
        var stateMachine = new IncidentStateMachine(new FixedClock(Now));

        var incident = await incidents.GetWithDetailAsync(incidentId, TestContext.Current.CancellationToken);
        stateMachine.Escalate(incident!, EscalationReason.LowConfidence, "test");

        var act = async () => await audit.AppendAsync(new AuditEvent
        {
            At = Now,
            Type = "investigation.completed",
            IncidentId = incident!.Id,
            Actor = IncidentStateMachine.SystemActor,
            Summary = "test",
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task A_second_signal_on_an_existing_incident_is_written()
    {
        await pg.ResetAsync();

        var incidentId = await SeedIncidentAsync();

        await using (var db = pg.CreateContext())
        {
            var incidents = new IncidentRepository(db, new FixedClock(Now));
            var incident = await incidents.GetWithDetailAsync(incidentId, TestContext.Current.CancellationToken);

            incident!.Signals.Add(NewSignal(incident.Id, "second"));
            incidents.AddSignal(incident.Signals[^1]);

            await incidents.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verify = pg.CreateContext();

        (await verify.Signals.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Migrations_leave_no_pending_model_changes()
    {
        await using var db = pg.CreateContext();

        var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);

        pending.Should().BeEmpty(
            "a model that has drifted from its migration fails at pod startup, where "
            + "MigrateHephaistoDatabaseAsync deliberately refuses to start degraded");
    }

    // ----------------------------------------------------------------------------------

    private async Task<Guid> SeedIncidentAsync()
    {
        await using var db = pg.CreateContext();
        var incidents = new IncidentRepository(db, new FixedClock(Now));
        var audit = new AuditRepository(db, new FixedClock(Now));
        var stateMachine = new IncidentStateMachine(new FixedClock(Now));

        var incident = new Incident
        {
            CorrelationKey = "shop/Deployment/checkout",
            Title = "CrashLoopBackOff on checkout",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Warning,
            Target = new TargetRef { Namespace = "shop", Kind = "Pod", Name = "checkout-abc", OwnerKind = "Deployment", OwnerName = "checkout" },
            OpenedAt = Now,
            LastSignalAt = Now,
        };

        incident.Signals.Add(NewSignal(incident.Id, "first"));

        await incidents.AddAsync(incident, TestContext.Current.CancellationToken);

        // Detected -> Triaging -> Investigating. The state machine rejects the shortcut, and
        // that is the behaviour under test elsewhere, so the seed has to walk it properly.
        stateMachine.Triage(incident, "seed");
        stateMachine.BeginInvestigation(incident, "seed");

        audit.Enlist(new AuditEvent
        {
            At = Now,
            Type = "incident.opened",
            IncidentId = incident.Id,
            Actor = IncidentStateMachine.SystemActor,
            Summary = "seed",
        });

        await incidents.SaveChangesAsync(TestContext.Current.CancellationToken);

        return incident.Id;
    }

    private static Signal NewSignal(Guid incidentId, string reason) => new()
    {
        IncidentId = incidentId,
        Fingerprint = $"fp-{reason}",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Warning,
        Source = SignalSource.KubernetesWatch,
        Reason = reason,
        Target = new TargetRef { Namespace = "shop", Kind = "Pod", Name = "checkout-abc", OwnerKind = "Deployment", OwnerName = "checkout" },
        FirstSeen = Now,
        LastSeen = Now,
    };

    private static Investigation BuildInvestigation(Guid incidentId)
    {
        var investigation = new Investigation
        {
            IncidentId = incidentId,
            ModelId = "test-model",
            StartedAt = Now,
            CompletedAt = Now.AddSeconds(30),
            TerminationReason = TerminationReason.Concluded,
            StepsUsed = 2,
            ToolCallsUsed = 1,
            InputTokens = 1000,
            OutputTokens = 50,
            CostUsd = 0.001m,
            Confidence = 0.8,
        };

        investigation.Steps.Add(new InvestigationStep
        {
            InvestigationId = investigation.Id, Ordinal = 0, Kind = StepKind.LlmTurn, At = Now,
        });

        var toolStep = new InvestigationStep
        {
            InvestigationId = investigation.Id, Ordinal = 1, Kind = StepKind.ToolCall,
            ToolName = "get_pod", At = Now,
        };

        investigation.Steps.Add(toolStep);

        var finding = new Finding
        {
            InvestigationId = investigation.Id,
            Category = "resources",
            Hypothesis = "container limit too low",
            Confidence = 0.8,
            IsPrimary = true,
        };

        finding.Evidence.Add(new Evidence
        {
            FindingId = finding.Id, StepId = toolStep.Id, Excerpt = "OOMKilled",
        });

        investigation.Findings.Add(finding);

        var plan = new ActionPlan
        {
            InvestigationId = investigation.Id, Summary = "raise the limit", CreatedAt = Now,
        };

        plan.Actions.Add(new AgentAction
        {
            IncidentId = incidentId,
            ActionPlanId = plan.Id,
            Type = ActionType.PatchResources,
            Target = new TargetRef { Namespace = "shop", Kind = "Deployment", Name = "checkout" },
            Risk = RiskTier.Medium,
        });

        investigation.Plan = plan;

        return investigation;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
