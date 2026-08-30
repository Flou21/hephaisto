using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Safety;
using Hephaisto.Core;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.ServiceDefaults;

namespace Hephaisto.Agent.Web;

/// <summary>What the list endpoint and the list page both narrow by.</summary>
public sealed record IncidentListQuery
{
    /// <summary>Exact state. Mutually exclusive with <see cref="OpenOnly"/>; state wins.</summary>
    public IncidentState? State { get; init; }

    /// <summary>The default view. "Open" is the seven live states, not "not resolved".</summary>
    public bool OpenOnly { get; init; }

    public SignalKind? Kind { get; init; }

    public string? Namespace { get; init; }

    public int Limit { get; init; } = 100;
}

/// <summary>
/// The read side of the human surface: every query the API endpoints and the Blazor pages
/// share, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton over a scope factory, not a scoped service.</b> A Blazor Server component
/// lives as long as its circuit - hours, for a console left open on a wall display - and a
/// scoped dependency injected into one is captured for that entire lifetime. Injecting a
/// <see cref="HephaistoDbContext"/> into a page would therefore pin one connection and one
/// change tracker per open browser tab, and the tracker would slowly accumulate every
/// incident anyone ever looked at. Opening a scope per call gives each query a fresh context
/// that is disposed immediately, which is what a DbContext is built for.
/// </para>
/// <para>
/// Everything here is <c>AsNoTracking</c> and maps to the view records in
/// <c>ViewModels.cs</c> before the scope closes. Handing a tracked entity to a component
/// outliving its context is a lazy-load exception waiting for the first collapsed section
/// someone expands.
/// </para>
/// </remarks>
public sealed class IncidentQueries(
    IServiceScopeFactory scopes,
    IKillSwitch killSwitch,
    IIncidentNotifier notifier,
    WatchdogMonitor watchdog,
    Pipeline.InvestigationTracker tracker,
    Pipeline.InvestigationQueue queue,
    IOptionsMonitor<LlmBudgetOptions> budgetOptions,
    IClock clock,
    ILogger<IncidentQueries> logger)
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<IncidentListItem>> ListAsync(IncidentListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var incidents = db.Incidents.AsNoTracking();

        if (query.State is { } state)
        {
            incidents = incidents.Where(i => i.State == state);
        }
        else if (query.OpenOnly)
        {
            // The mapped array, not Incident.IsOpen: that property has no backing column and
            // cannot appear in a translated query.
            incidents = incidents.Where(i => HephaistoDbContext.OpenStates.Contains(i.State));
        }

        if (query.Kind is { } kind)
        {
            incidents = incidents.Where(i => i.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Namespace))
        {
            var ns = query.Namespace.Trim();
            incidents = incidents.Where(i => i.Target.Namespace == ns);
        }

        var limit = Math.Clamp(query.Limit, 1, 500);

        var rows = await incidents
            .OrderByDescending(i => i.OpenedAt)
            .Take(limit)
            .Select(i => new IncidentListItem
            {
                Id = i.Id,
                Title = i.Title,
                Kind = i.Kind,
                Severity = i.Severity,
                State = i.State,
                SuppressionReason = i.SuppressionReason,
                EscalationReason = i.EscalationReason,
                Namespace = i.Target.Namespace,
                TargetKind = i.Target.Kind,
                TargetName = i.Target.Name,
                OwnerKind = i.Target.OwnerKind,
                OwnerName = i.Target.OwnerName,
                OpenedAt = i.OpenedAt,
                LastSignalAt = i.LastSignalAt,
                ResolvedAt = i.ResolvedAt,
                SignalCount = i.Signals.Count,
                InvestigationCount = i.Investigations.Count,
                HasDiagnosis = i.Investigations.Any(v => v.Findings.Any()),
            })
            .ToListAsync(ct);

        // Joined after the query, not inside it: the projection above is translated to SQL,
        // and "is a worker running this right now" lives in this process's memory rather
        // than in any table.
        return [.. rows.Select(row =>
        {
            if (tracker.For(row.Id) is not { } live)
            {
                return row;
            }

            return row with
            {
                InProgress = new InvestigationProgressView
                {
                    Model = live.Model,
                    StartedAt = live.StartedAt,
                    Steps = live.Steps,
                    ToolCalls = live.ToolCalls,
                    CostUsd = live.CostUsd,
                    Activity = live.Activity,
                },
            };
        })];
    }

    /// <summary>The distinct namespaces present in incident history, for the filter dropdown.</summary>
    public async Task<IReadOnlyList<string>> NamespacesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        return await db.Incidents
            .AsNoTracking()
            .Select(i => i.Target.Namespace)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);
    }

    public async Task<IncidentDetailView?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var incidents = sp.GetRequiredService<IIncidentRepository>();
        var db = sp.GetRequiredService<HephaistoDbContext>();

        var incident = await incidents.GetWithDetailAsync(id, ct);

        if (incident is null)
        {
            return null;
        }

        // GetWithDetailAsync loads signals, transitions and actions. The investigation
        // aggregate is fetched separately rather than added to that method: it is four more
        // collection Includes, nobody but this page wants them, and widening a repository
        // method used on the ingest hot path to serve one screen is how a hot path gets slow.
        var investigations = await db.Investigations
            .AsNoTracking()
            .Where(v => v.IncidentId == id)
            .Include(v => v.Steps)
            .Include(v => v.Findings)
                .ThenInclude(f => f.Evidence)
            .Include(v => v.Plan!)
                .ThenInclude(p => p.Actions)
            .AsSplitQuery()
            .OrderBy(v => v.StartedAt)
            .ToListAsync(ct);

        var feedback = await db.HumanFeedback
            .AsNoTracking()
            .Where(f => f.IncidentId == id)
            .OrderByDescending(f => f.At)
            .ToListAsync(ct);

        return new IncidentDetailView
        {
            InProgress = tracker.For(incident.Id) is { } live
                ? new InvestigationProgressView
                {
                    Model = live.Model,
                    StartedAt = live.StartedAt,
                    Steps = live.Steps,
                    ToolCalls = live.ToolCalls,
                    CostUsd = live.CostUsd,
                    Activity = live.Activity,

                    // Mapped through the same MapStep the finished investigations use, so a
                    // step renders identically whether it is still running or was read back
                    // from Postgres. Two renderers for one thing is how they drift.
                    StepLog = [.. live.StepLog.Select(MapStep)],
                }
                : null,
            Id = incident.Id,
            Title = incident.Title,
            CorrelationKey = incident.CorrelationKey,
            Kind = incident.Kind,
            Severity = incident.Severity,
            State = incident.State,
            SuppressionReason = incident.SuppressionReason,
            EscalationReason = incident.EscalationReason,
            Target = TargetView.From(incident.Target),
            ModeAtOpen = incident.Mode,
            OpenedAt = incident.OpenedAt,
            LastSignalAt = incident.LastSignalAt,
            ResolvedAt = incident.ResolvedAt,
            QuarantinedUntil = incident.QuarantinedUntil,
            Resolution = incident.Resolution,
            Signals = [.. incident.Signals.OrderBy(s => s.FirstSeen).Select(MapSignal)],
            Transitions = [.. incident.Events.OrderBy(e => e.At).Select(MapTransition)],
            Investigations = [.. investigations.Select(MapInvestigation)],
            Actions = [.. incident.Actions.OrderBy(a => a.Id).Select(MapAction)],
            Feedback = [.. feedback.Select(MapFeedback)],
        };
    }

    /// <summary>
    /// Hybrid search over incident history: full text, vector similarity and trigrams.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The query embedding is generated here. It used to be passed as null on the grounds that
    /// generating one was the retrieval stream's work - which was true, and meant <b>the vector
    /// arm had never run once</b> despite the corpus being fully embedded and HNSW-indexed. The
    /// expensive half was paid for and the cheap half was missing.
    /// </para>
    /// <para>
    /// <see cref="IncidentEmbedder.EmbedAsync"/> returns null on any failure and caches by content
    /// hash, so a provider outage degrades this to the lexical and trigram arms rather than
    /// failing the page, and a repeated query costs nothing. That degradation is designed for:
    /// search returning worse results is useful, search that throws during an outage is not.
    /// </para>
    /// </remarks>
    public async Task<IncidentSearchResult> SearchAsync(
        string query,
        SearchFilter filter,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return IncidentSearchResult.Empty;
        }

        await using var scope = scopes.CreateAsyncScope();
        var search = scope.ServiceProvider.GetRequiredService<IncidentSearch>();
        var embedder = scope.ServiceProvider.GetRequiredService<IncidentEmbedder>();

        var queryEmbedding = await embedder.EmbedAsync(query, hash: null, ct);

        return await search.SearchAsync(query, queryEmbedding, filter, Math.Clamp(limit, 1, 100), ct);
    }

    /// <summary>
    /// The raw tool result behind a step. Null when the blob has expired.
    /// </summary>
    /// <param name="maxBytes">
    /// Clip length. The UI passes a small value because the alternative is streaming a
    /// megabyte of log through a SignalR circuit and asking the browser to lay it out as
    /// DOM, which freezes the tab on the page someone opened during an outage. The API
    /// endpoint passes the full length, because a script asking for the raw evidence wants
    /// the raw evidence.
    /// </param>
    public async Task<EvidenceBlobView?> GetBlobAsync(Guid id, int maxBytes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

        var blob = await db.EvidenceBlobs
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (blob is null)
        {
            return null;
        }

        var clipped = blob.Content.Length > maxBytes;

        return new EvidenceBlobView
        {
            Id = blob.Id,
            ContentType = blob.ContentType,
            Content = clipped ? blob.Content[..maxBytes] : blob.Content,
            TotalBytes = blob.Content.Length,
            Clipped = clipped,
            CreatedAt = blob.CreatedAt,
            ExpiresAt = blob.ExpiresAt,
        };
    }

    public async Task<AgentStatusView> GetStatusAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<HephaistoDbContext>();
        var modes = sp.GetRequiredService<IAgentModeStore>();
        var budget = sp.GetRequiredService<LlmBudgetService>();

        var mode = await modes.GetAsync(ct);
        var resolved = await killSwitch.ResolveAsync(ct);

        // Both counts hit ix_incidents_state_open, the partial index that keeps this
        // proportional to the live set rather than to all history.
        var open = await db.Incidents
            .AsNoTracking()
            .CountAsync(i => HephaistoDbContext.OpenStates.Contains(i.State), ct);

        var escalated = await db.Incidents
            .AsNoTracking()
            .CountAsync(i => i.State == IncidentState.Escalated, ct);

        var o = budgetOptions.CurrentValue;

        return new AgentStatusView
        {
            RunningInvestigations = [.. tracker.Running.Select(r => new InvestigationProgressView
            {
                Model = r.Model,
                StartedAt = r.StartedAt,
                Steps = r.Steps,
                ToolCalls = r.ToolCalls,
                CostUsd = r.CostUsd,
                Activity = r.Activity,
            })],
            QueuedInvestigations = queue.Depth,

            // The CONFIGURED ceiling - the Helm value, as it arrives on the env and ConfigMap
            // arms - and deliberately not the resolved mode, because the gap between the two
            // is the interesting part. "Configured Auto, running Observe" is the state an
            // operator most needs to see during an incident, and it is exactly what a single
            // mode field hides.
            //
            // Not the agent_mode row's mode column: nothing writes it, no arm reads it, and
            // it is seeded to Observe - so showing it would have told an operator the agent
            // was configured Observe while the chart said Auto.
            Mode = killSwitch.External.Effective,
            EffectiveMode = resolved.Effective,
            ModeDecidedBy = resolved.DecidedBy,
            ModeArms = [.. resolved.Arms.Select(a => a.Describe())],
            ModeConstrained = resolved.IsConstrained,
            RunawayLatched = mode.RunawayLatched,
            LatchReason = mode.LatchReason,
            LatchedAt = mode.LatchedAt,
            ModeChangedBy = mode.ChangedBy,
            ModeChangedAt = mode.ChangedAt,
            OpenIncidents = open,
            EscalatedIncidents = escalated,
            HourlyTokenUtilization = await budget.GetUtilizationAsync(LlmBudgetService.WindowHourTokens, ct),
            HourlyCostUtilization = await budget.GetUtilizationAsync(LlmBudgetService.WindowHourCost, ct),
            DailyCostUtilization = await budget.GetUtilizationAsync(LlmBudgetService.WindowDayCost, ct),
            WarnAtUtilization = o.WarnAtUtilization,
            WatchdogLastSeenAt = watchdog.LastSeenAt,
            WatchdogStale = watchdog.IsStale,
            WatchdogReceipts = watchdog.ReceiptCount,
            Now = clock.UtcNow,
            Version = BuildInfo.Version,
            Commit = BuildInfo.ShortCommit,
        };
    }

    /// <summary>
    /// Records a human's verdict. Returns null when the incident does not exist.
    /// </summary>
    /// <remarks>
    /// The feedback row and its audit event are staged into one <c>SaveChangesAsync</c>. The
    /// audit trail is where "who said this incident was a false positive" is answerable a
    /// year later, and a feedback row without one is a claim with no provenance.
    /// </remarks>
    public async Task<FeedbackView?> AddFeedbackAsync(
        Guid incidentId,
        bool helpful,
        bool? rootCauseCorrect,
        bool falsePositive,
        string? comment,
        string submittedBy,
        CancellationToken ct)
    {
        var actor = submittedBy?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<HephaistoDbContext>();
        var audit = sp.GetRequiredService<IAuditRepository>();

        // The kind comes back with the existence check rather than in a second round trip:
        // hephaisto.human.feedback carries it as a label, because "which failure mode do we
        // get wrong" is the question the false-positive rate is actually asked.
        var kind = await db.Incidents
            .Where(i => i.Id == incidentId)
            .Select(i => (SignalKind?)i.Kind)
            .FirstOrDefaultAsync(ct);

        if (kind is null)
        {
            return null;
        }

        var now = clock.UtcNow;

        var feedback = new HumanFeedback
        {
            IncidentId = incidentId,
            Helpful = helpful,
            RootCauseCorrect = rootCauseCorrect,
            FalsePositive = falsePositive,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            SubmittedBy = actor,
            At = now,
        };

        db.HumanFeedback.Add(feedback);

        audit.Enlist(new AuditEvent
        {
            At = now,
            Type = "feedback.submitted",
            IncidentId = incidentId,
            Actor = actor,
            Summary = $"{(helpful ? "helpful" : "not helpful")}"
                + (falsePositive ? ", false positive" : string.Empty)
                + (rootCauseCorrect is { } rc ? $", root cause {(rc ? "correct" : "wrong")}" : string.Empty),
            Detail = JsonSerializer.Serialize(
                new { helpful, rootCauseCorrect, falsePositive, comment = feedback.Comment },
                AuditJson),
        });

        await db.SaveChangesAsync(ct);

        // After the save, not before: a counter incremented for a row that then failed to
        // commit would overstate the only externally-supplied quality signal the agent has.
        sp.GetRequiredService<HephaistoMetrics>().HumanFeedback(feedback, kind.Value);

        notifier.Publish(new IncidentLiveEvent
        {
            IncidentId = incidentId,
            Kind = IncidentLiveEventKind.FeedbackSubmitted,
            Detail = $"feedback from {actor}",
            At = now,
        });

        return MapFeedback(feedback);
    }

    /// <summary>
    /// Puts an incident that was never successfully diagnosed back on the investigation queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recovery path for the outcomes that produce no answer: a provider overload, a step
    /// budget that ran out mid-thought, a stall. Before this existed those incidents were
    /// terminal - the console showed "escalated, investigation failed" and the only way to get
    /// another attempt was to make the alert fire again.
    /// </para>
    /// <para>
    /// <b>Every rejection is a distinct outcome, not a bool.</b> "Already running", "the kill
    /// switch is off" and "the queue is saturated" want three different things from the person
    /// who clicked, and collapsing them into false produces a button that does nothing and
    /// says nothing.
    /// </para>
    /// </remarks>
    public async Task<ReinvestigateResult> RequestReinvestigationAsync(
        Guid incidentId,
        string requestedBy,
        CancellationToken ct)
    {
        var actor = requestedBy?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // AgentMode.Off is documented as "ingest nothing, investigate nothing. Full stop." A
        // hand-started retry is the one investigation a human can begin directly, so it is
        // also the one that would most visibly break that promise by ignoring the switch.
        var mode = await killSwitch.ResolveAsync(ct);

        if (mode.Effective == AgentMode.Off)
        {
            return new ReinvestigateResult
            {
                Outcome = ReinvestigateOutcome.Disabled,
                Detail = mode.Explain(),
            };
        }

        // Cheap and racy, and deliberately so. The authoritative guard is the state machine
        // below: an incident already Investigating is not a legal predecessor, so a retry that
        // slips past this check still cannot produce a second concurrent run. This exists to
        // give the common case a message that names the problem.
        if (tracker.IsRunning(incidentId))
        {
            return new ReinvestigateResult
            {
                Outcome = ReinvestigateOutcome.AlreadyRunning,
                Detail = "An investigation is already running for this incident.",
            };
        }

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<HephaistoDbContext>();
        var audit = sp.GetRequiredService<IAuditRepository>();
        var stateMachine = sp.GetRequiredService<IncidentStateMachine>();

        var incident = await db.Incidents
            .Include(i => i.Events)
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);

        if (incident is null)
        {
            return new ReinvestigateResult { Outcome = ReinvestigateOutcome.NotFound };
        }

        var from = incident.State;
        var eventsBefore = incident.Events.Count;

        try
        {
            stateMachine.Reinvestigate(incident, "Re-investigation requested", actor);
        }
        catch (InvalidStateTransitionException ex)
        {
            return new ReinvestigateResult
            {
                Outcome = ReinvestigateOutcome.IllegalState,
                Detail = ex.Message,
            };
        }

        // ORDER IS LOAD-BEARING, for the same reason it is in InvestigationCoordinator. The
        // event the state machine just appended carries a client-assigned Guid.CreateVersion7
        // key, so EF concludes the row already exists and emits an UPDATE matching nothing.
        // This has to run before any save touches the graph.
        db.TrackNewIncidentChildren(incident, eventsBefore);

        audit.Enlist(new AuditEvent
        {
            At = clock.UtcNow,
            Type = "investigation.requeued",
            IncidentId = incidentId,
            Actor = actor,
            Summary = $"re-investigation requested from {from}",
            Detail = JsonSerializer.Serialize(new { from = from.ToString(), mode = mode.Effective.ToString() }, AuditJson),
        });

        await db.SaveChangesAsync(ct);

        // Enqueued only after the commit. The worker resolves the incident by id from its own
        // scope, so handing it one whose Investigating state has not landed yet is a race it
        // would lose by reading the old state and doing nothing.
        if (!queue.TryEnqueue(incidentId))
        {
            // The state is already committed, so this incident is now Investigating with
            // nothing working it. That is precisely the situation StrandedIncidentRequeue
            // sweeps up on the next restart, so it is recoverable rather than lost - but say
            // so plainly instead of reporting success.
            return new ReinvestigateResult
            {
                Outcome = ReinvestigateOutcome.QueueFull,
                Detail = "The investigation queue is saturated. The incident is marked "
                    + "Investigating and will be picked up by the stranded-incident sweep.",
            };
        }

        notifier.Publish(new IncidentLiveEvent
        {
            IncidentId = incidentId,
            Kind = IncidentLiveEventKind.StateChanged,
            State = IncidentState.Investigating,
            Detail = $"re-investigation requested by {actor}",
            At = clock.UtcNow,
        });

        return new ReinvestigateResult { Outcome = ReinvestigateOutcome.Queued };
    }

    /// <summary>
    /// Clears the runaway latch, restoring whatever mode the deployment already grants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only write in the product that touches the kill switch, and it is
    /// deliberately the weakest one possible: it cannot name a mode, and it cannot raise the
    /// agent above the ceiling the Helm values set. Clearing the latch is an acknowledgement -
    /// "I have looked at why this tripped" - not a configuration change, which is why it can
    /// live behind a button while setting the mode cannot.
    /// </para>
    /// <para>
    /// It writes <c>mode.changed</c>. That audit type has been named in
    /// <c>Core/Domain/Audit.cs</c> as something the trail records since before anything wrote
    /// one; this is the first thing that does. An unaudited re-arm would be the single most
    /// important unattributed event in the system - the moment autonomy came back.
    /// </para>
    /// </remarks>
    public async Task<ReArmResult> ReArmAsync(string requestedBy, CancellationToken ct)
    {
        var actor = requestedBy?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var modes = sp.GetRequiredService<IAgentModeStore>();
        var audit = sp.GetRequiredService<IAuditRepository>();

        var row = await modes.GetRowOrDefaultAsync(ct);

        if (row is null or { RunawayLatched: false })
        {
            // Not an error, and not silently a success either. A button that reports "done"
            // when it did nothing teaches an operator that pressing it is meaningless.
            return new ReArmResult
            {
                Outcome = ReArmOutcome.NotLatched,
                Detail = "The runaway latch is not set; there is nothing to clear.",
            };
        }

        var reason = row.LatchReason;
        var latchedAt = row.LatchedAt;

        // Enlisted BEFORE the re-arm, because ReArmAsync is what saves. Both land in one
        // commit on the scope's shared DbContext, so there is no window in which the latch is
        // cleared and no row says who cleared it.
        audit.Enlist(new AuditEvent
        {
            At = clock.UtcNow,
            Type = "mode.changed",
            Actor = actor,
            Summary = "runaway latch cleared",
            Detail = JsonSerializer.Serialize(
                new { latchReason = reason, latchedAt, clearedBy = actor },
                AuditJson),
        });

        await modes.ReArmAsync(actor, ct);

        var resolved = await killSwitch.ResolveAsync(ct);

        logger.LogWarning(
            "Runaway latch cleared by {Actor}; the agent is now {Mode}. It was latched for: {Reason}",
            actor, resolved.Effective, reason ?? "unknown reason");

        return new ReArmResult
        {
            Outcome = ReArmOutcome.ReArmed,
            Detail = $"Latch cleared. The agent is now {resolved.Effective}, bound by {resolved.DecidedBy}.",
            EffectiveMode = resolved.Effective,
        };
    }

    // ------------------------------------------------------------------
    // Mapping
    // ------------------------------------------------------------------

    private static SignalView MapSignal(Signal s) => new()
    {
        Id = s.Id,
        Source = s.Source,
        Kind = s.Kind,
        Severity = s.Severity,
        Reason = s.Reason,
        Message = s.Message,
        FirstSeen = s.FirstSeen,
        LastSeen = s.LastSeen,
        Count = s.Count,
        Labels = s.Labels,
        Target = TargetView.From(s.Target),
    };

    private static TransitionView MapTransition(IncidentEvent e) => new()
    {
        Id = e.Id,
        From = e.From,
        To = e.To,
        Reason = e.Reason,
        At = e.At,
        TraceId = e.TraceId,
    };

    private static InvestigationView MapInvestigation(Investigation v)
    {
        var steps = v.Steps.OrderBy(s => s.Ordinal).ToList();

        // Built once per investigation so evidence can name the step it cites by ordinal.
        // Resolving it per evidence item would be quadratic on the one page that renders a
        // hundred steps and a hundred citations.
        var ordinalByStepId = steps.ToDictionary(s => s.Id, s => s.Ordinal);

        return new InvestigationView
        {
            Id = v.Id,
            TraceId = v.TraceId,
            ModelId = v.ModelId,
            StartedAt = v.StartedAt,
            CompletedAt = v.CompletedAt,
            TerminationReason = v.TerminationReason,
            StepsUsed = v.StepsUsed,
            ToolCallsUsed = v.ToolCallsUsed,
            InputTokens = v.InputTokens,
            OutputTokens = v.OutputTokens,
            CostUsd = v.CostUsd,
            Confidence = v.Confidence,
            Error = v.Error,
            Steps = [.. steps.Select(MapStep)],
            Findings =
            [
                .. v.Findings
                    .OrderByDescending(f => f.IsPrimary)
                    .ThenByDescending(f => f.Confidence)
                    .Select(f => MapFinding(f, ordinalByStepId)),
            ],
            Plan = v.Plan is { } plan ? MapPlan(plan) : null,
        };
    }

    private static StepView MapStep(InvestigationStep s) => new()
    {
        Id = s.Id,
        Ordinal = s.Ordinal,
        Kind = s.Kind,
        ToolName = s.ToolName,
        ToolServer = s.ToolServer,
        Arguments = s.Arguments,
        ResultDigest = s.ResultDigest,
        RawBlobId = s.RawBlobId,
        ResultTruncated = s.ResultTruncated,
        ResultBytes = s.ResultBytes,
        DurationMs = s.DurationMs,
        InputTokens = s.InputTokens,
        OutputTokens = s.OutputTokens,
        CostUsd = s.CostUsd,
        Failed = s.Failed,
        Error = s.Error,
        At = s.At,
    };

    private static FindingView MapFinding(Finding f, IReadOnlyDictionary<Guid, int> ordinalByStepId) => new()
    {
        Id = f.Id,
        Category = f.Category,
        Hypothesis = f.Hypothesis,
        Confidence = f.Confidence,
        IsPrimary = f.IsPrimary,
        Evidence =
        [
            .. f.Evidence.Select(e => new EvidenceView
            {
                Id = e.Id,
                StepId = e.StepId,
                StepOrdinal = ordinalByStepId.TryGetValue(e.StepId, out var ordinal) ? ordinal : null,
                Excerpt = e.Excerpt,
                SourceUri = e.SourceUri,
            }),
        ],
    };

    private static PlanView MapPlan(ActionPlan p) => new()
    {
        Id = p.Id,
        Summary = p.Summary,
        NoActionRequired = p.NoActionRequired,
        CreatedAt = p.CreatedAt,
        Actions = [.. p.Actions.OrderBy(a => a.Id).Select(MapAction)],
    };

    private static ActionView MapAction(AgentAction a) => new()
    {
        Id = a.Id,
        Type = a.Type,
        Target = TargetView.From(a.Target),
        Arguments = a.Arguments,
        Risk = a.Risk,
        State = a.State,
        PredictedEffect = a.PredictedEffect,
        RollbackSpec = a.RollbackSpec,
        Decision = a.Decision,
        DecisionReasons = a.DecisionReasons,
        DryRun = a.DryRun,
        ModeAtExecution = a.ModeAtExecution,
        ApprovedBy = a.ApprovedBy,
        ApprovalSource = a.ApprovalSource,
        ExecutedAt = a.ExecutedAt,
        Outcome = a.Outcome,
        Error = a.Error,
    };

    private static FeedbackView MapFeedback(HumanFeedback f) => new()
    {
        Id = f.Id,
        Helpful = f.Helpful,
        RootCauseCorrect = f.RootCauseCorrect,
        FalsePositive = f.FalsePositive,
        Comment = f.Comment,
        SubmittedBy = f.SubmittedBy,
        At = f.At,
    };
}

/// <summary>Why a re-investigation request did or did not take.</summary>
public enum ReinvestigateOutcome
{
    Queued = 0,

    NotFound = 1,

    /// <summary>An investigation is already in flight; a second would double-spend.</summary>
    AlreadyRunning = 2,

    /// <summary>Not a state a retry can start from - Suppressed, Resolved, or mid-flow.</summary>
    IllegalState = 3,

    /// <summary>The incident is Investigating but nothing is working it. Recoverable.</summary>
    QueueFull = 4,

    /// <summary>The kill switch is Off.</summary>
    Disabled = 5,
}

/// <summary>Whether clearing the runaway latch did anything.</summary>
public enum ReArmOutcome
{
    ReArmed = 0,

    /// <summary>Nothing was latched. Reported rather than treated as success.</summary>
    NotLatched = 1,
}

public sealed record ReArmResult
{
    public required ReArmOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public AgentMode? EffectiveMode { get; init; }

    public bool Accepted => Outcome == ReArmOutcome.ReArmed;
}

public sealed record ReinvestigateResult
{
    public required ReinvestigateOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public bool Accepted => Outcome == ReinvestigateOutcome.Queued;
}
