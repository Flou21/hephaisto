using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Safety;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// The code-fix stage against a real Postgres: what gets a row, what gets a Job, what gets
/// audited, and what the approval door refuses. Every refusal has its positive control beside it,
/// so a green run cannot mean "nothing was tested".
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CodeFixStageTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private const string Workload = "shop/Deployment/checkout";
    private const string Repo = "https://github.com/o/checkout";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Off never creates a row -------------------------------------------------------------

    [Theory]
    [InlineData("off", "Observe", false, CodeFixReasonCode.ModeOff)]
    [InlineData("plan", "Off", false, CodeFixReasonCode.AgentOff)]
    [InlineData("plan", "Auto", true, CodeFixReasonCode.EmergencyStop)]
    public async Task A_switch_that_is_down_leaves_an_audit_row_and_no_attempt_and_no_job(
        string codeFixMode, string agentMode, bool stop, CodeFixReasonCode expected)
    {
        await pg.ResetAsync();
        var (incidentId, investigationId) = await SeedEscalatedIncidentAsync();
        var launcher = new RecordingLauncher();

        await using (var db = pg.CreateContext())
        {
            var (incident, investigation) = await LoadAsync(db, incidentId, investigationId);

            var verdict = await Coordinator(db, launcher, Switch(codeFixMode, agentMode, stop)).EvaluateAsync(incident, investigation, Ct);

            verdict!.Eligible.Should().BeFalse();
            verdict.Codes.Should().Contain(expected);
        }

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.CountAsync(Ct)).Should().Be(0, "a refused evaluation must not leave an attempt row");
            launcher.Launched.Should().BeEmpty();

            var audit = await db.AuditEvents.AsNoTracking().Where(e => e.Type == "codefix.evaluated").ToListAsync(Ct);
            audit.Should().ContainSingle();
            audit[0].Detail.Should().Contain(expected.ToString());
        }
    }

    [Fact]
    public async Task Control_Plan_mode_with_the_agent_in_Observe_creates_one_attempt_and_one_plan_job()
    {
        await pg.ResetAsync();
        var (incidentId, investigationId) = await SeedEscalatedIncidentAsync();
        var launcher = new RecordingLauncher();

        await using (var db = pg.CreateContext())
        {
            var (incident, investigation) = await LoadAsync(db, incidentId, investigationId);

            var verdict = await Coordinator(db, launcher, Switch("plan", "Observe")).EvaluateAsync(incident, investigation, Ct);

            verdict!.Eligible.Should().BeTrue(verdict.Describe());
        }

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct);

            attempt.State.Should().Be(CodeFixState.Planning);
            attempt.RepositoryUrl.Should().Be(Repo);
            attempt.Branch.Should().MatchRegex("^hephaisto/codefix-[0-9a-f]{12}$");
            attempt.PlanJobName.Should().Be(CodeFixJobSpec.JobName(attempt.Id, CodeFixPhase.Plan));

            launcher.Launched.Should().ContainSingle().Which.Phase.Should().Be(CodeFixPhase.Plan);

            var request = JsonSerializer.Deserialize<CodeFixRequest>(attempt.RequestJson!, CodeFixContract.Json)!;
            request.Phase.Should().Be("plan");
            request.Findings.Should().ContainSingle().Which.Evidence.Should().ContainSingle();
        }
    }

    // --- collect, cost, outbox ---------------------------------------------------------------

    [Fact]
    public async Task A_plan_result_makes_the_attempt_PlanReady_charges_the_ledger_and_enlists_a_notification()
    {
        await pg.ResetAsync();
        var (incidentId, investigationId) = await SeedEscalatedIncidentAsync();
        var launcher = new RecordingLauncher();
        var attemptId = await StartAsync(incidentId, investigationId, launcher);

        launcher.Log = "progress\n" + CodeFixResultParser.Frame(PlanJson(attemptId, cost: 1.25m));

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct);
            await Coordinator(db, launcher, Switch("plan", "Observe")).CollectAsync(attempt, Ct);
        }

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct);
            attempt.State.Should().Be(CodeFixState.PlanReady);
            attempt.Summary.Should().Contain("null check");

            var usage = await db.LlmUsage.AsNoTracking().Where(u => u.CodeFixAttemptId == attemptId).ToListAsync(Ct);
            usage.Should().ContainSingle().Which.CostUsd.Should().Be(1.25m);

            var deliveries = await db.NotificationDeliveries.AsNoTracking().ToListAsync(Ct);
            deliveries.Should().ContainSingle().Which.Event.Should().Be(NotificationEvent.CodeFixPlanReady);

            (await db.AuditEvents.AsNoTracking().AnyAsync(e => e.Type == "codefix.plan_ready", Ct)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_forged_result_naming_another_attempt_fails_the_attempt_and_is_charged_the_cap()
    {
        await pg.ResetAsync();
        var (incidentId, investigationId) = await SeedEscalatedIncidentAsync();
        var launcher = new RecordingLauncher();
        var attemptId = await StartAsync(incidentId, investigationId, launcher);

        launcher.Log = CodeFixResultParser.Frame(PlanJson(Guid.CreateVersion7(), cost: 0.01m));

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct);
            await Coordinator(db, launcher, Switch("plan", "Observe")).CollectAsync(attempt, Ct);
        }

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct);
            attempt.State.Should().Be(CodeFixState.Failed);
            attempt.FailureReason.Should().Contain("contract violation").And.Contain("names attempt");

            (await db.LlmUsage.AsNoTracking().SumAsync(u => u.CostUsd, Ct)).Should().Be(5m, "an unreadable runner is charged its phase cap");
            (await db.AuditEvents.AsNoTracking().AnyAsync(e => e.Type == "codefix.failed", Ct)).Should().BeTrue();
        }
    }

    // --- the approval door -------------------------------------------------------------------

    [Fact]
    public async Task Approval_is_refused_in_Plan_mode_and_the_plan_stays_waiting()
    {
        var (incidentId, attemptId, launcher) = await PlanReadyAsync();

        await using var db = pg.CreateContext();
        var result = await Coordinator(db, launcher, Switch("plan", "Observe"))
            .DecideAsync(incidentId, attemptId, true, "flo", ApprovalSource.Api, authenticated: false, null, Ct);

        result.Outcome.Should().Be(CodeFixDecisionOutcome.ModeRefused);
        (await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct)).State.Should().Be(CodeFixState.PlanReady);
        launcher.Launched.Should().ContainSingle("only the plan job ran");
    }

    [Fact]
    public async Task Unauthenticated_approval_is_refused_unless_the_escape_hatch_is_set()
    {
        var (incidentId, attemptId, launcher) = await PlanReadyAsync();

        await using var db = pg.CreateContext();
        var result = await Coordinator(db, launcher, Switch("pr", "Observe"), allowUnauthenticated: false)
            .DecideAsync(incidentId, attemptId, true, "flo", ApprovalSource.Api, authenticated: false, null, Ct);

        result.Outcome.Should().Be(CodeFixDecisionOutcome.Forbidden);
    }

    [Theory]
    [InlineData("hephaisto/model")]
    [InlineData("hephaisto/system")]
    [InlineData("hephaisto/auto")]
    public async Task A_machine_identity_can_never_approve(string actor)
    {
        var (incidentId, attemptId, launcher) = await PlanReadyAsync();

        await using var db = pg.CreateContext();
        var result = await Coordinator(db, launcher, Switch("pr", "Observe"))
            .DecideAsync(incidentId, attemptId, true, actor, ApprovalSource.Api, authenticated: true, null, Ct);

        result.Outcome.Should().Be(CodeFixDecisionOutcome.Forbidden);
    }

    [Fact]
    public async Task Control_approval_in_Pr_mode_commits_the_audit_row_then_starts_exactly_one_implement_job()
    {
        var (incidentId, attemptId, launcher) = await PlanReadyAsync();

        await using (var db = pg.CreateContext())
        {
            var result = await Coordinator(db, launcher, Switch("pr", "Observe"))
                .DecideAsync(incidentId, attemptId, true, "flo", ApprovalSource.Oidc, authenticated: true, null, Ct);

            result.Outcome.Should().Be(CodeFixDecisionOutcome.Done, result.Message);
        }

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct);
            attempt.State.Should().Be(CodeFixState.Implementing);
            attempt.ApprovedBy.Should().Be("flo");
            attempt.ImplementJobName.Should().Be(CodeFixJobSpec.JobName(attemptId, CodeFixPhase.Implement));

            launcher.Launched.Select(l => l.Phase).Should().Equal(CodeFixPhase.Plan, CodeFixPhase.Implement);
            (await db.AuditEvents.AsNoTracking().Where(e => e.Type == "codefix.approved").Select(e => e.Actor).SingleAsync(Ct)).Should().Be("flo");

            var request = JsonSerializer.Deserialize<CodeFixRequest>(launcher.Launched[1].Json, CodeFixContract.Json)!;
            request.Phase.Should().Be("implement");
            request.Plan.Should().NotBeNull("the implement request carries the approved plan");
        }

        // A second click finds Implementing and gets a conflict, not a second Job.
        await using (var db = pg.CreateContext())
        {
            var again = await Coordinator(db, launcher, Switch("pr", "Observe"))
                .DecideAsync(incidentId, attemptId, true, "flo", ApprovalSource.Oidc, authenticated: true, null, Ct);

            again.Outcome.Should().Be(CodeFixDecisionOutcome.Conflict);
            launcher.Launched.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task A_second_open_attempt_for_one_incident_is_refused_by_postgres()
    {
        await pg.ResetAsync();
        var (incidentId, _) = await SeedEscalatedIncidentAsync();

        await using var db = pg.CreateContext();

        CodeFixAttempt Open() => new()
        {
            IncidentId = incidentId,
            Workload = Workload,
            RepositoryUrl = Repo,
            Branch = "hephaisto/codefix-000000000000",
            State = CodeFixState.Planning,
            CreatedAt = Now,
        };

        db.CodeFixAttempts.Add(Open());
        await db.SaveChangesAsync(Ct);

        db.CodeFixAttempts.Add(Open());
        var act = () => db.SaveChangesAsync(Ct);

        await act.Should().ThrowAsync<DbUpdateException>();

        // ...while a finished one beside an open one is fine: the index is partial.
        await using var db2 = pg.CreateContext();
        var done = Open();
        done.State = CodeFixState.Failed;
        db2.CodeFixAttempts.Add(done);
        await db2.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_kill_switch_cancels_a_running_attempt_and_deletes_its_job()
    {
        await pg.ResetAsync();
        var (incidentId, investigationId) = await SeedEscalatedIncidentAsync();
        var launcher = new RecordingLauncher { Phase = CodeFixJobPhase.Running };
        var attemptId = await StartAsync(incidentId, investigationId, launcher);

        await using (var db = pg.CreateContext())
        {
            var attempt = await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct);
            await Coordinator(db, launcher, Switch("off", "Observe")).CancelAsync(attempt, "code-fix mode is Off", Ct);
        }

        await using (var db = pg.CreateContext())
        {
            (await db.CodeFixAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct)).State.Should().Be(CodeFixState.Cancelled);
            launcher.Deleted.Should().ContainSingle();
        }
    }

    // ------------------------------------------------------------------------------------------

    private async Task<(Guid IncidentId, Guid AttemptId, RecordingLauncher Launcher)> PlanReadyAsync()
    {
        await pg.ResetAsync();
        var (incidentId, investigationId) = await SeedEscalatedIncidentAsync();
        var launcher = new RecordingLauncher();
        var attemptId = await StartAsync(incidentId, investigationId, launcher);

        launcher.Log = CodeFixResultParser.Frame(PlanJson(attemptId, cost: 1m));

        await using var db = pg.CreateContext();
        var attempt = await db.CodeFixAttempts.SingleAsync(a => a.Id == attemptId, Ct);
        await Coordinator(db, launcher, Switch("plan", "Observe")).CollectAsync(attempt, Ct);
        attempt.State.Should().Be(CodeFixState.PlanReady);

        return (incidentId, attemptId, launcher);
    }

    private async Task<Guid> StartAsync(Guid incidentId, Guid investigationId, RecordingLauncher launcher)
    {
        await using var db = pg.CreateContext();
        var (incident, investigation) = await LoadAsync(db, incidentId, investigationId);
        await Coordinator(db, launcher, Switch("plan", "Observe")).EvaluateAsync(incident, investigation, Ct);

        return (await db.CodeFixAttempts.AsNoTracking().SingleAsync(Ct)).Id;
    }

    private static string PlanJson(Guid attemptId, decimal cost) => JsonSerializer.Serialize(new CodeFixPlanResult
    {
        AttemptId = attemptId,
        Outcome = "planned",
        Summary = "Endpoints.Map needs a null check.",
        RootCause = "src/Startup/Endpoints.cs:14 dereferences a null list.",
        Confidence = 0.9,
        Files = ["src/Startup/Endpoints.cs"],
        Steps = ["Treat a null list as empty."],
        Verification = new CodeFixVerification { Level = "tests", NotVerifiable = [] },
        NeedsCait = false,
        Notes = [],
        AnalysedRef = null,
        ContextSha = null,
        CostUsd = cost,
        SessionId = null,
        Error = null,
        DeniedToolCalls = [],
    }, CodeFixContract.Json);

    private static async Task<(Incident, Investigation)> LoadAsync(HephaistoDbContext db, Guid incidentId, Guid investigationId)
    {
        var incident = await db.Incidents.SingleAsync(i => i.Id == incidentId, Ct);
        var investigation = await db.Investigations
            .Include(i => i.Findings).ThenInclude(f => f.Evidence)
            .Include(i => i.Steps)
            .SingleAsync(i => i.Id == investigationId, Ct);

        return (incident, investigation);
    }

    private async Task<(Guid, Guid)> SeedEscalatedIncidentAsync()
    {
        await using var db = pg.CreateContext();

        var incident = new Incident
        {
            CorrelationKey = Workload,
            Title = "CrashLoopBackOff on checkout",
            Kind = SignalKind.CrashLoopBackOff,
            Severity = Severity.Critical,
            State = IncidentState.Escalated,
            EscalationReason = EscalationReason.NoPlanProduced,
            Target = new TargetRef { Namespace = "shop", Kind = "Pod", Name = "checkout-abc", OwnerKind = "Deployment", OwnerName = "checkout" },
            OpenedAt = Now,
            LastSignalAt = Now,
        };

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(Ct);

        var step = new InvestigationStep { Ordinal = 1, Kind = StepKind.ToolCall, ToolName = "get_pod_logs", ResultDigest = "NullReferenceException", At = Now };
        var investigation = new Investigation
        {
            IncidentId = incident.Id,
            StartedAt = Now,
            CompletedAt = Now,
            TerminationReason = TerminationReason.Concluded,
            Steps = [step],
            Findings =
            [
                new Finding
                {
                    Category = "application",
                    Hypothesis = "startup dereferences a null endpoint list",
                    Confidence = 0.9,
                    IsPrimary = true,
                    Evidence = [new Evidence { StepId = step.Id, Excerpt = "NullReferenceException" }],
                },
            ],
        };

        db.Investigations.Add(investigation);
        await db.SaveChangesAsync(Ct);

        return (incident.Id, investigation.Id);
    }

    private static CodeFixCoordinator Coordinator(HephaistoDbContext db, ICodeFixJobLauncher launcher, ICodeFixSwitch codeFixSwitch, bool allowUnauthenticated = true)
    {
        var clock = new FixedClock(Now);
        var options = new OptionsStub<CodeFixOptions>(new CodeFixOptions
        {
            Image = "hephaisto/coder:test",
            Repositories = [new RepositoryBinding { Workload = Workload, Url = Repo, DefaultBranch = "main" }],
            AllowedRepositoryHosts = ["github.com"],
            EligibleCategories = ["application"],
            AllowUnauthenticatedApproval = allowUnauthenticated,
        });
        var notifications = new OptionsStub<NotificationOptions>(new NotificationOptions
        {
            BaseUrl = "http://hephaisto.test",
            Webhook = new HttpChannelOptions { Url = "http://receiver.test/hook" },
            Routes = [new NotificationRoute { Channel = "webhook", Events = [NotificationEvent.CodeFixPlanReady, NotificationEvent.CodeFixPrOpened, NotificationEvent.CodeFixFailed] }],
        });
        var meters = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

        return new CodeFixCoordinator(
            db,
            new AuditRepository(db, clock),
            codeFixSwitch,
            launcher,
            new NullWorkloadImageReader(),
            new CodeFixRequestBuilder(options),
            new CodeFixStateMachine(clock),
            new CodeFixNotifier(db, notifications, new NullNotifier(), clock, NullLogger<CodeFixNotifier>.Instance),
            new CodeFixMetrics(meters),
            new NullGlobalLlmBudget(),
            new NullGrafanaAnnotator(),
            options,
            new OptionsStub<IngestOptions>(new IngestOptions()),
            new OptionsStub<AuthOptions>(new AuthOptions()),
            clock,
            NullLogger<CodeFixCoordinator>.Instance);
    }

    private static ICodeFixSwitch Switch(string codeFixMode, string agentMode, bool emergencyStop = false)
    {
        var agentArms = new List<ModeArm> { ModeResolver.Parse("env:HEPHAISTO_MODE", agentMode) };

        if (emergencyStop)
            agentArms.Add(ModeResolver.ParseEmergencyStop("configmap:killSwitch", "yes"));

        var resolution = CodeFixModeResolver.Resolve(
            [CodeFixModeResolver.Parse("env:CodeFix__Mode", codeFixMode)],
            ModeResolver.Resolve(agentArms),
            "configmap:killSwitch",
            "db:agent_mode");

        return new StubSwitch(resolution);
    }

    private sealed class StubSwitch(CodeFixModeResolution resolution) : ICodeFixSwitch
    {
        public Task<CodeFixModeResolution> ResolveAsync(CancellationToken ct) => Task.FromResult(resolution);
    }

    private sealed class RecordingLauncher : ICodeFixJobLauncher
    {
        public List<(Guid AttemptId, CodeFixPhase Phase, string Json)> Launched { get; } = [];

        public List<string> Deleted { get; } = [];

        public string? Log { get; set; }

        public CodeFixJobPhase Phase { get; set; } = CodeFixJobPhase.Succeeded;

        public bool IsAvailable => true;

        public Task<string> LaunchAsync(CodeFixAttempt attempt, CodeFixPhase phase, string requestJson, CancellationToken ct)
        {
            Launched.Add((attempt.Id, phase, requestJson));
            return Task.FromResult(CodeFixJobSpec.JobName(attempt.Id, phase));
        }

        public Task<CodeFixJobObservation> ObserveAsync(string jobName, CancellationToken ct) =>
            Task.FromResult(new CodeFixJobObservation(Phase, null));

        public Task<string?> ReadResultLogAsync(string jobName, CancellationToken ct) => Task.FromResult(Log);

        public Task DeleteAsync(string jobName, CancellationToken ct)
        {
            Deleted.Add(jobName);
            return Task.CompletedTask;
        }
    }

    private sealed class NullNotifier : IIncidentNotifier
    {
        public void Publish(IncidentLiveEvent liveEvent)
        {
        }

        public async IAsyncEnumerable<IncidentLiveEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
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
}
