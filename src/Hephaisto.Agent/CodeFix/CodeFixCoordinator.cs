using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.CodeFix;

/// <summary>How a human's decision on a plan came out.</summary>
public enum CodeFixDecisionOutcome
{
    Done = 0,
    NotFound = 1,

    /// <summary>The attempt is not waiting on a decision (already decided, still planning, finished).</summary>
    Conflict = 2,

    /// <summary>The actor may not decide: a machine identity, or unauthenticated approval where Pr needs a login.</summary>
    Forbidden = 3,

    /// <summary>The effective code-fix mode is not Pr, or the kill switch is engaged.</summary>
    ModeRefused = 4,
}

public sealed record CodeFixDecisionResult(CodeFixDecisionOutcome Outcome, string Message, CodeFixAttempt? Attempt = null);

/// <summary>
/// Joins the code-fix stage to the database, the kill switch and the Job launcher. Everything that
/// changes a <see cref="CodeFixAttempt"/> goes through here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evaluation always leaves a record; a Job only when eligible.</b> Every escalation is judged and
/// the verdict audited as <c>codefix.evaluated</c>, including "would have started, but the mode is
/// Off" - the same honesty as the would-have panel, and what lets an operator decide to turn the mode
/// on from evidence rather than hope.
/// </para>
/// <para>
/// <b>Nothing here can fail an investigation.</b> The stage runs after the investigation has
/// committed, and every exit path is caught: a broken coder must never cost a diagnosis.
/// </para>
/// </remarks>
public sealed class CodeFixCoordinator(
    HephaistoDbContext db,
    IAuditRepository audit,
    ICodeFixSwitch codeFixSwitch,
    ICodeFixJobLauncher launcher,
    IWorkloadImageReader images,
    CodeFixRequestBuilder requests,
    CodeFixStateMachine machine,
    CodeFixNotifier notifier,
    CodeFixMetrics metrics,
    IGlobalLlmBudget globalBudget,
    IGrafanaAnnotator annotator,
    IOptionsMonitor<CodeFixOptions> options,
    IOptionsMonitor<IngestOptions> ingest,
    IClock clock,
    ILogger<CodeFixCoordinator> logger)
{
    public const string AuditEvaluated = "codefix.evaluated";
    public const string AuditRequested = "codefix.requested";
    public const string AuditLaunched = "codefix.launched";
    public const string AuditPlanReady = "codefix.plan_ready";
    public const string AuditApproved = "codefix.approved";
    public const string AuditDenied = "codefix.denied";
    public const string AuditPrOpened = "codefix.pr_opened";
    public const string AuditFailed = "codefix.failed";
    public const string AuditExpired = "codefix.expired";
    public const string AuditCancelled = "codefix.cancelled";

    /// <summary>
    /// After an escalation has committed. Never throws: the investigation this follows is already
    /// safe, and a code-fix problem must stay a code-fix problem.
    /// </summary>
    public async Task<CodeFixVerdict?> EvaluateAsync(Incident incident, Investigation? investigation, CancellationToken ct)
    {
        try
        {
            return (await EvaluateCoreAsync(incident, investigation, IncidentStateMachine.SystemActor, false, ct)
                .ConfigureAwait(false)).Verdict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Code-fix evaluation of incident {IncidentId} failed; the investigation is unaffected.", incident.Id);
            return null;
        }
    }

    /// <summary>
    /// The manual door: a human asks for a code fix on an escalated incident. Their judgement replaces
    /// the model's on category and confidence; nothing else relaxes.
    /// </summary>
    public async Task<(CodeFixVerdict? Verdict, CodeFixAttempt? Attempt, string? Refusal)> RequestAsync(
        Guid incidentId, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor) || IncidentStateMachine.IsForbiddenGranter(actor)
            || actor.Trim().StartsWith("hephaisto/", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, $"'{actor}' may not request a code fix; that is a human act.");
        }

        var incident = await db.Incidents
            .Include(i => i.CodeFixAttempts)
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            .ConfigureAwait(false);

        if (incident is null)
            return (null, null, "no such incident");

        var investigation = await LatestInvestigationAsync(incident.Id, ct).ConfigureAwait(false);
        var (verdict, attempt) = await EvaluateCoreAsync(incident, investigation, actor.Trim(), true, ct).ConfigureAwait(false);

        return (verdict, attempt, verdict.Eligible ? null : verdict.Describe());
    }

    private async Task<(CodeFixVerdict Verdict, CodeFixAttempt? Attempt)> EvaluateCoreAsync(
        Incident incident, Investigation? investigation, string actor, bool requestedByHuman, CancellationToken ct)
    {
        using var span = HephaistoMetrics.ActivitySource.StartActivity(HephaistoTelemetry.Spans.CodeFixEvaluate);
        span?.SetTag("incident.id", incident.Id);

        var o = options.CurrentValue;
        var mode = await codeFixSwitch.ResolveAsync(ct).ConfigureAwait(false);
        var target = await images.ResolveWorkloadAsync(incident.Target, ct).ConfigureAwait(false);
        var workload = target.WorkloadKey;
        var binding = o.BindingFor(workload);
        var now = clock.UtcNow;
        var dayAgo = now.AddDays(-1);

        var primary = investigation?.Findings.FirstOrDefault(f => f.IsPrimary);
        var ns = incident.Target.Namespace;

        var candidate = new CodeFixCandidate
        {
            State = incident.State,
            EscalationReason = incident.EscalationReason,
            Kind = incident.Kind,
            SelfSignal = incident.EscalationReason == EscalationReason.SelfSignal
                || ingest.CurrentValue.SelfNamespaces.Contains(ns)
                || string.Equals(ns, o.Namespace, StringComparison.Ordinal),
            Termination = investigation?.TerminationReason,
            WorkloadKey = workload,
            PrimaryCategory = primary?.Category,
            PrimaryConfidence = primary?.Confidence,
            GroundedEvidenceCount = primary?.Evidence.Count ?? 0,
            Binding = binding,
            RequestedByHuman = requestedByHuman,
        };

        var open = CodeFixStates.Open.ToArray();
        var running = CodeFixStates.Running.ToArray();

        var facts = new CodeFixFacts
        {
            Mode = mode.Declared,
            AgentMode = mode.AgentMode,
            EmergencyStop = mode.EmergencyStop,
            RunawayLatched = mode.RunawayLatched || mode.AgentArmFailed,
            IncidentAttemptOpen = await db.CodeFixAttempts
                .AnyAsync(a => a.IncidentId == incident.Id && open.Contains(a.State), ct).ConfigureAwait(false),
            WorkloadAttemptOpen = await db.CodeFixAttempts
                .AnyAsync(a => a.Workload == workload && a.IncidentId != incident.Id && open.Contains(a.State), ct)
                .ConfigureAwait(false),
            RepositoryAttemptsToday = binding is null
                ? 0
                : await db.CodeFixAttempts
                    .CountAsync(a => a.RepositoryUrl == binding.Url && a.CreatedAt >= dayAgo, ct).ConfigureAwait(false),
            JobsInFlight = await db.CodeFixAttempts.CountAsync(a => running.Contains(a.State), ct).ConfigureAwait(false),
            CostTodayUsd = await db.LlmUsage
                .Where(u => u.CodeFixAttemptId != null && u.At >= dayAgo)
                .SumAsync(u => u.CostUsd, ct).ConfigureAwait(false),
            LlmBudgetExhausted = !(await globalBudget.CheckAsync(incident.Id, ct).ConfigureAwait(false)).Allowed,
        };

        var verdict = CodeFixEligibility.Evaluate(candidate, facts, o.ToEligibilityOptions());

        metrics.Evaluated(verdict, requestedByHuman);
        span?.SetTag("codefix.eligible", verdict.Eligible);
        span?.SetTag("codefix.reason", verdict.PrimaryCode?.ToString());

        audit.Enlist(Audit(incident.Id, investigation?.Id, null, requestedByHuman ? AuditRequested : AuditEvaluated, actor,
            verdict.Eligible
                ? $"code fix eligible for {workload} -> {binding!.Url}"
                : verdict.WouldHaveStarted
                    ? $"would have started a code fix for {workload}, but the code-fix mode is Off"
                    : $"no code fix: {verdict.Describe()}",
            new
            {
                eligible = verdict.Eligible,
                would_have_started = verdict.WouldHaveStarted,
                codes = verdict.Codes.Select(c => c.ToString()),
                reasons = verdict.Reasons,
                mode = mode.Explain(),
                workload,
                repository = binding?.Url,
                category = primary?.Category,
                confidence = primary?.Confidence,
            }));

        if (!verdict.Eligible)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (verdict, null);
        }

        var attempt = new CodeFixAttempt
        {
            IncidentId = incident.Id,
            InvestigationId = investigation?.Id,
            Workload = workload,
            RepositoryUrl = binding!.Url,
            DefaultBranch = string.IsNullOrWhiteSpace(binding.DefaultBranch) ? "main" : binding.DefaultBranch,
            RequestedBy = actor,
            CreatedAt = now,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        };

        attempt.Branch = CodeFixJobSpec.BranchName(attempt.Id);
        db.CodeFixAttempts.Add(attempt);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two escalations of one incident in the same instant: both passed the in-memory check and
            // the partial unique index let exactly one through. This is the other one.
            db.Entry(attempt).State = EntityState.Detached;
            logger.LogInformation("A code fix for incident {IncidentId} is already open; not starting a second.", incident.Id);

            var refused = verdict with
            {
                Eligible = false,
                Codes = [CodeFixReasonCode.AttemptAlreadyOpen],
                Reasons = ["a code fix is already open for this incident"],
            };

            return (refused, null);
        }

        await StartPhaseAsync(attempt, CodeFixPhase.Plan, incident, investigation, null, ct).ConfigureAwait(false);
        return (verdict, attempt);
    }

    /// <summary>
    /// Builds the request, creates the Job and records the transition. A launch failure fails the
    /// attempt with the reason rather than leaving it waiting for a Job that will never exist.
    /// </summary>
    public async Task StartPhaseAsync(
        CodeFixAttempt attempt,
        CodeFixPhase phase,
        Incident incident,
        Investigation? investigation,
        CodeFixPlanResult? plan,
        CancellationToken ct)
    {
        using var span = HephaistoMetrics.ActivitySource.StartActivity(HephaistoTelemetry.Spans.CodeFixLaunch);
        span?.SetTag("codefix.phase", phase.ToString());

        string json;

        try
        {
            var target = await images.ResolveWorkloadAsync(incident.Target, ct).ConfigureAwait(false);
            var (image, revision) = await images.ReadAsync(target, ct).ConfigureAwait(false);
            var request = requests.Build(attempt, phase, incident, investigation, image, revision, plan);
            json = JsonSerializer.Serialize(request, CodeFixContract.Json);

            if (phase == CodeFixPhase.Plan)
                attempt.RequestJson = json;

            var job = await launcher.LaunchAsync(attempt, phase, json, ct).ConfigureAwait(false);

            if (phase == CodeFixPhase.Plan)
                machine.BeginPlanning(attempt, job);
            else
                machine.BeginImplementing(attempt, job);

            metrics.JobStarted();
            audit.Enlist(Audit(incident.Id, attempt.InvestigationId, attempt.Id, AuditLaunched, IncidentStateMachine.SystemActor,
                $"{phase} job {job} started for {attempt.RepositoryUrl}", new { job, phase = phase.ToString(), image }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not start the {Phase} job for code fix {AttemptId}.", phase, attempt.Id);

            FailInternal(attempt, incident, $"the {phase.ToString().ToLowerInvariant()} job could not be started: {ex.Message}");
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        notifier.Publish(attempt);
    }

    /// <summary>
    /// A human approves or denies a plan. Approve is the one door from a read-only analysis to a Job
    /// that writes to a repository, so it re-checks everything at the moment of opening it.
    /// </summary>
    public async Task<CodeFixDecisionResult> DecideAsync(
        Guid incidentId,
        Guid attemptId,
        bool approve,
        string actor,
        ApprovalSource source,
        bool authenticated,
        string? reason,
        CancellationToken ct)
    {
        using var span = HephaistoMetrics.ActivitySource.StartActivity(HephaistoTelemetry.Spans.CodeFixApprove);

        if (string.IsNullOrWhiteSpace(actor) || IncidentStateMachine.IsForbiddenGranter(actor)
            || actor.Trim().StartsWith("hephaisto/", StringComparison.OrdinalIgnoreCase))
        {
            return new(CodeFixDecisionOutcome.Forbidden, $"'{actor}' may not decide a code fix; that is a human act.");
        }

        var o = options.CurrentValue;

        if (approve && !authenticated && !o.AllowUnauthenticatedApproval)
        {
            return new(CodeFixDecisionOutcome.Forbidden,
                "approving a code fix opens a repository to a coder and needs an authenticated human; "
                + "enable Auth, or set CodeFix:AllowUnauthenticatedApproval on a throwaway cluster");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The row lock is what makes a double click, or two approvers, produce one Job: the second
        // waits here, then reads Implementing and gets a conflict.
        var attempt = await db.CodeFixAttempts
            .FromSql($"SELECT * FROM code_fix_attempts WHERE id = {attemptId} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (attempt is null || attempt.IncidentId != incidentId)
            return new(CodeFixDecisionOutcome.NotFound, "no such code fix on this incident");

        if (attempt.State != CodeFixState.PlanReady)
            return new(CodeFixDecisionOutcome.Conflict, $"the code fix is {attempt.State}; only a plan that is ready can be decided", attempt);

        if (approve)
        {
            var mode = await codeFixSwitch.ResolveAsync(ct).ConfigureAwait(false);

            if (mode.Effective != CodeFixMode.Pr)
                return new(CodeFixDecisionOutcome.ModeRefused, $"approval needs code-fix mode Pr; {mode.Explain()}", attempt);

            if (attempt.NeedsCait)
            {
                return new(CodeFixDecisionOutcome.Conflict,
                    "this fix needs a Cait change first; that is staged delivery by a human, Cait first", attempt);
            }

            machine.Approve(attempt, actor, source);
        }
        else
        {
            machine.Deny(attempt, actor, reason);
        }

        audit.Enlist(Audit(incidentId, attempt.InvestigationId, attempt.Id, approve ? AuditApproved : AuditDenied, actor.Trim(),
            approve ? $"approved code fix {attempt.Id} for {attempt.RepositoryUrl}" : $"denied code fix {attempt.Id}: {attempt.FailureReason}",
            new { source = source.ToString(), reason }));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        metrics.AwaitingApproval(-1);

        if (!approve)
        {
            notifier.Publish(attempt);
            return new(CodeFixDecisionOutcome.Done, "denied", attempt);
        }

        // The Job starts only after the approval is durable: a Job can never exist without the
        // record of the human who let it.
        var incident = await db.Incidents.FirstAsync(i => i.Id == incidentId, ct).ConfigureAwait(false);
        var investigation = await LatestInvestigationAsync(incidentId, ct).ConfigureAwait(false);
        var plan = attempt.PlanResultJson is null
            ? null
            : JsonSerializer.Deserialize<CodeFixPlanResult>(attempt.PlanResultJson, CodeFixContract.Json);

        await StartPhaseAsync(attempt, CodeFixPhase.Implement, incident, investigation, plan, ct).ConfigureAwait(false);

        return new(CodeFixDecisionOutcome.Done, attempt.State == CodeFixState.Implementing ? "approved; implementing" : $"approved, but {attempt.FailureReason}", attempt);
    }

    /// <summary>One watcher pass over one running attempt: deadline, completion, result.</summary>
    public async Task CollectAsync(CodeFixAttempt attempt, CancellationToken ct)
    {
        var phase = attempt.State == CodeFixState.Implementing ? CodeFixPhase.Implement : CodeFixPhase.Plan;
        var job = phase == CodeFixPhase.Plan ? attempt.PlanJobName : attempt.ImplementJobName;
        var started = (phase == CodeFixPhase.Plan ? attempt.PlanStartedAt : attempt.ImplementStartedAt) ?? attempt.CreatedAt;
        var o = options.CurrentValue;
        var incident = await db.Incidents.FirstAsync(i => i.Id == attempt.IncidentId, ct).ConfigureAwait(false);

        if (job is null)
        {
            Finish(attempt, incident, phase, "failed", $"the {phase} phase has no job recorded");
            await SaveAndPublishAsync(attempt, ct).ConfigureAwait(false);
            return;
        }

        var observed = await launcher.ObserveAsync(job, ct).ConfigureAwait(false);
        var deadline = (phase == CodeFixPhase.Plan ? o.PlanDeadline : o.ImplementDeadline) + TimeSpan.FromMinutes(2);

        if (observed.Phase is CodeFixJobPhase.Pending or CodeFixJobPhase.Running)
        {
            if (clock.UtcNow - started <= deadline)
                return;

            await launcher.DeleteAsync(job, ct).ConfigureAwait(false);
            Charge(attempt, phase, PhaseCap(o, phase));
            Finish(attempt, incident, phase, "deadline", $"the {phase} job ran past its deadline and was deleted");
            await SaveAndPublishAsync(attempt, ct).ConfigureAwait(false);
            return;
        }

        using var span = HephaistoMetrics.ActivitySource.StartActivity(HephaistoTelemetry.Spans.CodeFixCollect);
        span?.SetTag("codefix.phase", phase.ToString());
        span?.SetTag("codefix.job_phase", observed.Phase.ToString());

        var log = observed.Phase == CodeFixJobPhase.Missing
            ? null
            : await launcher.ReadResultLogAsync(job, ct).ConfigureAwait(false);

        if (phase == CodeFixPhase.Plan)
            ApplyPlan(attempt, incident, CodeFixResultParser.ParsePlan(log, attempt.Id), observed);
        else
            await ApplyImplementAsync(attempt, incident, CodeFixResultParser.ParseImplement(log, attempt.Id), observed, ct).ConfigureAwait(false);

        await SaveAndPublishAsync(attempt, ct).ConfigureAwait(false);
    }

    private void ApplyPlan(CodeFixAttempt attempt, Incident incident, CodeFixParse<CodeFixPlanResult> parse, CodeFixJobObservation observed)
    {
        var o = options.CurrentValue;

        if (!parse.Ok)
        {
            Charge(attempt, CodeFixPhase.Plan, o.MaxCostUsdPerPlan);
            ContractViolation(attempt, incident, CodeFixPhase.Plan, parse.Violation!, observed);
            return;
        }

        var plan = parse.Result!;
        attempt.PlanResultJson = parse.Json;
        attempt.Summary = plan.Summary;
        attempt.RootCause = plan.RootCause;
        attempt.Confidence = plan.Confidence;
        attempt.VerificationLevel = plan.Verification.Level;
        attempt.NeedsCait = plan.NeedsCait;
        attempt.AnalysedRef = plan.AnalysedRef;
        attempt.ContextSha = plan.ContextSha;
        attempt.PlanSessionId = plan.SessionId;
        Charge(attempt, CodeFixPhase.Plan, plan.CostUsd);

        if (plan.Outcome != "planned")
        {
            Finish(attempt, incident, CodeFixPhase.Plan, plan.Outcome,
                $"the coder returned {plan.Outcome}" + (string.IsNullOrWhiteSpace(plan.Error) ? string.Empty : $": {plan.Error}"));
            return;
        }

        machine.PlanReady(attempt);
        metrics.JobEnded();
        metrics.AwaitingApproval(1);
        metrics.PhaseFinished(CodeFixPhase.Plan, "planned", clock.UtcNow - (attempt.PlanStartedAt ?? attempt.CreatedAt), plan.CostUsd);

        audit.Enlist(Audit(incident.Id, attempt.InvestigationId, attempt.Id, AuditPlanReady, IncidentStateMachine.SystemActor,
            $"plan ready for {attempt.RepositoryUrl}: {Trim(plan.Summary, 300)}",
            new
            {
                files = plan.Files,
                confidence = plan.Confidence,
                verification = plan.Verification.Level,
                needs_cait = plan.NeedsCait,
                denied_tool_calls = plan.DeniedToolCalls.Count,
                analysed_ref = plan.AnalysedRef,
            }));

        notifier.Enlist(NotificationEvent.CodeFixPlanReady, attempt, incident, Trim(plan.Summary, 500));
    }

    private async Task ApplyImplementAsync(
        CodeFixAttempt attempt, Incident incident, CodeFixParse<CodeFixImplementResult> parse, CodeFixJobObservation observed, CancellationToken ct)
    {
        var o = options.CurrentValue;

        if (!parse.Ok)
        {
            Charge(attempt, CodeFixPhase.Implement, o.MaxCostUsdPerImplement);
            ContractViolation(attempt, incident, CodeFixPhase.Implement, parse.Violation!, observed);
            return;
        }

        var result = parse.Result!;
        attempt.ImplementResultJson = parse.Json;
        attempt.ImplementSessionId = result.SessionId;
        Charge(attempt, CodeFixPhase.Implement, result.CostUsd);

        var violation = CodeFixResultParser.CheckImplementPostConditions(
            result, attempt.Branch, attempt.RepositoryUrl, o.AllowedRepositoryHosts, o.RequireGreenBuild,
            requireTests: string.Equals(attempt.VerificationLevel, "tests", StringComparison.Ordinal));

        if (violation is not null)
        {
            ContractViolation(attempt, incident, CodeFixPhase.Implement, violation, observed);
            return;
        }

        if (result.Outcome is not ("pr_opened" or "already_exists"))
        {
            Finish(attempt, incident, CodeFixPhase.Implement, result.Outcome,
                $"no PR: the coder returned {result.Outcome}" + (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $": {result.Error}"));
            return;
        }

        machine.PrOpened(attempt, result.PrUrl!, result.PrNumber);
        metrics.JobEnded();
        metrics.PhaseFinished(CodeFixPhase.Implement, result.Outcome,
            clock.UtcNow - (attempt.ImplementStartedAt ?? attempt.CreatedAt), result.CostUsd);

        audit.Enlist(Audit(incident.Id, attempt.InvestigationId, attempt.Id, AuditPrOpened, IncidentStateMachine.SystemActor,
            $"draft PR opened: {result.PrUrl}",
            new { pr = result.PrUrl, branch = result.Branch, files = result.Files, result.BuildPassed, result.TestsPassed }));

        notifier.Enlist(NotificationEvent.CodeFixPrOpened, attempt, incident, $"Draft PR {result.PrUrl}");
        await annotator.CodeFixPrOpenedAsync(incident, attempt, ct).ConfigureAwait(false);
    }

    /// <summary>Cancels an open attempt, deleting its Job if one runs. The kill switch's path.</summary>
    public async Task CancelAsync(CodeFixAttempt attempt, string reason, CancellationToken ct)
    {
        var wasRunning = attempt.State.IsRunning();
        var wasWaiting = attempt.State == CodeFixState.PlanReady;
        var job = attempt.State == CodeFixState.Implementing ? attempt.ImplementJobName : attempt.PlanJobName;

        if (wasRunning && job is not null)
            await launcher.DeleteAsync(job, ct).ConfigureAwait(false);

        var incident = await db.Incidents.FirstAsync(i => i.Id == attempt.IncidentId, ct).ConfigureAwait(false);

        machine.Cancel(attempt, reason);

        if (wasRunning)
            metrics.JobEnded();

        if (wasWaiting)
            metrics.AwaitingApproval(-1);

        audit.Enlist(Audit(incident.Id, attempt.InvestigationId, attempt.Id, AuditCancelled, IncidentStateMachine.SystemActor,
            $"code fix cancelled: {reason}", new { job }));
        notifier.Enlist(NotificationEvent.CodeFixFailed, attempt, incident, $"cancelled: {reason}");

        await SaveAndPublishAsync(attempt, ct).ConfigureAwait(false);
    }

    public async Task ExpireAsync(CodeFixAttempt attempt, CancellationToken ct)
    {
        var incident = await db.Incidents.FirstAsync(i => i.Id == attempt.IncidentId, ct).ConfigureAwait(false);

        machine.Expire(attempt);
        metrics.AwaitingApproval(-1);

        audit.Enlist(Audit(incident.Id, attempt.InvestigationId, attempt.Id, AuditExpired, IncidentStateMachine.SystemActor,
            "nobody approved or denied the plan in time", null));
        notifier.Enlist(NotificationEvent.CodeFixFailed, attempt, incident, "expired: nobody decided on the plan in time");

        await SaveAndPublishAsync(attempt, ct).ConfigureAwait(false);
    }

    /// <summary>Retries the launch of an attempt that was saved but never got its Job (a crash in between).</summary>
    public async Task RelaunchAsync(CodeFixAttempt attempt, CancellationToken ct)
    {
        var incident = await db.Incidents.FirstAsync(i => i.Id == attempt.IncidentId, ct).ConfigureAwait(false);
        var investigation = await LatestInvestigationAsync(incident.Id, ct).ConfigureAwait(false);

        await StartPhaseAsync(attempt, CodeFixPhase.Plan, incident, investigation, null, ct).ConfigureAwait(false);
    }

    public Task<Investigation?> LatestInvestigationAsync(Guid incidentId, CancellationToken ct) =>
        db.Investigations
            .Include(i => i.Findings).ThenInclude(f => f.Evidence)
            .Include(i => i.Steps)
            .Include(i => i.Plan)
            .AsSplitQuery()
            .Where(i => i.IncidentId == incidentId)
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefaultAsync(ct);

    private void ContractViolation(CodeFixAttempt attempt, Incident incident, CodeFixPhase phase, string violation, CodeFixJobObservation observed)
    {
        logger.LogWarning("Code fix {AttemptId} {Phase}: {Violation} (job {JobPhase} {Detail})",
            attempt.Id, phase, violation, observed.Phase, observed.Detail);

        Finish(attempt, incident, phase, "contract_violation",
            $"contract violation: {violation}" + (observed.Detail is { Length: > 0 } d ? $" (job: {d})" : string.Empty));
    }

    private void Finish(CodeFixAttempt attempt, Incident incident, CodeFixPhase phase, string outcome, string reason)
    {
        metrics.JobEnded();
        metrics.PhaseFinished(phase, outcome,
            clock.UtcNow - ((phase == CodeFixPhase.Plan ? attempt.PlanStartedAt : attempt.ImplementStartedAt) ?? attempt.CreatedAt),
            phase == CodeFixPhase.Plan ? attempt.PlanCostUsd : attempt.ImplementCostUsd);

        FailInternal(attempt, incident, reason);
    }

    private void FailInternal(CodeFixAttempt attempt, Incident incident, string reason)
    {
        machine.Fail(attempt, reason);

        audit.Enlist(Audit(incident.Id, attempt.InvestigationId, attempt.Id, AuditFailed, IncidentStateMachine.SystemActor,
            $"code fix failed: {Trim(reason, 400)}", new { reason }));
        notifier.Enlist(NotificationEvent.CodeFixFailed, attempt, incident, Trim(reason, 500));
    }

    /// <summary>
    /// Coder spend enters the same ledger as every investigation's, so the global budget sees it. A
    /// runner that died without reporting is charged its phase cap: the money may well have been spent.
    /// </summary>
    private void Charge(CodeFixAttempt attempt, CodeFixPhase phase, decimal costUsd)
    {
        if (phase == CodeFixPhase.Plan)
            attempt.PlanCostUsd = costUsd;
        else
            attempt.ImplementCostUsd = costUsd;

        db.LlmUsage.Add(new LlmUsageRecord
        {
            IncidentId = attempt.IncidentId,
            CodeFixAttemptId = attempt.Id,
            At = clock.UtcNow,
            CostUsd = costUsd,
        });
    }

    private static decimal PhaseCap(CodeFixOptions o, CodeFixPhase phase) =>
        phase == CodeFixPhase.Plan ? o.MaxCostUsdPerPlan : o.MaxCostUsdPerImplement;

    private async Task SaveAndPublishAsync(CodeFixAttempt attempt, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        notifier.Publish(attempt);
    }

    private AuditEvent Audit(Guid incidentId, Guid? investigationId, Guid? attemptId, string type, string actor, string summary, object? detail) =>
        new()
        {
            At = clock.UtcNow,
            Type = type,
            IncidentId = incidentId,
            InvestigationId = investigationId,
            Actor = actor,
            Summary = summary,
            Detail = JsonSerializer.Serialize(new { attempt_id = attemptId, detail }),
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        };

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}
