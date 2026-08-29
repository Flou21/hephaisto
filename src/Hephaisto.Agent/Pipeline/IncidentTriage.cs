using Microsoft.Extensions.Options;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Fingerprinting;

namespace Hephaisto.Agent.Pipeline;

public enum TriageOutcome
{
    /// <summary>Folded into an incident that already exists. By far the most common outcome.</summary>
    Deduplicated,

    /// <summary>Attached to a related open incident on the same workload.</summary>
    Correlated,

    Suppressed,

    /// <summary>A new incident worth spending money on.</summary>
    Investigate,
}

public readonly record struct TriageResult(TriageOutcome Outcome, Guid IncidentId);

/// <summary>
/// Decides what an arriving signal means. Ordering here is not arbitrary - the cheap,
/// certain checks run before the expensive, judgemental ones, and the self-signal check runs
/// before everything.
/// </summary>
public sealed class IncidentTriage(
    IIncidentRepository incidents,
    IAuditRepository audit,
    IncidentStateMachine stateMachine,
    IClock clock,
    IOptionsMonitor<IngestOptions> options,
    HephaistoMetrics metrics,
    Observability.IGrafanaAnnotator annotator,
    ILogger<IncidentTriage> logger)
{
    public async Task<TriageResult> TriageAsync(Signal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var opts = options.CurrentValue;
        var now = clock.UtcNow;

        // 1. Dedup first: it is the cheapest check and the most likely to hit. An identical
        //    signal while its incident is still open is the same problem restating itself.
        var existing = await incidents
            .FindByFingerprintAsync(signal.Fingerprint, opts.BurstWindow, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            Attach(existing, signal, now);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Deduplicated, existing.Id);
        }

        // 2. Flap detection, before opening anything. A workload that has produced four
        //    incidents in an hour does not need a fifth investigation; it needs a human. Note
        //    this counts on the OWNER - keyed on the pod it would always be 1, because a
        //    crash-looping Deployment gets a new pod name every couple of minutes and nothing
        //    would ever look like flapping.
        var recent = await incidents
            .CountRecentForWorkloadAsync(signal.Target, opts.FlapWindow, ct)
            .ConfigureAwait(false);

        if (recent >= opts.FlapThreshold)
        {
            var flapping = OpenIncident(signal, now);
            metrics.IncidentOpened(flapping.Kind, flapping.Severity);
            await annotator.IncidentOpenedAsync(flapping, ct).ConfigureAwait(false);

            stateMachine.Triage(flapping, "flap detection");
            stateMachine.Suppress(flapping, SuppressionReason.Flapping,
                $"{recent} incidents for {signal.Target.WorkloadKey} in {opts.FlapWindow}");
            flapping.QuarantinedUntil = now + opts.FlapCooldown;

            await RecordOutcomeAsync(flapping, now, ct).ConfigureAwait(false);

            await incidents.AddAsync(flapping, ct).ConfigureAwait(false);
            EnlistAudit(flapping, "incident.suppressed", "Flapping workload");
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogWarning(
                "Workload {Workload} is flapping ({Count} incidents in {Window}); suppressed for {Cooldown}.",
                signal.Target.WorkloadKey, recent, opts.FlapWindow, opts.FlapCooldown);

            return new(TriageOutcome.Suppressed, flapping.Id);
        }

        // 3. Correlation: a different KIND of signal on the same workload is a facet of one
        //    problem, not a second problem. Merging them is what turns "OOMKilled" plus
        //    "latency high" plus "replica mismatch" into one thing a human reads once.
        var correlationKey = SignalFingerprinter.CorrelationKey(signal);
        var related = await incidents.FindByCorrelationKeyAsync(correlationKey, ct).ConfigureAwait(false);

        if (related is not null
            && related.IsOpen
            && now - related.LastSignalAt <= opts.CorrelationWindow)
        {
            Attach(related, signal, now);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Correlated, related.Id);
        }

        // 4. A genuinely new problem.
        var incident = OpenIncident(signal, now);
        await incidents.AddAsync(incident, ct).ConfigureAwait(false);

        metrics.IncidentOpened(incident.Kind, incident.Severity);
        metrics.DetectionLatency(now - signal.FirstSeen);

        await annotator.IncidentOpenedAsync(incident, ct).ConfigureAwait(false);

        stateMachine.Triage(incident, "new signal");

        // 5. Self-signals are hard-coded to escalate. Hephaisto alerting on Hephaisto is the
        //    point of the selfcheck rules; Hephaisto ACTING on Hephaisto is a feedback loop,
        //    and the cheapest place to break it is before an investigation ever starts.
        if (IsSelfSignal(signal, opts))
        {
            stateMachine.Escalate(incident, EscalationReason.SelfSignal,
                "signal concerns Hephaisto's own namespace");

            await RecordOutcomeAsync(incident, now, ct).ConfigureAwait(false);
            EnlistAudit(incident, "incident.escalated", "Self-signal");
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            return new(TriageOutcome.Suppressed, incident.Id);
        }

        stateMachine.BeginInvestigation(incident, "triage complete");
        EnlistAudit(incident, "incident.opened", signal.Reason);
        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

        return new(TriageOutcome.Investigate, incident.Id);
    }

    /// <summary>Used by the storm circuit breaker, which decides to escalate after triage has finished.</summary>
    public async Task EscalateAsync(Guid incidentId, EscalationReason reason, CancellationToken ct)
    {
        var incident = await incidents.GetAsync(incidentId, ct).ConfigureAwait(false);
        if (incident is null || !incident.IsOpen) return;

        var eventsBefore = incident.Events.Count;

        stateMachine.Escalate(incident, reason);

        // The incident already exists, so the transition event Escalate just appended is a
        // new child of a persisted parent - the case change detection gets wrong. This must
        // run before anything saves, which is why the audit row is enlisted rather than
        // appended below.
        incidents.TrackNewIncidentChildren(incident, eventsBefore);

        await RecordOutcomeAsync(incident, clock.UtcNow, ct).ConfigureAwait(false);
        EnlistAudit(incident, "incident.escalated", reason.ToString());

        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The closed counter and the MTTR histogram, from an incident that has just been moved to
    /// its outcome state.
    /// </summary>
    /// <remarks>
    /// Called after the transition, never before: it reads <see cref="Incident.State"/> for the
    /// <c>outcome</c> label, so calling it first would record every incident as still
    /// <c>Investigating</c>.
    /// </remarks>
    private async Task RecordOutcomeAsync(Incident incident, DateTimeOffset now, CancellationToken ct)
    {
        metrics.IncidentClosed(incident.Kind, incident.Severity, incident.State, now - incident.OpenedAt);

        // No diagnosis on this path - triage suppresses and escalates without ever running an
        // investigation - so the annotation carries the state and nothing more.
        await annotator.IncidentClosedAsync(incident, summary: null, ct).ConfigureAwait(false);
    }

    private static bool IsSelfSignal(Signal signal, IngestOptions opts) =>
        signal.Source == SignalSource.SelfMonitoring
        || opts.SelfNamespaces.Contains(signal.Target.Namespace);

    private void Attach(Incident incident, Signal signal, DateTimeOffset now)
    {
        incident.Signals.Add(signal);
        incident.LastSignalAt = now;
        signal.IncidentId = incident.Id;

        // Explicitly Added, not left to change detection. The navigation add above is not
        // enough on its own - see IIncidentRepository.AddSignal for why it produces an
        // UPDATE that matches no rows, and why that silently breaks dedup and correlation
        // while leaving the first signal of every incident working perfectly.
        incidents.AddSignal(signal);

        // The incident carries the worst severity any of its signals reported. A warning that
        // later turns critical must not stay filed as a warning.
        if (signal.Severity > incident.Severity)
            incident.Severity = signal.Severity;
    }

    private static Incident OpenIncident(Signal signal, DateTimeOffset now) => new()
    {
        CorrelationKey = SignalFingerprinter.CorrelationKey(signal),
        Title = $"{signal.Kind} on {signal.Target.OwnerName ?? signal.Target.Name} ({signal.Target.Namespace})",
        Kind = signal.Kind,
        Severity = signal.Severity,
        State = IncidentState.Detected,
        // Clone, never share: Target is an EF owned type on both entities, and one instance
        // attached to two owners breaks SaveChanges for every signal. See TargetRef.Clone.
        Target = signal.Target.Clone(),
        OpenedAt = now,
        LastSignalAt = now,
        Signals = [signal],
    };

    /// <summary>
    /// Stages an audit event into the current unit of work. It does NOT save - see the
    /// identical helper on <see cref="InvestigationCoordinator"/> for why appending here
    /// instead would flush a half-stated graph.
    /// </summary>
    private void EnlistAudit(Incident incident, string type, string summary) =>
        audit.Enlist(new AuditEvent
        {
            At = clock.UtcNow,
            Type = type,
            IncidentId = incident.Id,
            Actor = IncidentStateMachine.SystemActor,
            Summary = summary,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        });
}
