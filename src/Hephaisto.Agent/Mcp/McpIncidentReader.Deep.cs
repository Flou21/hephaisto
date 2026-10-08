using System.Globalization;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Mcp.Answers;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace Hephaisto.Agent.Mcp;

// The reads that go deeper than an overview: the history of an alert, the finding and what it
// rests on, every step of the investigation, a raw blob, the actions, the note people keep, and
// code fixes. Same rules as McpIncidentReader.cs.
public sealed partial class McpIncidentReader
{
    private const int DigestChars = 3_000;
    private const int BlobChars = 20_000;

    // -------------------------------------------------------------------------------------
    // History
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// How often an alert name - or a workload - opened an incident in the window, how each
    /// ended, what was done. An incident id stands for its alert name, or its workload when it
    /// has none.
    /// </summary>
    public async Task<IncidentHistory> HistoryAsync(
        string? alertName,
        string? workload,
        Guid? incidentId,
        int days,
        string? bucket,
        int limit,
        CancellationToken ct)
    {
        if (incidentId is { } id)
        {
            var from = await db.Incidents.AsNoTracking()
                .Where(i => i.Id == id)
                .Select(i => new { i.AlertName, Workload = i.Target.OwnerName ?? i.Target.Name })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? throw new McpException($"No incident has the id {id}.");

            alertName ??= from.AlertName;
            workload = alertName is null ? from.Workload : workload;
        }

        if (string.IsNullOrWhiteSpace(alertName) && string.IsNullOrWhiteSpace(workload))
        {
            throw new McpException("Name an alertName, a workload or an incidentId to read the history of.");
        }

        var weekly = (bucket ?? "day").Trim().ToLowerInvariant() switch
        {
            "day" => false,
            "week" => true,
            _ => throw new McpException($"bucket '{McpQuery.Echo(bucket)}' is not day or week."),
        };

        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var query = db.Incidents.AsNoTracking().Where(i => i.OpenedAt >= since && i.State != IncidentState.Suppressed);

        string subject;
        string kind;

        if (!string.IsNullOrWhiteSpace(alertName))
        {
            var name = alertName.Trim();
            query = query.Where(i => i.AlertName == name);
            subject = name;
            kind = "alertName";
        }
        else
        {
            var w = workload!.Trim();
            query = query.Where(i => i.Target.Name == w || i.Target.OwnerName == w);
            subject = w;
            kind = "workload";
        }

        var rows = await query
            .OrderByDescending(i => i.OpenedAt)
            .Take(CountWindow)
            .Select(i => new
            {
                i.Id,
                i.State,
                i.Target.Namespace,
                Workload = i.Target.OwnerName ?? i.Target.Name,
                i.OpenedAt,
                i.ResolvedAt,
                i.ClosedAt,
                i.ClosedBy,
                i.Resolution,
                Actions = i.Actions.Count,
                CodeFixes = i.CodeFixAttempts.Count,
                RootCause = i.Investigations
                    .OrderByDescending(v => v.StartedAt)
                    .SelectMany(v => v.Findings.Where(f => f.IsPrimary))
                    .Select(f => f.Hypothesis)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var noteEntries = kind == "alertName"
            ? await db.AlertNoteEntries.AsNoTracking().CountAsync(e => e.AlertName == subject, ct).ConfigureAwait(false)
            : 0;

        string BucketOf(DateTimeOffset at) => weekly
            ? $"{ISOWeek.GetYear(at.UtcDateTime)}-W{ISOWeek.GetWeekOfYear(at.UtcDateTime):00}"
            : at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var durations = rows
            .Where(r => (r.ClosedAt ?? r.ResolvedAt) is not null)
            .Select(r => ((r.ClosedAt ?? r.ResolvedAt)!.Value - r.OpenedAt).TotalMinutes)
            .Order()
            .ToList();

        return new IncidentHistory
        {
            Subject = McpText.Name(subject),
            SubjectKind = kind,
            WindowDays = days,
            Total = rows.Count,
            Open = rows.Count(r => HephaistoDbContext.OpenStates.Contains(r.State)),
            Outcomes = rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count()),
            Endings = new HistoryEndings
            {
                Open = rows.Count(r => HephaistoDbContext.OpenStates.Contains(r.State)),
                ClosedByPerson = rows.Count(r => r.State == IncidentState.Closed && !IncidentStateMachine.ClosedByItsSource(r.ClosedBy)),
                EndedByAlert = rows.Count(r => r.State == IncidentState.Closed && IncidentStateMachine.ClosedByItsSource(r.ClosedBy)),
                Resolved = rows.Count(r => r.State == IncidentState.Resolved),
                Expired = rows.Count(r => r.State == IncidentState.Expired),
            },
            Buckets = [.. rows.GroupBy(r => BucketOf(r.OpenedAt)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new HistoryBucket { Start = g.Key, Count = g.Count() })],
            MedianMinutes = durations.Count == 0 ? null : Math.Round(durations[durations.Count / 2], 1),
            NoteEntries = noteEntries,
            Incidents = [.. rows.Take(limit).Select(r => new HistoryIncident
            {
                Id = r.Id,
                State = r.State,
                Namespace = McpText.Name(r.Namespace),
                Workload = McpText.Name(r.Workload),
                OpenedAt = r.OpenedAt,
                EndedAt = r.ClosedAt ?? r.ResolvedAt,
                DurationMinutes = (r.ClosedAt ?? r.ResolvedAt) is { } end ? Math.Round((end - r.OpenedAt).TotalMinutes, 1) : null,
                ClosedBy = McpText.NameOrNull(r.ClosedBy),
                Resolution = McpText.UntrustedOrNull(r.Resolution, 500),
                RootCause = McpText.UntrustedOrNull(r.RootCause, 500),
                Actions = r.Actions,
                CodeFixes = r.CodeFixes,
            })],
            Note = (rows.Count >= CountWindow ? $"Only the newest {CountWindow} were read. " : string.Empty)
                + (rows.Count > limit ? $"Showing the newest {limit}; the counts cover all {rows.Count}." : string.Empty) is { Length: > 0 } note
                ? note.Trim()
                : null,
        };
    }

    // -------------------------------------------------------------------------------------
    // The investigation
    // -------------------------------------------------------------------------------------

    public async Task<FindingsView> FindingsAsync(Guid incidentId, Guid? investigationId, CancellationToken ct)
    {
        var investigation = investigationId ?? await db.Investigations.AsNoTracking()
            .Where(v => v.IncidentId == incidentId && v.Findings.Any())
            .OrderByDescending(v => v.StartedAt)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (investigation is not { } vid)
        {
            return new FindingsView
            {
                IncidentId = incidentId,
                Findings = [],
                Note = "No investigation of this incident produced a finding that survived grounding: every finding "
                    + "must cite something a tool returned, and one that cited nothing was discarded. "
                    + "get_investigation shows what was tried.",
            };
        }

        var v = await db.Investigations.AsNoTracking()
            .Where(x => x.Id == vid && x.IncidentId == incidentId)
            .Select(x => new { x.Id, x.Confidence, Summary = x.Plan != null ? x.Plan.Summary : null })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new McpException($"Investigation {vid} does not belong to incident {incidentId}.");

        var findings = await db.Findings.AsNoTracking()
            .Where(f => f.InvestigationId == vid)
            .Include(f => f.Evidence)
            .OrderByDescending(f => f.IsPrimary)
            .ThenByDescending(f => f.Confidence)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stepIds = findings.SelectMany(f => f.Evidence).Select(e => e.StepId).Distinct().ToList();

        var steps = await db.InvestigationSteps.AsNoTracking()
            .Where(s => stepIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ordinal, s.ToolName, s.RawBlobId })
            .ToDictionaryAsync(s => s.Id, ct)
            .ConfigureAwait(false);

        var liveBlobs = await LiveBlobsAsync([.. steps.Values.Select(s => s.RawBlobId).OfType<Guid>()], ct).ConfigureAwait(false);

        return new FindingsView
        {
            IncidentId = incidentId,
            InvestigationId = v.Id,
            Summary = McpText.UntrustedOrNull(v.Summary, 1_500),
            Confidence = v.Confidence,
            Findings = [.. findings.Select(f => new FindingDetail
            {
                Id = f.Id,
                Primary = f.IsPrimary,
                Category = McpText.Name(f.Category),
                Hypothesis = McpText.Untrusted(f.Hypothesis, 2_000),
                Confidence = f.Confidence,
                Evidence = [.. f.Evidence.Select(e =>
                {
                    var step = steps.GetValueOrDefault(e.StepId);
                    return new EvidenceDetail
                    {
                        StepId = e.StepId,
                        StepOrdinal = step?.Ordinal,
                        Tool = McpText.NameOrNull(step?.ToolName),
                        Excerpt = McpText.Untrusted(e.Excerpt, 1_500),
                        BlobId = step?.RawBlobId is { } b && liveBlobs.Contains(b) ? b : null,
                    };
                })],
            })],
        };
    }

