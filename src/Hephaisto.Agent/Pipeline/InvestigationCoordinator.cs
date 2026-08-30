using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations;
using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Safety;
using Hephaisto.Agent.Web;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Joins the investigation loop to the database, the policy engine and the UI. The runner
/// itself is deliberately persistence-free - it returns an outcome and this decides what
/// becomes of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every proposed action is run through the real policy engine even in observe mode,
/// where the answer is a foregone conclusion.</b> That is not wasted work: the "would have
/// done this" panel is only worth reading if the reasons on it are the reasons that would
/// actually apply. Rendering a hardcoded "observe mode" string instead would produce a UI
/// that agrees with the policy engine right up until the day it does not, which is the day
/// someone turns autonomy on.
/// </para>
/// <para>
/// This layer holds no Kubernetes write handle, and neither does anything it calls. Phase 3
/// of the loop - execution - is not wired at MVP; the executor's absence here is what makes
/// "the agent cannot change anything" a structural fact rather than a configuration setting.
/// </para>
/// </remarks>
public sealed class InvestigationCoordinator(
    HephaistoDbContext db,
    IIncidentRepository incidents,
    IAuditRepository audit,
    IKillSwitch killSwitch,
    IncidentStateMachine stateMachine,
    InvestigationRunner runner,
    InvestigationTracker tracker,
    IGlobalLlmBudget globalBudget,
    IncidentEmbedder embedder,
    IIncidentNotifier notifier,
    IOptionsMonitor<PolicyOptions> policyOptions,
    ClusterFactsGatherer facts,
    IActionExecutor executor,
    IClock clock,
    HephaistoMetrics metrics,
    Observability.IGrafanaAnnotator annotator,
    ILogger<InvestigationCoordinator> logger) : IIncidentInvestigator
{
    public async Task InvestigateAsync(Guid incidentId, CancellationToken ct)
    {
        var incident = await incidents.GetWithDetailAsync(incidentId, ct).ConfigureAwait(false);

        if (incident is null || !incident.IsOpen)
        {
            logger.LogDebug("Incident {IncidentId} is gone or already closed; nothing to investigate.", incidentId);
            return;
        }

        // The effective mode, not just the database row: an operator who sets
        // HEPHAISTO_MODE=observe or edits the switch ConfigMap has to be able to hold this
        // incident below whatever the row says, or those two arms are decorative.
        var mode = (await killSwitch.ResolveAsync(ct).ConfigureAwait(false)).Effective;
        incident.Mode = mode;

        // The mode is already in hand from the line above, so this branch is free - and it is
        // the last gate before runner.RunAsync opens an LLM conversation, which is to say the
        // last gate before money. InvestigationWorker declines queued work as well, but this
        // method is reachable directly, so it holds its own line rather than trusting callers.
        //
        // Before the notifier: publishing InvestigationStarted for an investigation that is not
        // going to start would put a permanent spinner in the console.
        if (mode == AgentMode.Off)
        {
            logger.LogInformation(
                "Agent is Off; declining to investigate incident {IncidentId}. It stays open.",
                incidentId);

            return;
        }

        notifier.Publish(new IncidentLiveEvent
        {
            IncidentId = incident.Id,
            Kind = IncidentLiveEventKind.InvestigationStarted,
        });

        var started = clock.UtcNow;
        InvestigationOutcome outcome;

        // Registered for the duration of the run, so "Investigating" stops meaning both
        // "a worker has this" and "this is queued behind two others". Disposed on every exit
        // path including the throw below - a leaked entry would report an investigation as
        // running forever, which is worse than reporting nothing.
        using var inFlight = tracker.Begin(incident.Id, runner.ModelId);

        try
        {
            outcome = await runner.RunAsync(incident, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed investigation is an escalation, not a lost incident. The problem in the
            // cluster is real whether or not the model managed to say anything about it.
            logger.LogError(ex, "Investigation of incident {IncidentId} threw.", incident.Id);

            var failedEventsBefore = incident.Events.Count;

            stateMachine.Escalate(incident, EscalationReason.InvestigationFailed, ex.Message);

            // The transition event Escalate just appended is a new child of an incident that
            // already exists, so change detection states it wrongly and the save throws. On
            // THIS path that is especially bad: the escalation exists precisely to record
            // that an investigation failed, so losing it means a failed investigation leaves
            // the incident stuck in Investigating with nothing explaining why.
            //
            // This runs BEFORE the audit event is enlisted, and the audit event is enlisted
            // rather than appended, so that exactly one SaveChangesAsync sees a correctly
            // stated graph. See EnlistAudit.
            incidents.TrackNewIncidentChildren(incident, failedEventsBefore);

            await RecordOutcomeAsync(incident, summary: null, ct).ConfigureAwait(false);
            EnlistAudit(incident, null, "investigation.failed", ex.Message);

            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            notifier.Publish(new IncidentLiveEvent
            {
                IncidentId = incident.Id,
                Kind = IncidentLiveEventKind.StateChanged,
                State = incident.State,
                Detail = "Investigation failed",
            });
            return;
        }

        var investigation = outcome.Investigation;
        investigation.IncidentId = incident.Id;
        incident.Investigations.Add(investigation);

        // Immediately, not later. The navigation add above is not enough - the key is
        // already assigned, so change detection reads it as an existing row - and the
        // evidence blobs added next carry a foreign key to it. Anything that runs
        // DetectChanges in between would otherwise fix the investigation as Unchanged and
        // the blob insert fails against a parent that was never written.
        db.AddInvestigationGraph(investigation);

        metrics.InvestigationCompleted(
            clock.UtcNow - started,
            investigation.StepsUsed,
            investigation.TerminationReason);

        // rejection.Reason, not rejection. The record's compiler-generated ToString() emits all
        // four members - including a Detail string and two Guids - so this was writing a label
        // value unique per rejection. backlog #12; the correct form was already three files
        // away in InvestigationRunner.
        foreach (var rejection in outcome.Rejections)
            metrics.GroundingRejected(rejection.Reason.ToString());

        var disposition = await DecideOutcomeAsync(incident, outcome, mode, ct).ConfigureAwait(false);

        // Snapshot before the transition so the event it appends can be Added explicitly.
        var eventsBefore = incident.Events.Count;

        switch (disposition.Kind)
        {
            case DispositionKind.Act:
                stateMachine.BeginActing(incident, disposition.Detail ?? "policy allowed the plan");
                break;

            case DispositionKind.AwaitApproval:
                stateMachine.AwaitApproval(incident, disposition.Detail ?? "a human must confirm the plan");
                break;

            default:
                stateMachine.Escalate(incident, disposition.Reason, disposition.Detail);
                break;
        }

        // The investigation and the transition event are both new children of an incident
        // that already exists, which is the case EF Core states wrongly - it sees their
        // assigned keys and emits UPDATEs. The blobs above carry a foreign key to the
        // investigation, so without this the blob insert fails against a parent row that
        // was never written.
        //
        // ORDER IS LOAD-BEARING. This used to run AFTER the audit append, and the audit
        // append saved - so DetectChanges ran on a graph where the event Escalate had just
        // appended was still stated as Modified, EF emitted an UPDATE matching zero rows,
        // and the whole investigation was discarded with the scope. Nothing between
        // AddInvestigationGraph and the save below may call SaveChangesAsync.
        incidents.TrackNewIncidentChildren(incident, eventsBefore);

        // Only for an incident that has actually reached its outcome. Escalated is one;
        // Acting and AwaitingApproval are not, and recording them here would book a closure
        // and an MTTR for an incident that is still very much open - then book a second one
        // when it really does end. Until v0.2.0 both paths out of here were escalations, so
        // this call was unconditional and correct.
        if (disposition.Kind == DispositionKind.Escalate)
        {
            await RecordOutcomeAsync(incident, PrimaryHypothesis(investigation), ct).ConfigureAwait(false);
        }

        EnlistAudit(incident, investigation.Id, "investigation.completed",
            $"{investigation.TerminationReason}; {disposition.Describe()}");

        // What this investigation actually spent, staged into the same commit as the steps
        // that spent it. Nothing called this before, so llm_usage was empty, every rolling
        // window summed an empty table, and the cap allowed everything while reporting 0%
        // utilisation - a spend limit that reads healthy precisely because it is not
        // running. The steps and the counter commit together or neither does.
        globalBudget.Enlist(
            incident.Id,
            investigation.Id,
            investigation.InputTokens,
            investigation.OutputTokens,
            investigation.CostUsd);

        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

        // Blobs go in their own save, AFTER the investigation row exists.
        //
        // They carry a foreign key to the investigation, and batching them together relies
        // on EF ordering the two inserts by that dependency. It does not do so reliably here
        // - the relationship is configured without a navigation property
        // (HasOne<Investigation>().WithMany()), and the blob insert kept being emitted first,
        // failing the whole save with
        //   23503: insert or update on table evidence_blobs violates foreign key constraint
        // and taking the investigation down with it.
        //
        // Two saves means a crash in between could leave an investigation whose raw evidence
        // is missing. That is the right way round to fail: the digested findings and their
        // citations live on the investigation, and the blobs are the expandable raw backing
        // that a human opens occasionally. Losing the diagnosis to preserve atomicity with
        // the attachments would be the worse trade.
        if (outcome.Blobs.Count > 0)
        {
            db.EvidenceBlobs.AddRange(outcome.Blobs);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Indexing happens after the incident is safely written. Embedding is a network call
        // to a third party and must never be able to lose an investigation that already
        // succeeded - a null embedding degrades search to lexical-only, which is survivable.
        await IndexAsync(incident, investigation, ct).ConfigureAwait(false);

        notifier.Publish(new IncidentLiveEvent
        {
            IncidentId = incident.Id,
            Kind = outcome.Plan is null ? IncidentLiveEventKind.InvestigationCompleted : IncidentLiveEventKind.PlanReady,
            State = incident.State,
            Detail = disposition.Detail,
        });

        // EXECUTION HAPPENS HERE, after everything above has committed, and never earlier.
        //
        // The executor saves - three times on the happy path - and the block above is a
        // carefully ordered single save over a graph EF states wrongly. A save in the middle
        // of it discards the investigation, which is the failure the ordering comments
        // upstream exist to prevent. Acting after the commit also means an action can only
        // ever follow a durable record of the decision to take it.
        if (disposition.Kind == DispositionKind.Act)
        {
            await ActAsync(incident, disposition.Actions, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The closed counter and the MTTR histogram, for an incident that has just reached its
    /// outcome state.
    /// </summary>
    /// <remarks>
    /// Both escalation paths in this class are unconditional, so in Observe mode this is where
    /// MTTR actually comes from. Call it after the transition - it reads
    /// <see cref="Incident.State"/> for the <c>outcome</c> label.
    /// </remarks>
    private async Task RecordOutcomeAsync(Incident incident, string? summary, CancellationToken ct)
    {
        metrics.IncidentClosed(
            incident.Kind,
            incident.Severity,
            incident.State,
            clock.UtcNow - incident.OpenedAt);

        // The hypothesis rides along, so the annotation on a latency graph says what the agent
        // concluded rather than merely that it concluded something.
        await annotator.IncidentClosedAsync(incident, summary, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The primary finding's hypothesis, for the Grafana annotation. Null when there is none -
    /// which is the dominant outcome and must read as "no finding", not as an empty string.
    /// </summary>
    private static string? PrimaryHypothesis(Investigation investigation) =>
        investigation.Findings.FirstOrDefault(f => f.IsPrimary)?.Hypothesis;

    /// <summary>
    /// Runs every proposed action past the policy engine, records the verdict on the action,
    /// and decides why the incident is going to a human.
    /// </summary>
    private async Task<Disposition> DecideOutcomeAsync(
        Incident incident,
        InvestigationOutcome outcome,
        AgentMode mode,
        CancellationToken ct)
    {
        if (outcome.Escalation is { } forced)
            return Disposition.Escalate(forced, outcome.Investigation.TerminationReason.ToString());

        if (outcome.Plan is null || outcome.Plan.NoActionRequired || outcome.Plan.Actions.Count == 0)
        {
            // Not a failure. Most incidents want a diagnosis, and a model that declines to
            // act when it cannot justify acting is behaving correctly.
            return Disposition.Escalate(EscalationReason.NoPlanProduced, "diagnosis only; no action proposed");
        }

        var options = policyOptions.CurrentValue;

        // Read the world once, immediately before judging, and refuse to judge without it.
        //
        // This used to be an inline record carrying the clock, the mode and the quarantine
        // stamp - nothing else. An unread fact is not a neutral fact: a null Workload skips
        // the stability, blast-radius and last-replica gates rather than failing them, and an
        // empty label set satisfies every label check. So the engine ran in full and could
        // not fail most of itself, and would have started saying Allow the moment autonomy
        // was switched on.
        ClusterFacts world;

        try
        {
            world = await facts.GatherAsync(incident, mode, ct).ConfigureAwait(false);
        }
        catch (ClusterFactsUnavailableException ex)
        {
            // Default-deny includes the case where the question cannot be asked. An action
            // nobody could judge is not an action anybody approved.
            logger.LogWarning(ex, "Could not gather cluster facts for incident {IncidentId}.", incident.Id);

            foreach (var action in outcome.Plan.Actions)
            {
                action.IncidentId = incident.Id;
                action.Decision = PolicyDecision.Deny;
                action.DecisionReasons = ["cluster facts could not be read, so no action can be judged"];
                action.State = ActionState.Denied;
            }

            return Disposition.Escalate(
                EscalationReason.PolicyDenied, "cluster facts unavailable; nothing could be judged");
        }

        // The policy engine runs on every investigation, and its declared span has never been
        // started - the one genuinely missing span of the four, since the other three were
        // waiting on an executor that did not exist. backlog #16.
        using var policySpan = HephaistoMetrics.ActivitySource.StartActivity(
            HephaistoTelemetry.Spans.PolicyEvaluate);

        policySpan?.SetTag("plan.action_count", outcome.Plan.Actions.Count);
        policySpan?.SetTag("k8s.namespace.name", incident.Target.Namespace);

        foreach (var action in outcome.Plan.Actions)
        {
            action.IncidentId = incident.Id;

            var request = new ActionRequest
            {
                ActionId = action.Id,
                IncidentId = incident.Id,
                Type = action.Type,
                Target = action.Target,
                Risk = action.Risk,
                HasRollbackSpec = !string.IsNullOrWhiteSpace(action.RollbackSpec),
                GroundedFindingIds = action.EvidenceFindingIds,
            };

            var verdict = PolicyEngine.Evaluate(request, world, options);

            action.Decision = verdict.Decision;
            action.DecisionReasons = [.. verdict.Reasons];
            action.State = verdict.Decision switch
            {
                PolicyDecision.Allow => ActionState.Approved,
                PolicyDecision.RequireApproval => ActionState.AwaitingApproval,
                _ => ActionState.Denied,
            };

            metrics.PolicyDecision(verdict.Decision, action.Type, verdict.DowngradedFrom is not null);

            // The prose belongs here, where cardinality is not a concern and a human reading
            // one investigation gets the whole verdict rather than its first line.
            policySpan?.AddEvent(new ActivityEvent(
                $"{action.Type} -> {verdict.Decision}",
                tags: new ActivityTagsCollection
                {
                    ["action.id"] = action.Id,
                    ["policy.decision"] = verdict.Decision.ToString(),
                    ["policy.downgraded_from"] = verdict.DowngradedFrom?.ToString(),
                    ["policy.reasons"] = string.Join("; ", verdict.Reasons),
                }));
        }

        var top = outcome.Plan.Actions[0];
        var summary = $"{outcome.Plan.Actions.Count} action(s) proposed; {top.Type} -> {top.Decision}: "
            + string.Join("; ", top.DecisionReasons.Take(2));

        // The plan as a whole goes wherever its STRONGEST verdict points, and the actions that
        // travel with it are only the ones that earned it. An Allow beside a Deny does not
        // make the Deny executable - Approved is the filter, and DecideOutcome above set it
        // from the policy verdict.
        var approved = outcome.Plan.Actions
            .Where(a => a.State == ActionState.Approved)
            .ToList();

        if (approved.Count > 0)
        {
            return Disposition.Act(approved, summary);
        }

        if (outcome.Plan.Actions.Any(a => a.State == ActionState.AwaitingApproval))
        {
            // Not an escalation. The agent has a plan it believes in and is asking, which is
            // a different thing to tell a human than "I could not work this out" - and it is
            // the normal outcome for every allow-eligible action until an operator opts its
            // type into AutoEnabledActionTypes.
            return Disposition.AwaitApproval(summary);
        }

        return Disposition.Escalate(EscalationReason.PolicyDenied, summary);
    }

    /// <summary>
    /// Runs the approved actions, then hands the incident to verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after the investigation has committed, in its own saves. Sequential on purpose:
    /// the actions in one plan are usually about the same workload, and the cooldown and
    /// per-workload budget that admission enforces are written to be evaluated one at a time.
    /// Firing them concurrently would have every one of them read the same pre-action counts.
    /// </para>
    /// <para>
    /// An action that is refused or fails does not stop the ones behind it - each is admitted
    /// on its own merits and its own budget - but if NOTHING was applied the incident
    /// escalates rather than pretending it is being verified.
    /// </para>
    /// </remarks>
    private async Task ActAsync(Incident incident, IReadOnlyList<AgentAction> approved, CancellationToken ct)
    {
        var applied = 0;
        var changed = 0;
        var attempted = 0;

        foreach (var action in approved)
        {
            attempted++;

            var result = await executor.ExecuteAsync(action, ct).ConfigureAwait(false);

            if (result.Outcome == ActionExecutionOutcome.Executed)
            {
                applied++;
            }

            if (result.Changed)
            {
                changed++;
            }

            notifier.Publish(new IncidentLiveEvent
            {
                IncidentId = incident.Id,
                Kind = IncidentLiveEventKind.StateChanged,
                State = incident.State,
                Detail = $"{action.Type}: {result.Outcome}{(result.DryRun ? " (dry run)" : string.Empty)}",
                At = clock.UtcNow,
            });
        }

        var eventsBefore = incident.Events.Count;

        if (changed == 0)
        {
            // Nothing in the cluster is different, so there is nothing to verify - and this
            // covers two situations worth keeping apart in the reason text.
            //
            // A DRY RUN is the interesting one. Every call was made and the API server
            // validated and discarded it, which is a successful outcome and still leaves the
            // fault in place. Sending it to Verifying would schedule three checks that must
            // fail, and a failed check triggers a rollback - of an action that never happened.
            // The would-have-acted log is for a human, so it goes to a human.
            var detail = applied > 0
                ? $"dry run: {applied} of {attempted} action(s) validated against the API server; "
                  + "nothing was changed"
                : $"none of {attempted} approved action(s) could be executed";

            stateMachine.Escalate(incident, EscalationReason.PolicyDenied, detail);

            incidents.TrackNewIncidentChildren(incident, eventsBefore);
            await RecordOutcomeAsync(incident, PrimaryHypothesis(incident), ct).ConfigureAwait(false);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation("Incident {IncidentId}: {Detail}", incident.Id, detail);

            return;
        }

        stateMachine.BeginVerifying(incident, $"{changed} of {attempted} action(s) applied");

        incidents.TrackNewIncidentChildren(incident, eventsBefore);
        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Incident {IncidentId} is verifying after {Changed}/{Attempted} action(s).",
            incident.Id, changed, attempted);
    }

    private static string? PrimaryHypothesis(Incident incident) =>
        incident.Investigations
            .SelectMany(i => i.Findings)
            .FirstOrDefault(f => f.IsPrimary)?.Hypothesis;

    private enum DispositionKind
    {
        Escalate = 0,
        AwaitApproval = 1,
        Act = 2,
    }

    /// <summary>
    /// What becomes of the incident once its plan has been judged.
    /// </summary>
    /// <remarks>
    /// This used to be an <see cref="EscalationReason"/> and nothing else, because there was
    /// only ever one answer: the method evaluated every action, recorded the verdicts, and
    /// escalated regardless - a policy Allow escalated exactly like a Deny.
    /// </remarks>
    private sealed record Disposition
    {
        public required DispositionKind Kind { get; init; }

        public EscalationReason Reason { get; init; }

        public string? Detail { get; init; }

        public IReadOnlyList<AgentAction> Actions { get; init; } = [];

        public static Disposition Escalate(EscalationReason reason, string? detail) =>
            new() { Kind = DispositionKind.Escalate, Reason = reason, Detail = detail };

        public static Disposition AwaitApproval(string? detail) =>
            new() { Kind = DispositionKind.AwaitApproval, Detail = detail };

        public static Disposition Act(IReadOnlyList<AgentAction> actions, string? detail) =>
            new() { Kind = DispositionKind.Act, Detail = detail, Actions = actions };

        public string Describe() =>
            Kind == DispositionKind.Escalate ? Reason.ToString() : Kind.ToString();
    }

    /// <summary>
    /// Composes and embeds the digest that outlives the incident. Evidence excerpts come from
    /// findings that already survived grounding verification - ungrounded text must never
    /// reach a record that is kept indefinitely while the logs behind it expire at 30 days.
    /// </summary>
    private async Task IndexAsync(Incident incident, Investigation investigation, CancellationToken ct)
    {
        try
        {
            var primary = investigation.Findings.FirstOrDefault(f => f.IsPrimary)
                          ?? investigation.Findings.OrderByDescending(f => f.Confidence).FirstOrDefault();

            var input = new IncidentDigestInput
            {
                Incident = incident,
                PrimaryFinding = primary,
                TopEvidence = [.. (primary?.Evidence ?? []).Select(e => e.Excerpt).Take(5)],
                Actions = incident.Actions,

                // Null, not false: nothing was executed, so nothing was verified. Recording
                // false here would read as "we tried and it did not work".
                VerificationPassed = null,
            };

            var existing = await db.IncidentDigests
                .FirstOrDefaultAsync(d => d.IncidentId == incident.Id, ct)
                .ConfigureAwait(false);

            var digest = await embedder.BuildAsync(input, existing, ct).ConfigureAwait(false);

            if (existing is null)
                db.IncidentDigests.Add(digest);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to index incident {IncidentId}; history search will miss it.", incident.Id);
        }
    }

    /// <summary>
    /// Stages an audit event into the current unit of work. It does NOT save.
    /// </summary>
    /// <remarks>
    /// <see cref="IAuditRepository.AppendAsync(AuditEvent, CancellationToken)"/> calls
    /// <c>SaveChangesAsync</c>, and every caller here shares one scoped
    /// <see cref="HephaistoDbContext"/> with the incident repository. Appending mid-method
    /// therefore flushes whatever else is staged at that moment - including an incident
    /// event that has not yet been stated Added - which is what made investigations fail to
    /// persist. The audit row belongs in the same commit as the thing it describes anyway:
    /// an audit trail that can survive the transaction it audits is not an audit trail.
    /// </remarks>
    private void EnlistAudit(Incident incident, Guid? investigationId, string type, string summary) =>
        audit.Enlist(new AuditEvent
        {
            At = clock.UtcNow,
            Type = type,
            IncidentId = incident.Id,
            InvestigationId = investigationId,
            Actor = IncidentStateMachine.SystemActor,
            Summary = summary,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        });
}
