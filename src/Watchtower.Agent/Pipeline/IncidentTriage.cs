using Microsoft.Extensions.Options;
using Watchtower.Agent.Options;
using Watchtower.Agent.Persistence.Repositories;
using Watchtower.Core;
using Watchtower.Core.Abstractions;
using Watchtower.Core.Domain;
using Watchtower.Core.Fingerprinting;

namespace Watchtower.Agent.Pipeline;

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
    WatchtowerMetrics metrics,
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
            stateMachine.Triage(flapping, "flap detection");
            stateMachine.Suppress(flapping, SuppressionReason.Flapping,
                $"{recent} incidents for {signal.Target.WorkloadKey} in {opts.FlapWindow}");
            flapping.QuarantinedUntil = now + opts.FlapCooldown;

            await incidents.AddAsync(flapping, ct).ConfigureAwait(false);
            await AuditAsync(flapping, "incident.suppressed", "Flapping workload", ct).ConfigureAwait(false);
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

        metrics.IncidentOpened(signal.Kind);
        metrics.DetectionLatency(now - signal.FirstSeen);

        stateMachine.Triage(incident, "new signal");

        // 5. Self-signals are hard-coded to escalate. Watchtower alerting on Watchtower is the
        //    point of the selfcheck rules; Watchtower ACTING on Watchtower is a feedback loop,
        //    and the cheapest place to break it is before an investigation ever starts.
        if (IsSelfSignal(signal, opts))
        {
            stateMachine.Escalate(incident, EscalationReason.SelfSignal,
                "signal concerns Watchtower's own namespace");

            await AuditAsync(incident, "incident.escalated", "Self-signal", ct).ConfigureAwait(false);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            return new(TriageOutcome.Suppressed, incident.Id);
        }

        stateMachine.BeginInvestigation(incident, "triage complete");
        await AuditAsync(incident, "incident.opened", signal.Reason, ct).ConfigureAwait(false);
        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

        return new(TriageOutcome.Investigate, incident.Id);
    }

    /// <summary>Used by the storm circuit breaker, which decides to escalate after triage has finished.</summary>
    public async Task EscalateAsync(Guid incidentId, EscalationReason reason, CancellationToken ct)
    {
        var incident = await incidents.GetAsync(incidentId, ct).ConfigureAwait(false);
        if (incident is null || !incident.IsOpen) return;

        stateMachine.Escalate(incident, reason);
        await AuditAsync(incident, "incident.escalated", reason.ToString(), ct).ConfigureAwait(false);
        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
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

    private Task AuditAsync(Incident incident, string type, string summary, CancellationToken ct) =>
        audit.AppendAsync(new AuditEvent
        {
            At = clock.UtcNow,
            Type = type,
            IncidentId = incident.Id,
            Actor = "watchtower/system",
            Summary = summary,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            SpanId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
        }, ct);
}