    public async Task<InvestigationPage> InvestigationAsync(Guid incidentId, Guid? investigationId, int afterStep, int limit, CancellationToken ct)
    {
        var all = await db.Investigations.AsNoTracking()
            .Where(v => v.IncidentId == incidentId)
            .OrderByDescending(v => v.StartedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var v = (investigationId is { } wanted ? all.FirstOrDefault(x => x.Id == wanted) : all.FirstOrDefault())
            ?? throw new McpException(investigationId is null
                ? $"Incident {incidentId} has not been investigated."
                : $"Investigation {investigationId} does not belong to incident {incidentId}.");

        var steps = await db.InvestigationSteps.AsNoTracking()
            .Where(s => s.InvestigationId == v.Id && s.Ordinal > afterStep)
            .OrderBy(s => s.Ordinal)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var page = steps.Take(limit).ToList();
        var liveBlobs = await LiveBlobsAsync([.. page.Select(s => s.RawBlobId).OfType<Guid>()], ct).ConfigureAwait(false);

        return new InvestigationPage
        {
            Investigation = new InvestigationDetail
            {
                Id = v.Id,
                IncidentId = incidentId,
                Model = McpText.Name(v.ModelId),
                Executor = McpText.Name(v.Executor ?? Core.Investigations.InvestigationExecutors.InProcess),
                StartedAt = v.StartedAt,
                CompletedAt = v.CompletedAt,
                Termination = v.TerminationReason,
                Steps = v.StepsUsed,
                ToolCalls = v.ToolCallsUsed,
                InputTokens = v.InputTokens,
                OutputTokens = v.OutputTokens,
                CostUsd = v.CostUsd,
                Confidence = v.Confidence,
                Error = McpText.UntrustedOrNull(v.Error, 1_000),
                Investigations = all.Count,
            },
            Steps = [.. page.Select(s => new StepDetail
            {
                Id = s.Id,
                Ordinal = s.Ordinal,
                Kind = s.Kind,
                Tool = McpText.NameOrNull(s.ToolName),
                Server = McpText.NameOrNull(s.ToolServer),
                Arguments = McpText.UntrustedOrNull(s.Arguments, 1_000),
                Digest = McpText.UntrustedOrNull(s.ResultDigest, DigestChars),
                BlobId = s.RawBlobId is { } b && liveBlobs.Contains(b) ? b : null,
                Truncated = s.ResultTruncated,
                ResultBytes = s.ResultBytes,
                DurationMs = s.DurationMs,
                Failed = s.Failed,
                Error = McpText.UntrustedOrNull(s.Error, 500),
                At = s.At,
            })],
            NextAfterStep = steps.Count > limit ? page[^1].Ordinal : null,
        };
    }

    /// <summary>
    /// The raw result behind a step, a window of it at a time; with a filter, only the lines that
    /// contain it.
    /// </summary>
    public async Task<EvidenceBlobPage> BlobAsync(Guid blobId, int offset, int length, string? filter, CancellationToken ct)
    {
        var blob = await db.EvidenceBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blobId, ct).ConfigureAwait(false)
            ?? throw new McpException(
                $"No evidence blob {blobId}. Blobs are kept for 30 days; the step's digest (get_investigation) outlives it.");

        var content = blob.Content;
        int? matching = null;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var lines = content.Split('\n').Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            matching = lines.Count;
            content = string.Join('\n', lines);
        }

