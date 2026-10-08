using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.CodeFix;

/// <summary>One attempt, as the console and the API show it.</summary>
public sealed record CodeFixAttemptView
{
    public required Guid Id { get; init; }

    /// <summary>The incident this attempt is for. Null when it is for a work item.</summary>
    public required Guid? IncidentId { get; init; }

    /// <summary>The incident's title; empty for a work item's attempt.</summary>
    public required string IncidentTitle { get; init; }

    /// <summary>The work item this attempt is for (v0.14.0). Null when it is for an incident.</summary>
    public Guid? WorkItemId { get; init; }

    /// <summary><c>owner/repo#12</c>, and the issue's page. Null for an incident's attempt.</summary>
    public string? Issue { get; init; }

    public string? IssueUrl { get; init; }
    public required string Workload { get; init; }
    public required string Repository { get; init; }
    public required string DefaultBranch { get; init; }
    public required string Branch { get; init; }
    public required CodeFixState State { get; init; }
    public required string RequestedBy { get; init; }
    public string? Summary { get; init; }
    public string? RootCause { get; init; }
    public double? Confidence { get; init; }
    public string? VerificationLevel { get; init; }
    public bool NeedsCait { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<string> Steps { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<string> NotVerifiable { get; init; } = [];
    public IReadOnlyList<CodeFixDenial> DeniedToolCalls { get; init; } = [];
    public string? AnalysedRef { get; init; }
    public string? ContextSha { get; init; }
    public string? PlanJobName { get; init; }
    public string? ImplementJobName { get; init; }
    public decimal PlanCostUsd { get; init; }
    public decimal ImplementCostUsd { get; init; }
    /// <summary>The comment on the issue that carries the plan, once it is written. Null for an incident's attempt.</summary>
    public long? PlanCommentId { get; init; }
    public string? PrUrl { get; init; }
    public int? PrNumber { get; init; }

    /// <summary>
    /// The pull request's description as the runner sent it to GitHub - text a model had a hand
    /// in, to be shown as text. Null without a pull request, and for one whose runner reported none.
    /// </summary>
    public string? PrBody { get; init; }
    public bool? BuildPassed { get; init; }
    public bool? TestsPassed { get; init; }
    public IReadOnlyList<string> Deviations { get; init; } = [];
    public string? ApprovedBy { get; init; }

    /// <summary>
    /// Through what the plan was answered - the console (<c>Ui</c>), the API (<c>Api</c>, or
    /// <c>Oidc</c> with a token), a comment on the issue (<c>GitHub</c>). Null while nobody has,
    /// and for a plan denied before v0.14.0, whose row did not keep it.
    /// </summary>
    public ApprovalSource? DecidedThrough { get; init; }
    public string? FailureReason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PlanStartedAt { get; init; }
    public DateTimeOffset? PlanReadyAt { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset? ImplementStartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>When the current phase started, for the elapsed-time line of a running attempt.</summary>
    public DateTimeOffset? RunningSince => State switch
    {
        CodeFixState.Planning => PlanStartedAt,
        CodeFixState.Implementing => ImplementStartedAt,
        _ => null,
    };

    public decimal TotalCostUsd => PlanCostUsd + ImplementCostUsd;
}

/// <summary>The latest judgement on an incident: the "would have / declined because" line.</summary>
public sealed record CodeFixEvaluationView(
    DateTimeOffset At,
    bool Eligible,
    bool WouldHaveStarted,
    IReadOnlyList<string> Codes,
    IReadOnlyList<string> Reasons,
    string? Mode,
    string? Repository,
    string Actor);

/// <summary>The code-fix mode as the console states it, with why approval is or is not possible.</summary>
public sealed record CodeFixModeView(CodeFixMode Effective, string Explanation, bool CanApprove, string? ApprovalBlockedBecause);

public sealed record CodeFixCounts(int Running, int AwaitingApproval, int PrsOpenedLastWeek);

public sealed record IncidentCodeFixView(
    IReadOnlyList<CodeFixAttemptView> Attempts,
    CodeFixEvaluationView? LatestEvaluation,
    CodeFixModeView Mode);

/// <summary>
/// One attempt with what it is for, as its own page and <c>GET /api/codefixes/{id}</c> show it.
/// </summary>
/// <param name="WorkItem">
/// The issue the attempt is for - its title, author and text are somebody else's words - or null
/// for an incident's attempt, whose <see cref="CodeFixAttemptView.IncidentId"/> names its page.
/// </param>
public sealed record CodeFixAttemptDetail(CodeFixAttemptView Attempt, WorkItemView? WorkItem, CodeFixModeView Mode);

/// <summary>Read side of the code-fix stage, for the console and <c>/api</c>.</summary>
public sealed class CodeFixQueries(
    HephaistoDbContext db,
    ICodeFixSwitch codeFixSwitch,
    IOptionsMonitor<CodeFixOptions> options,
    IOptionsMonitor<Options.AuthOptions> auth)
{
    public async Task<IReadOnlyList<CodeFixAttemptView>> ListAsync(CodeFixState? state, int limit, CancellationToken ct)
    {
        var query = db.CodeFixAttempts.AsNoTracking().Include(a => a.Incident).Include(a => a.WorkItem).AsQueryable();

        if (state is { } s)
            query = query.Where(a => a.State == s);

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Running and waiting first: the page answers "what needs me" before "what happened".
        return rows
            .OrderBy(a => a.State switch
            {
                CodeFixState.PlanReady => 0,
                CodeFixState.Planning or CodeFixState.Implementing or CodeFixState.Eligible => 1,
                _ => 2,
            })
            .ThenByDescending(a => a.CreatedAt)
            .Select(View)
            .ToList();
    }

    public async Task<CodeFixCounts> CountsAsync(CancellationToken ct)
    {
        var running = CodeFixStates.Running.ToArray();
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        return new CodeFixCounts(
            await db.CodeFixAttempts.CountAsync(a => running.Contains(a.State), ct).ConfigureAwait(false),
            await db.CodeFixAttempts.CountAsync(a => a.State == CodeFixState.PlanReady, ct).ConfigureAwait(false),
            await db.CodeFixAttempts.CountAsync(a => a.State == CodeFixState.PrOpened && a.FinishedAt >= weekAgo, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// One attempt, whichever kind of subject it has. Null when there is no such attempt.
    /// </summary>
    public async Task<CodeFixAttemptDetail?> AttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var attempt = await db.CodeFixAttempts.AsNoTracking()
            .Include(a => a.Incident)
            .Include(a => a.WorkItem)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
            .ConfigureAwait(false);

        return attempt is null
            ? null
            : new CodeFixAttemptDetail(
                View(attempt),
                attempt.WorkItem is { } item ? WorkItemQueries.View(item) : null,
                await ModeAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The attempts of one work item, newest first. One, until something plans an issue twice.</summary>
    public async Task<IReadOnlyList<CodeFixAttemptView>> ForWorkItemAsync(Guid workItemId, CancellationToken ct) =>
        (await db.CodeFixAttempts.AsNoTracking()
            .Include(a => a.WorkItem)
            .Where(a => a.WorkItemId == workItemId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false))
        .ConvertAll(View);

    public async Task<IncidentCodeFixView> ForIncidentAsync(Guid incidentId, CancellationToken ct)
    {
        var attempts = await db.CodeFixAttempts.AsNoTracking()
            .Include(a => a.Incident)
            .Where(a => a.IncidentId == incidentId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var evaluation = await db.AuditEvents.AsNoTracking()
            .Where(e => e.IncidentId == incidentId
                && (e.Type == CodeFixCoordinator.AuditEvaluated || e.Type == CodeFixCoordinator.AuditRequested))
            .OrderByDescending(e => e.At)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new IncidentCodeFixView(
            attempts.Select(View).ToList(),
            evaluation is null ? null : ParseEvaluation(evaluation),
            await ModeAsync(ct).ConfigureAwait(false));
    }

    public async Task<CodeFixModeView> ModeAsync(CancellationToken ct)
    {
        var mode = await codeFixSwitch.ResolveAsync(ct).ConfigureAwait(false);

        string? blocked = mode.Effective != CodeFixMode.Pr
            ? $"approval needs code-fix mode Pr; it is {mode.Effective} ({mode.DecidedBy})"
            : !auth.CurrentValue.IsConfigured && !options.CurrentValue.AllowUnauthenticatedApproval
                ? "approval needs an authenticated human, and Auth is not enabled"
                : null;

        return new CodeFixModeView(mode.Effective, mode.Explain(), blocked is null, blocked);
    }

    /// <summary>
    /// The plan an attempt stored, or null when it stored none - or one an older contract wrote,
    /// which the denormalised columns still describe.
    /// </summary>
    public static CodeFixPlanResult? Plan(CodeFixAttempt a)
    {
        ArgumentNullException.ThrowIfNull(a);

        try
        {
            return a.PlanResultJson is { } json ? JsonSerializer.Deserialize<CodeFixPlanResult>(json, CodeFixContract.Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CodeFixAttemptView View(CodeFixAttempt a)
    {
        CodeFixPlanResult? plan = null;
        CodeFixImplementResult? impl = null;

        try
        {
            if (a.PlanResultJson is { } p)
                plan = JsonSerializer.Deserialize<CodeFixPlanResult>(p, CodeFixContract.Json);

            if (a.ImplementResultJson is { } i)
                impl = JsonSerializer.Deserialize<CodeFixImplementResult>(i, CodeFixContract.Json);
        }
        catch (JsonException)
        {
            // Stored by an older contract. The denormalised columns still render.
        }

        return new CodeFixAttemptView
        {
            Id = a.Id,
            IncidentId = a.IncidentId,
            IncidentTitle = a.Incident?.Title ?? string.Empty,
            WorkItemId = a.WorkItemId,
            Issue = a.WorkItem is { } w ? $"{w.Repository}#{w.Number}" : null,
            IssueUrl = a.WorkItem?.Url,
            Workload = a.Workload,
            Repository = a.RepositoryUrl,
            DefaultBranch = a.DefaultBranch,
            Branch = a.Branch,
            State = a.State,
            RequestedBy = a.RequestedBy,
            Summary = a.Summary,
            RootCause = a.RootCause,
            Confidence = a.Confidence,
            VerificationLevel = a.VerificationLevel,
            NeedsCait = a.NeedsCait,
            Files = plan?.Files ?? [],
            Steps = plan?.Steps ?? [],
            Notes = plan?.Notes ?? [],
            NotVerifiable = plan?.Verification.NotVerifiable ?? [],
            DeniedToolCalls = [.. plan?.DeniedToolCalls ?? [], .. impl?.DeniedToolCalls ?? []],
            AnalysedRef = a.AnalysedRef,
            ContextSha = a.ContextSha,
            PlanJobName = a.PlanJobName,
            ImplementJobName = a.ImplementJobName,
            PlanCostUsd = a.PlanCostUsd,
            ImplementCostUsd = a.ImplementCostUsd,
            PlanCommentId = a.PlanCommentId,
            PrUrl = a.PrUrl,
            PrNumber = a.PrNumber,
            PrBody = a.PrBody,
            BuildPassed = impl?.BuildPassed,
            TestsPassed = impl?.TestsPassed,
            Deviations = impl?.Deviations ?? [],
            ApprovedBy = a.ApprovedBy,
            DecidedThrough = a.ApprovedBy is null || a.ApprovalSource == ApprovalSource.NotApplicable ? null : a.ApprovalSource,
            FailureReason = a.FailureReason,
            CreatedAt = a.CreatedAt,
            PlanStartedAt = a.PlanStartedAt,
            PlanReadyAt = a.PlanReadyAt,
            DecidedAt = a.DecidedAt,
            ImplementStartedAt = a.ImplementStartedAt,
            FinishedAt = a.FinishedAt,
        };
    }

    private static CodeFixEvaluationView ParseEvaluation(AuditEvent e)
    {
        bool eligible = false, would = false;
        List<string> codes = [], reasons = [];
        string? mode = null, repository = null;

        try
        {
            using var doc = JsonDocument.Parse(e.Detail ?? "{}");

            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                eligible = d.TryGetProperty("eligible", out var el) && el.ValueKind == JsonValueKind.True;
                would = d.TryGetProperty("would_have_started", out var w) && w.ValueKind == JsonValueKind.True;

                if (d.TryGetProperty("codes", out var c) && c.ValueKind == JsonValueKind.Array)
                    codes = [.. c.EnumerateArray().Select(x => x.GetString() ?? string.Empty)];

                if (d.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array)
                    reasons = [.. r.EnumerateArray().Select(x => x.GetString() ?? string.Empty)];

                mode = d.TryGetProperty("mode", out var m) ? m.GetString() : null;
                repository = d.TryGetProperty("repository", out var rp) && rp.ValueKind == JsonValueKind.String ? rp.GetString() : null;
            }
        }
        catch (JsonException)
        {
            reasons = [e.Summary];
        }

        return new CodeFixEvaluationView(e.At, eligible, would, codes, reasons, mode, repository, e.Actor);
    }
}
