using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Agent.Investigations;
using Watchtower.Agent.Llm;
using Watchtower.Agent.Persistence;
using Watchtower.Agent.Persistence.Repositories;
using Watchtower.Agent.Safety;
using Watchtower.Agent.Web;
using Watchtower.Core;
using Watchtower.Core.Abstractions;
using Watchtower.Core.Domain;
using Watchtower.Core.Policy;

namespace Watchtower.Agent.Pipeline;

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
    WatchtowerDbContext db,
    IIncidentRepository incidents,
    IAuditRepository audit,
    IKillSwitch killSwitch,
    IncidentStateMachine stateMachine,
    InvestigationRunner runner,
    IncidentEmbedder embedder,
    IIncidentNotifier notifier,
    IOptionsMonitor<PolicyOptions> policyOptions,
    IClock clock,
    WatchtowerMetrics metrics,
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
        // WATCHTOWER_MODE=observe or edits the switch ConfigMap has to be able to hold this
        // incident below whatever the row says, or those two arms are decorative.
        var mode = (await killSwitch.ResolveAsync(ct).ConfigureAwait(false)).Effective;
        incident.Mode = mode;

        notifier.Publish(new IncidentLiveEvent
        {
            IncidentId = incident.Id,
            Kind = IncidentLiveEventKind.InvestigationStarted,
        });

        var started = clock.UtcNow;
        InvestigationOutcome outcome;

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
            await AuditAsync(incident, null, "investigation.failed", ex.Message, ct).ConfigureAwait(false);

            // The transition event Escalate just appended is a new child of an incident that
            // already exists, so change detection states it wrongly and the save throws. On
            // THIS path that is especially bad: the escalation exists precisely to record
            // that an investigation failed, so losing it means a failed investigation leaves
            // the incident stuck in Investigating with nothing explaining why.
            incidents.TrackNewIncidentChildren(incident, failedEventsBefore);

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

        foreach (var rejection in outcome.Rejections)
            metrics.GroundingRejected(rejection.ToString() ?? "unknown");

        var escalation = DecideOutcome(incident, outcome, mode);

        // Snapshot before the transition so the event it appends can be Added explicitly.
        var eventsBefore = incident.Events.Count;

        stateMachine.Escalate(incident, escalation.Reason, escalation.Detail);

        await AuditAsync(incident, investigation.Id, "investigation.completed",
            $"{investigation.TerminationReason}; {escalation.Reason}", ct).ConfigureAwait(false);

        // The investigation and the transition event are both new children of an incident
        // that already exists, which is the case EF Core states wrongly - it sees their
        // assigned keys and emits UPDATEs. The blobs above carry a foreign key to the
        // investigation, so without this the blob insert fails against a parent row that
        // was never written.
        incidents.TrackNewIncidentChildren(incident, eventsBefore);

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
            Detail = escalation.Detail,
        });
    }

    /// <summary>
    /// Runs every proposed action past the policy engine, records the verdict on the action,
    /// and decides why the incident is going to a human.
    /// </summary>
    private (EscalationReason Reason, string? Detail) DecideOutcome(
        Incident incident,
        InvestigationOutcome outcome,
        AgentMode mode)
    {
        if (outcome.Escalation is { } forced)
            return (forced, outcome.Investigation.TerminationReason.ToString());

        if (outcome.Plan is null || outcome.Plan.NoActionRequired || outcome.Plan.Actions.Count == 0)
        {
            // Not a failure. Most incidents want a diagnosis, and a model that declines to
            // act when it cannot justify acting is behaving correctly.
            return (EscalationReason.NoPlanProduced, "diagnosis only; no action proposed");
        }

        var options = policyOptions.CurrentValue;
        var now = clock.UtcNow;

        var facts = new ClusterFacts
        {
            Now = now,
            Mode = mode,
            TargetLabels = new Dictionary<string, string>(),
            QuarantinedUntil = incident.QuarantinedUntil,
        };

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

            var verdict = PolicyEngine.Evaluate(request, facts, options);

            action.Decision = verdict.Decision;
            action.DecisionReasons = [.. verdict.Reasons];
            action.State = verdict.Decision switch
            {
                PolicyDecision.Allow => ActionState.Approved,
                PolicyDecision.RequireApproval => ActionState.AwaitingApproval,
                _ => ActionState.Denied,
            };

            metrics.PolicyDecision(verdict.Decision, action.Type, verdict.Reasons.FirstOrDefault() ?? "none");
        }

        var top = outcome.Plan.Actions[0];
        return (EscalationReason.PolicyDenied,
            $"{outcome.Plan.Actions.Count} action(s) proposed; {top.Type} -> {top.Decision}: "
            + string.Join("; ", top.DecisionReasons.Take(2)));
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

    private Task AuditAsync(Incident incident, Guid? investigationId, string type, string summary, CancellationToken ct) =>
        audit.AppendAsync(new AuditEvent
        {
            At = clock.UtcNow,
            Type = type,
            IncidentId = incident.Id,
            InvestigationId = investigationId,
            Actor = "watchtower/system",
            Summary = summary,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        }, ct);
}