        offset = Math.Clamp(offset, 0, content.Length);
        var take = Math.Min(Math.Clamp(length, 1, BlobChars), content.Length - offset);

        return new EvidenceBlobPage
        {
            BlobId = blob.Id,
            ContentType = McpText.Name(blob.ContentType).Value == blob.ContentType ? blob.ContentType : "text/plain",
            TotalChars = content.Length,
            Offset = offset,
            NextOffset = offset + take < content.Length ? offset + take : null,
            MatchingLines = matching,
            Text = McpText.Untrusted(content.Substring(offset, take), take + 1),
            ExpiresAt = blob.ExpiresAt,
        };
    }

    public async Task<ActionsView> ActionsAsync(Guid incidentId, CancellationToken ct)
    {
        var actions = await db.AgentActions.AsNoTracking()
            .Where(a => a.IncidentId == incidentId)
            .Include(a => a.Verifications)
            .OrderBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ActionsView
        {
            IncidentId = incidentId,
            Actions = [.. actions.Select(a => new ActionDetail
            {
                Id = a.Id,
                Type = a.Type,
                Target = McpText.Name($"{a.Target.Namespace}/{a.Target.Kind}/{a.Target.Name}"),
                Arguments = McpText.UntrustedOrNull(a.Arguments, 1_000),
                Risk = a.Risk,
                State = a.State,
                Decision = a.Decision,
                DecisionReasons = [.. a.DecisionReasons.Select(r => McpText.Untrusted(r, 500))],
                PredictedEffect = McpText.UntrustedOrNull(a.PredictedEffect, 1_000),
                DryRun = a.DryRun,
                ApprovedBy = McpText.NameOrNull(a.ApprovedBy),
                ApprovedAt = a.ApprovedAt,
                ApprovalSource = a.ApprovedBy is null ? null : a.ApprovalSource,
                ApprovalReason = McpText.UntrustedOrNull(a.ApprovalReason, 500),
                ExecutedAt = a.ExecutedAt,
                Outcome = McpText.UntrustedOrNull(a.Outcome, 1_000),
                Error = McpText.UntrustedOrNull(a.Error, 1_000),
                Verifications = [.. a.Verifications.OrderBy(x => x.Attempt).Select(x => new VerificationDetail
                {
                    Attempt = x.Attempt,
                    DueAt = x.DueAt,
                    RanAt = x.RanAt,
                    Outcome = x.Outcome,
                    Checks = McpText.UntrustedOrNull(x.Checks, 1_000),
                    Detail = McpText.UntrustedOrNull(x.Detail, 1_000),
                })],
            })],
            Note = "Read only. An action is approved or denied by a person, in the console or in Teams, never here.",
        };
    }

    // -------------------------------------------------------------------------------------
    // What people keep, and code fixes
    // -------------------------------------------------------------------------------------

    public async Task<NoteDetail> AlertNoteAsync(string alertName, int limit, string? cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alertName))
        {
            throw new McpException("Name the alert: the alertname exactly, as lookup_incident_filters lists it.");
        }

        var name = alertName.Trim();
        var key = $"note:{name}";
        var offset = McpQuery.ReadOffset(cursor, key);

        var note = await db.AlertNotes.AsNoTracking().FirstOrDefaultAsync(n => n.AlertName == name, ct).ConfigureAwait(false);
        var count = await db.AlertNoteEntries.AsNoTracking().CountAsync(e => e.AlertName == name, ct).ConfigureAwait(false);

        var entries = await db.AlertNoteEntries.AsNoTracking()
            .Where(e => e.AlertName == name)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new NoteDetail
        {
            AlertName = McpText.Name(name),
            Exists = note is not null,
            Body = McpText.UntrustedOrNull(note?.Body, 8_000),
            UpdatedBy = McpText.NameOrNull(note?.UpdatedBy),
            UpdatedAt = note?.UpdatedAt,
            EntryCount = count,
            Entries = [.. entries.Select(e => new NoteEntryDetail
            {
                Id = e.Id,
                At = e.CreatedAt,
                Author = McpText.Name(e.Author),
                Text = McpText.Untrusted(e.Text, 1_000),
                IncidentId = e.IncidentId,
            })],
            NextCursor = count > offset + limit ? McpQuery.Cursor(key, offset + limit) : null,
            Url = NotificationLinks.AlertNote(notifications.CurrentValue.BaseUrl, name),
        };
    }

    public async Task<CodeFixPage> CodeFixesAsync(
        string? state,
        string? repository,
        string? workload,
        Guid? incidentId,
        DateTimeOffset? since,
        int limit,
        string? cursor,
        CancellationToken ct)
    {
        // Both kinds of attempt (v0.14.0): an incident's, and one for a GitHub issue, which has no
        // incident. A row names whichever it is for - incidentId, or workItemId with the issue.
        var query = db.CodeFixAttempts.AsNoTracking().Include(a => a.WorkItem).AsQueryable();

        if (!string.IsNullOrWhiteSpace(state))
        {
            var states = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<CodeFixState>(s, true, out var p) && Enum.IsDefined(p)
                    ? p
                    : throw new McpException($"state '{McpQuery.Echo(s)}' is not one of: {string.Join(", ", Enum.GetNames<CodeFixState>())}."))
                .ToList();
            query = query.Where(a => states.Contains(a.State));
        }

        if (!string.IsNullOrWhiteSpace(repository))
        {
            var r = repository.Trim();
            query = query.Where(a => a.RepositoryUrl.Contains(r));
        }

        if (!string.IsNullOrWhiteSpace(workload))
        {
            var w = workload.Trim();
            query = query.Where(a => a.Workload == w);
        }

        if (incidentId is { } iid)
        {
            query = query.Where(a => a.IncidentId == iid);
        }

        if (since is { } s2)
        {
            query = query.Where(a => a.CreatedAt >= s2);
        }

        var key = $"codefixes|{state}|{repository}|{workload}|{incidentId}|{since?.UtcTicks}";

        if (McpQuery.ReadKeyset(cursor, key) is { } after)
        {
            query = query.Where(a => EF.Functions.LessThan(
                ValueTuple.Create(a.CreatedAt, a.Id),
                ValueTuple.Create(after.At, after.Id)));
        }

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var page = rows.Take(limit).ToList();

        return new CodeFixPage
        {
            CodeFixes = [.. page.Select(a => CodeFixRowOf(CodeFixQueries.View(a)))],
            NextCursor = rows.Count > limit ? McpQuery.Cursor(key, page[^1].CreatedAt, page[^1].Id) : null,
        };
    }

    public async Task<CodeFixDetail> CodeFixAsync(Guid attemptId, CancellationToken ct)
    {
        var detail = await codeFixes.AttemptAsync(attemptId, ct).ConfigureAwait(false)
            ?? throw new McpException($"No code fix attempt {attemptId}.");

        return CodeFixDetailOf(detail.Attempt, detail.WorkItem);
    }

    public async Task<IncidentCodeFixes> IncidentCodeFixesAsync(Guid incidentId, CancellationToken ct)
    {
        var view = await codeFixes.ForIncidentAsync(incidentId, ct).ConfigureAwait(false);

        string? why = null;

        if (view.Attempts.Count == 0)
        {
            why = view.LatestEvaluation is { } e
                ? $"Evaluated {e.At:yyyy-MM-dd HH:mm}Z and {(e.WouldHaveStarted ? "would have started, but the mode did not allow it" : "declined")}: "
                    + string.Join("; ", e.Reasons.DefaultIfEmpty("no reason recorded"))
                : view.Mode.Effective == CodeFixMode.Off
                    ? $"The code-fix stage is off: {view.Mode.Explanation}"
                    : "No code fix was evaluated for this incident: one is considered only after an investigation "
                        + "concludes with a grounded finding on a workload mapped to a repository.";
        }

        return new IncidentCodeFixes
        {
            IncidentId = incidentId,
            Attempts = [.. view.Attempts.Select(CodeFixRowOf)],
            Latest = view.Attempts.Count > 0 ? CodeFixDetailOf(view.Attempts[0]) : null,
            Why = McpText.UntrustedOrNull(why, 1_500),
            Mode = view.Mode.Effective,
        };
    }

    private static CodeFixRow CodeFixRowOf(CodeFixAttemptView a) => new()
    {
        Id = a.Id,

        // Exactly one of the two: an attempt is for an incident or for a work item.
        IncidentId = a.IncidentId,
        WorkItemId = a.WorkItemId,
        Issue = a.Issue,
        IssueUrl = McpText.NameOrNull(a.IssueUrl),
        State = a.State,
        Repository = McpText.Name(a.Repository),

        // Empty for a work item: an issue names a repository, not something that runs.
        Workload = McpText.NameOrNull(a.Workload),
        Branch = McpText.NameOrNull(a.Branch),
        Summary = McpText.UntrustedOrNull(a.Summary, 1_000),
        PullRequestUrl = McpText.NameOrNull(a.PrUrl),
        PullRequestNumber = a.PrNumber,
        CostUsd = a.TotalCostUsd,
        CreatedAt = a.CreatedAt,
        FinishedAt = a.FinishedAt,
        FailureReason = McpText.UntrustedOrNull(a.FailureReason, 1_000),
    };

    /// <summary>
    /// <paramref name="item"/> is the issue a work item's attempt is for. Its title is somebody
    /// else's words, the pull request's description a model's, and a rejection's reason whatever
    /// an approver typed into a comment: each goes out enveloped, like the plan's own text.
    /// </summary>
    private static CodeFixDetail CodeFixDetailOf(CodeFixAttemptView a, WorkItemView? item = null) => new()
    {
        Attempt = CodeFixRowOf(a),
        IssueTitle = McpText.UntrustedOrNull(item?.Title, 300),
        RootCause = McpText.UntrustedOrNull(a.RootCause, 1_500),
        Confidence = a.Confidence,
        VerificationLevel = McpText.NameOrNull(a.VerificationLevel),
        Files = [.. a.Files.Select(f => McpText.Name(f))],
        Steps = [.. a.Steps.Select(s => McpText.Untrusted(s, 800))],
        Notes = [.. a.Notes.Select(s => McpText.Untrusted(s, 800))],
        NotVerifiable = [.. a.NotVerifiable.Select(s => McpText.Untrusted(s, 800))],
        BuildPassed = a.BuildPassed,
        TestsPassed = a.TestsPassed,
        Deviations = [.. a.Deviations.Select(s => McpText.Untrusted(s, 800))],
        PullRequestBody = McpText.UntrustedOrNull(a.PrBody, 4_000),
        PlanCostUsd = a.PlanCostUsd,
        ImplementCostUsd = a.ImplementCostUsd,
        RequestedBy = McpText.NameOrNull(a.RequestedBy),
        DecidedBy = McpText.NameOrNull(a.ApprovedBy),
        DecidedThrough = a.DecidedThrough,
        PlanReadyAt = a.PlanReadyAt,
        DecidedAt = a.DecidedAt,
        Note = a.WorkItemId is null
            ? "Read only. A plan is approved or denied by a person in the console, never here."
            : "Read only. A plan is approved or denied by a person - in a comment on the issue, or in the console - never here.",
        Next = a.WorkItemId is { } workItemId
            ? [$"get_work_item {{\"id\":\"{workItemId}\"}} - the issue this attempt is for, and what became of it"]
            : a.IncidentId is { } incidentId
                ? [$"get_incident {{\"incidentId\":\"{incidentId}\"}} - the incident this attempt is for"]
                : [],
    };

    private async Task<HashSet<Guid>> LiveBlobsAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;

        return [.. await db.EvidenceBlobs.AsNoTracking()
            .Where(b => ids.Contains(b.Id) && b.ExpiresAt > now)
            .Select(b => b.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false)];
    }
}
