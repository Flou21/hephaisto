using Microsoft.Extensions.Options;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Classification;
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

    /// <summary>A resolve: its alert instance is no longer firing, and nothing opened.</summary>
    Cleared,

    /// <summary>A resolve for an alert the agent has no incident for. Nothing happened.</summary>
    Ignored,
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
    ILogger<IncidentTriage> logger,
    IOptionsMonitor<Core.Notifications.NotificationOptions>? notifications = null)
{
    public async Task<TriageResult> TriageAsync(Signal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // An alert has a lifecycle the Kubernetes watch does not: it resolves, it repeats on
        // Alertmanager's timer, and it comes back. See TriageAlertAsync.
        if (signal.Source == SignalSource.Alertmanager && signal.AlertKey is not null)
        {
            return await TriageAlertAsync(signal, ct).ConfigureAwait(false);
        }

        // The watcher saying a workload it reported is healthy again (#158).
        if (signal.Source == SignalSource.KubernetesWatch && signal.Status == SignalStatus.Resolved)
        {
            return await ClearWatchedAsync(signal, clock.UtcNow, ct).ConfigureAwait(false);
        }

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

    /// <summary>
    /// One alert, for as long as it fires (#129, #130).
    /// </summary>
    /// <remarks>
    /// <code>
    /// RESOLVED  no matching row            -> Ignored, nothing opens
    ///           row found                  -> row.Status = Resolved
    ///           no other firing row on it  -> Acting|Verifying: left to the verifier
    ///                                         AwaitingApproval: proposal expired, AlertCleared
    ///                                         otherwise:        AlertCleared (-> Closed)
    /// FIRING    open incident, row exists  -> absorbed, whatever its age
    ///           open incident, no row      -> new row attached
    ///           closed by a person, still  -> absorbed silently: no page per repeat
    ///             firing since
    ///           ended inside ReopenWindow  -> Reopen (-> Triaging), decided like a new one
    ///           otherwise                  -> flap (per cluster) -> correlation -> open
    /// </code>
    /// <para>
    /// One row per alert instance (<see cref="Signal.AlertKey"/>), updated in place. A repeat
    /// used to be a new row every time, which is how "fired three times" became three rows and
    /// "has everything on it cleared" became unanswerable.
    /// </para>
    /// <para>
    /// <b>Never Suppressed.</b> An Alertmanager alert that flaps is escalated instead: grouping
    /// a noisy alert is Alertmanager's job, and an incident suppressed here is a page nobody
    /// receives (#147).
    /// </para>
    /// </remarks>
    private async Task<TriageResult> TriageAlertAsync(Signal signal, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var now = clock.UtcNow;
        var key = signal.AlertKey!;

        if (signal.Status == SignalStatus.Resolved)
        {
            return await ClearAsync(signal, key, now, ct).ConfigureAwait(false);
        }

        // 1. The same alert, still open: absorbed however long ago it last spoke. The burst and
        //    correlation windows are for recognising different kinds of trouble on one workload,
        //    not the same alert repeating on Alertmanager's timer.
        var open = await incidents.FindOpenByFingerprintAsync(signal.Fingerprint, ct).ConfigureAwait(false);

        if (open is not null)
        {
            await AbsorbAsync(open, signal, key, now, ct).ConfigureAwait(false);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Deduplicated, open.Id);
        }

        // 2. The same alert, ended.
        var ended = await incidents.FindLastEndedByFingerprintAsync(signal.Fingerprint, ct).ConfigureAwait(false);

        if (ended is not null)
        {
            var row = await incidents.FindAlertRowAsync(ended.Id, key, ct).ConfigureAwait(false);

            // A person closed it while this alert was firing, and it has not stopped since. The
            // repeat is Alertmanager restating what that person already decided about; paging
            // them for it every repeat_interval is how a pager gets muted.
            if (ended.State == IncidentState.Closed
                && ended.ClosedBy != IncidentStateMachine.AlertmanagerActor
                && row is { Status: SignalStatus.Firing })
            {
                Touch(row, signal, now);
                await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
                return new(TriageOutcome.Deduplicated, ended.Id);
            }

            var endedAt = ended.ClosedAt ?? ended.ResolvedAt;

            if (endedAt is { } at && now - at <= opts.ReopenWindow)
            {
                return await ReopenAsync(ended, row, signal, key, now, ct).ConfigureAwait(false);
            }
        }

        // 3. Flap detection, per cluster and never for an alert that names no object: its
        //    "workload" is the rule, and every series of a rule would count against the others.
        if (!signal.Target.IsAlertOnly)
        {
            var recent = await incidents
                .CountRecentForWorkloadAsync(signal.Target, opts.FlapWindow, ct)
                .ConfigureAwait(false);

            if (recent >= opts.FlapThreshold)
            {
                var flapping = await OpenAlertIncidentAsync(signal, now, ct).ConfigureAwait(false);
                stateMachine.Triage(flapping, "flap detection");
                stateMachine.Escalate(flapping, EscalationReason.Flapping,
                    $"flapping: {recent} incidents for {signal.Target.WorkloadKey} in {opts.FlapWindow}");
                flapping.QuarantinedUntil = now + opts.FlapCooldown;

                await RecordOutcomeAsync(flapping, now, ct).ConfigureAwait(false);
                EnlistAudit(flapping, "incident.escalated", "Flapping workload; not investigated");
                await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

                return new(TriageOutcome.Suppressed, flapping.Id);
            }
        }

        // 4. Correlation, measured from when the related incident opened or reopened rather than
        //    from its last signal: an alert that repeats must not keep a correlation window open
        //    for ever.
        var related = await incidents
            .FindByCorrelationKeyAsync(SignalFingerprinter.CorrelationKey(signal), ct)
            .ConfigureAwait(false);

        if (related is not null
            && related.IsOpen
            && now - (related.ReopenedAt ?? related.OpenedAt) <= opts.CorrelationWindow)
        {
            Attach(related, signal, now);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Correlated, related.Id);
        }

        // 5. A new incident.
        var incident = await OpenAlertIncidentAsync(signal, now, ct).ConfigureAwait(false);
        stateMachine.Triage(incident, "new alert");

        return await DecideAsync(incident, signal, now, ct).ConfigureAwait(false);
    }

    private async Task<Incident> OpenAlertIncidentAsync(Signal signal, DateTimeOffset now, CancellationToken ct)
    {
        var incident = OpenIncident(signal, now);
        await incidents.AddAsync(incident, ct).ConfigureAwait(false);

        metrics.IncidentOpened(incident.Kind, incident.Severity);
        metrics.DetectionLatency(now - signal.FirstSeen);
        await annotator.IncidentOpenedAsync(incident, ct).ConfigureAwait(false);

        return incident;
    }

    /// <summary>The last step for a new or reopened incident: escalate a self-signal, else investigate.</summary>
    /// <param name="trackFrom">
    /// For an incident that already exists, the index of its first event appended in this unit of
    /// work: those are new rows EF would otherwise state as updates matching nothing. See
    /// <see cref="IIncidentRepository.TrackNewIncidentChildren"/>.
    /// </param>
    private async Task<TriageResult> DecideAsync(
        Incident incident,
        Signal signal,
        DateTimeOffset now,
        CancellationToken ct,
        int? trackFrom = null)
    {
        if (IsSelfSignal(signal, options.CurrentValue))
        {
            stateMachine.Escalate(incident, EscalationReason.SelfSignal, "signal concerns Hephaisto's own namespace");
            if (trackFrom is { } from) incidents.TrackNewIncidentChildren(incident, from);

            await RecordOutcomeAsync(incident, now, ct).ConfigureAwait(false);
            EnlistAudit(incident, "incident.escalated", "Self-signal");
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            return new(TriageOutcome.Suppressed, incident.Id);
        }

        // The rule said not to investigate (#134). Opened and told, never asked.
        if (!InvestigationPolicy.ShouldInvestigate(signal.Labels))
        {
            stateMachine.Escalate(incident, EscalationReason.NotInvestigated,
                $"the rule is labelled {InvestigationPolicy.Label}=false");
            if (trackFrom is { } from2) incidents.TrackNewIncidentChildren(incident, from2);

            await RecordOutcomeAsync(incident, now, ct).ConfigureAwait(false);
            EnlistAudit(incident, "incident.opened", $"{signal.Reason}; not investigated by the rule's choice");
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

            return new(TriageOutcome.Suppressed, incident.Id);
        }

        stateMachine.BeginInvestigation(incident, "triage complete");
        if (trackFrom is { } start) incidents.TrackNewIncidentChildren(incident, start);
        EnlistAudit(incident, "incident.opened", signal.Reason);
        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

        return new(TriageOutcome.Investigate, incident.Id);
    }

    /// <summary>
    /// The alert came back within the reopen window: the same incident, decided again.
    /// </summary>
    private async Task<TriageResult> ReopenAsync(
        Incident incident,
        Signal? row,
        Signal signal,
        string key,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var eventsBefore = incident.Events.Count;

        stateMachine.Reopen(incident, $"{signal.Reason} fired again");

        if (row is not null)
        {
            Touch(row, signal, now);
            incident.LastSignalAt = now;
            RaiseSeverity(incident, signal);
        }
        else
        {
            Attach(incident, signal, now);
        }

        metrics.IncidentReopened(incident.Kind, incident.Severity);
        EnlistAudit(incident, "incident.reopened", $"{signal.Reason} fired again");

        // A reopen is news, and a person is told either way. Whether it is also worth another
        // investigation depends on how often it has come back: at the flap threshold the answer
        // is a person, not another model run on the same fault.
        var reopens = await incidents.CountReopensAsync(incident.Id, now - opts.FlapWindow, ct).ConfigureAwait(false);

        if (reopens + 1 >= opts.FlapThreshold)
        {
            stateMachine.Escalate(incident, EscalationReason.Flapping,
                $"flapping: reopened {reopens + 1} times in {opts.FlapWindow}");
            incidents.TrackNewIncidentChildren(incident, eventsBefore);

            await RecordOutcomeAsync(incident, now, ct).ConfigureAwait(false);
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Suppressed, incident.Id);
        }

        return await DecideAsync(incident, signal, now, ct, trackFrom: eventsBefore).ConfigureAwait(false);
    }

    /// <summary>
    /// A resolve. It closes the incident once nothing on it still fires, and opens nothing.
    /// </summary>
    private async Task<TriageResult> ClearAsync(Signal signal, string key, DateTimeOffset now, CancellationToken ct)
    {
        var open = await incidents.FindOpenByFingerprintAsync(signal.Fingerprint, ct).ConfigureAwait(false);

        var row = open is not null
            ? await incidents.FindAlertRowAsync(open.Id, key, ct).ConfigureAwait(false)
            : await incidents.FindLatestAlertRowAsync(signal.Fingerprint, key, ct).ConfigureAwait(false);

        if (row is null)
        {
            // Resolved before it was ever seen - the agent was down, or installed after the alert
            // fired. The fault going away is not something to tell anybody about.
            metrics.SignalDropped(signal.Source, "resolved-unmatched");
            return new(TriageOutcome.Ignored, Guid.Empty);
        }

        row.Status = SignalStatus.Resolved;
        row.LastSeen = signal.LastSeen;

        if (open is null)
        {
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Cleared, row.IncidentId ?? Guid.Empty);
        }

        if (await incidents.HasOtherFiringAlertsAsync(open.Id, row.Id, ct).ConfigureAwait(false))
        {
            await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(TriageOutcome.Cleared, open.Id);
        }

        await EndClearedAsync(open, byWatcher: false, signal.Reason, now, ct).ConfigureAwait(false);

        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(TriageOutcome.Cleared, open.Id);
    }

    /// <summary>
    /// The watcher's resolve: the workload has run cleanly for the quiet period (#158).
    /// </summary>
    /// <remarks>
    /// One statement covers every watcher signal on the incident. They are facets of one
    /// workload - an OOMKill and the BackOff event beside it - and the workload is what healed.
    /// An alert still firing on the incident keeps it open: Alertmanager has not said it is over.
    /// </remarks>
    private async Task<TriageResult> ClearWatchedAsync(Signal signal, DateTimeOffset now, CancellationToken ct)
    {
        var open = await incidents.FindOpenByFingerprintAsync(signal.Fingerprint, ct).ConfigureAwait(false);

        if (open is null)
        {
            metrics.SignalDropped(signal.Source, "resolved-unmatched");
            return new(TriageOutcome.Ignored, Guid.Empty);
        }

        if (!await incidents.HasOtherFiringAlertsAsync(open.Id, Guid.Empty, ct).ConfigureAwait(false))
        {
            await EndClearedAsync(open, byWatcher: true, signal.Message, now, ct).ConfigureAwait(false);
        }

        await incidents.SaveChangesAsync(ct).ConfigureAwait(false);

        // After the save, and not in it: if this fails the incident is closed with rows that
        // still read firing, where the other order would leave it open with nothing to find it by.
        await incidents.ResolveWatchSignalsAsync(open.Id, ct).ConfigureAwait(false);

        return new(TriageOutcome.Cleared, open.Id);
    }

    /// <summary>Closes an incident whose fault is over, unless an action is in flight.</summary>
    private async Task EndClearedAsync(Incident open, bool byWatcher, string detail, DateTimeOffset now, CancellationToken ct)
    {
        // The verifier reads exactly this - the fault going away after the action - and
        // decides. Closing underneath it would record a fix as an accident.
        if (open.State is IncidentState.Acting or IncidentState.Verifying)
        {
            return;
        }

        var eventsBefore = open.Events.Count;

        if (open.State == IncidentState.AwaitingApproval)
        {
            foreach (var action in open.Actions.Where(a => a.State == ActionState.AwaitingApproval))
            {
                action.State = ActionState.Expired;
                action.Error = byWatcher
                    ? "The workload healed before anyone approved this."
                    : "The alert cleared before anyone approved this.";
            }
        }

        if (byWatcher)
        {
            stateMachine.FaultCleared(open, detail);
        }
        else
        {
            stateMachine.AlertCleared(open, detail);
        }

        incidents.TrackNewIncidentChildren(open, eventsBefore);

        await RecordOutcomeAsync(open, now, ct).ConfigureAwait(false);
        EnlistAudit(
            open,
            "incident.cleared",
            byWatcher ? detail : $"{detail} resolved and nothing on the incident still fires");
    }

    /// <summary>A firing alert on its open incident: its row updated, or a row added.</summary>
    private async Task AbsorbAsync(Incident incident, Signal signal, string key, DateTimeOffset now, CancellationToken ct)
    {
        var row = await incidents.FindAlertRowAsync(incident.Id, key, ct).ConfigureAwait(false);

        if (row is null)
        {
            Attach(incident, signal, now);
            return;
        }

        Touch(row, signal, now);
        incident.LastSignalAt = now;
        RaiseSeverity(incident, signal);
    }

    /// <summary>One more observation of an alert instance that already has its row.</summary>
    private static void Touch(Signal row, Signal signal, DateTimeOffset now)
    {
        row.Count++;
        row.LastSeen = now;
        row.Status = SignalStatus.Firing;
        row.Severity = signal.Severity > row.Severity ? signal.Severity : row.Severity;
        row.Message = signal.Message;
    }

    private void RaiseSeverity(Incident incident, Signal signal)
    {
        if (signal.Severity <= incident.Severity)
        {
            return;
        }

        var before = incident.Severity;
        incident.Severity = signal.Severity;

        NotifyRaised(incident, before);
    }

    /// <summary>
    /// A warning that turns critical is news to a route that wants criticals (#148).
    /// </summary>
    /// <remarks>
    /// A raise is not a transition, and notifications are enqueued on transitions, so until now a
    /// route with <c>minSeverity: Critical</c> never heard of an incident that opened as a warning.
    /// The routes that carry <c>IncidentOpened</c> are asked again at the new severity; whoever
    /// they reach now and did not reach before is told, as <c>SeverityRaised</c>.
    /// </remarks>
    private void NotifyRaised(Incident incident, Severity before)
    {
        var routes = notifications?.CurrentValue.Routes;

        if (routes is null || routes.Count == 0 || incident.State is IncidentState.Detected)
        {
            return;
        }

        var now = clock.UtcNow;
        var transition = new IncidentEvent
        {
            IncidentId = incident.Id,
            From = incident.State,
            To = incident.State,
            Reason = $"severity raised from {before} to {incident.Severity}",
            At = now,
        };

        var opened = Notifications.NotificationEnqueue.Snapshot(Core.Notifications.NotificationEvent.IncidentOpened, transition, incident);
        var then = Core.Notifications.NotificationRouter.Match(opened with { Severity = before }, routes).Matches;
        var nowMatches = Core.Notifications.NotificationRouter.Match(opened, routes).Matches;

        foreach (var match in nowMatches)
        {
            var old = then.FirstOrDefault(m => string.Equals(m.Channel, match.Channel, StringComparison.Ordinal));
            var recipients = match.Recipients
                .Where(r => old is null || !old.Recipients.Contains(r, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var channelList = match.UsesChannelRecipients && old is not { UsesChannelRecipients: true };

            if (recipients.Count == 0 && !channelList)
            {
                continue;
            }

            incidents.EnlistNotification(new Persistence.NotificationDelivery
            {
                Event = Core.Notifications.NotificationEvent.SeverityRaised,
                IncidentId = incident.Id,
                Channel = match.Channel,
                Recipients = recipients,
                UsesChannelRecipients = channelList,
                Routes = [.. match.Routes],
                CorrelationKey = incident.CorrelationKey,
                Status = Core.Notifications.DeliveryStatus.Pending,
                Snapshot = opened with { Event = Core.Notifications.NotificationEvent.SeverityRaised },
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }
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

    // The agent's own namespaces only mean the agent in the agent's own cluster (#131): a
    // namespace called "hephaisto" elsewhere is somebody else's workload.
    private static bool IsSelfSignal(Signal signal, IngestOptions opts) =>
        signal.Source == SignalSource.SelfMonitoring
        || (opts.SelfNamespaces.Contains(signal.Target.Namespace) && !signal.Target.IsForeignTo(opts.ClusterName));

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
        RaiseSeverity(incident, signal);

        Relabel(incident, signal);
    }

    /// <summary>
    /// Re-labels an incident when a later signal identifies the failure more specifically
    /// than the one that happened to open it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backlog #70. The kind used to be written once, by whichever signal won the race to
    /// open the incident, and never reconsidered - so <c>KubePodNotReady</c> (generic,
    /// <c>for: 2m</c>) beating <c>KubeContainerWaiting</c> (specific, <c>for: 1m</c>) under
    /// load permanently labelled a bad image tag as a readiness problem. Since
    /// <see cref="SignalKind"/> selects the runbook, the investigation was then handed
    /// instructions written for a different failure.
    /// </para>
    /// <para>
    /// The precedent is directly above: severity is already promoted on attach, for the same
    /// reason and with the same shape. The kind is the more consequential of the two.
    /// </para>
    /// <para>
    /// <b>Only before the investigation starts.</b> Once a runbook has been read into a
    /// prompt, changing the label underneath it does not correct the investigation - it
    /// conceals that the investigation was run against the wrong instructions, which is
    /// strictly worse than leaving the wrong label where somebody can see it. The signals
    /// that race are all early anyway: the shortest generic rule is <c>for: 2m</c> and the
    /// correlation window is ten minutes.
    /// </para>
    /// <para>
    /// The re-label is audited rather than silent. An incident whose kind changed after it
    /// opened is exactly the kind of thing that should be visible when someone is working out
    /// why a runbook was chosen.
    /// </para>
    /// </remarks>
    private void Relabel(Incident incident, Signal signal)
    {
        if (incident.State is not (IncidentState.Detected or IncidentState.Triaging))
        {
            return;
        }

        if (!SignalKindSpecificity.ShouldReplace(incident.Kind, signal.Kind))
        {
            return;
        }

        var was = incident.Kind;

        incident.Kind = signal.Kind;
        incident.Title = TitleFor(signal.Kind, incident.Target, signal.Labels);

        EnlistAudit(
            incident,
            "incident.reclassified",
            $"{was} -> {signal.Kind} ({signal.Reason})");
    }

    /// <summary>
    /// What a person reads on the board.
    /// </summary>
    /// <remarks>
    /// An alert that names no object is titled by the labels that tell its series apart (#132) -
    /// otherwise two providers failing are two lines reading the same - and one about another
    /// cluster says which (#131). The namespace only when there is one: "()" read as a bug.
    /// </remarks>
    private string TitleFor(SignalKind kind, TargetRef target, IReadOnlyDictionary<string, string> labels)
    {
        var subject = target.OwnerName ?? target.Name;

        if (target.IsAlertOnly && AlertIdentity.Distinguishing(labels) is { Length: > 0 } which)
        {
            subject = $"{subject} [{which}]";
        }

        var where = string.Join(", ", new[]
        {
            target.Namespace,
            target.IsForeignTo(options.CurrentValue.ClusterName) ? $"cluster {target.Cluster}" : string.Empty,
        }.Where(p => p.Length > 0));

        return where.Length > 0 ? $"{kind} on {subject} ({where})" : $"{kind} on {subject}";
    }

    private Incident OpenIncident(Signal signal, DateTimeOffset now) => new()
    {
        CorrelationKey = SignalFingerprinter.CorrelationKey(signal),
        Title = TitleFor(signal.Kind, signal.Target, signal.Labels),
        Kind = signal.Kind,
        Severity = signal.Severity,
        State = IncidentState.Detected,
        // Clone, never share: Target is an EF owned type on both entities, and one instance
        // attached to two owners breaks SaveChanges for every signal. See TargetRef.Clone.
        Target = signal.Target.Clone(),
        Labels = signal.Labels
            .Where(kv => !AlertIdentity.ScrapeLabels.Contains(kv.Key) && !AlertIdentity.IsAgentLabel(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        AlertName = signal.Source == SignalSource.Alertmanager ? signal.Reason : null,
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
