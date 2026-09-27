using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.CodeFix;

/// <summary>One attempt, as the console and the API show it.</summary>
public sealed record CodeFixAttemptView
{
    public required Guid Id { get; init; }
    public required Guid IncidentId { get; init; }
    public required string IncidentTitle { get; init; }
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
    public string? PrUrl { get; init; }
    public int? PrNumber { get; init; }
    public bool? BuildPassed { get; init; }
    public bool? TestsPassed { get; init; }
    public IReadOnlyList<string> Deviations { get; init; } = [];
    public string? ApprovedBy { get; init; }
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

/// <summary>Read side of the code-fix stage, for the console and <c>/api</c>.</summary>
public sealed class CodeFixQueries(
    HephaistoDbContext db,
    ICodeFixSwitch codeFixSwitch,
    IOptionsMonitor<CodeFixOptions> options,
    IOptionsMonitor<Options.AuthOptions> auth)
{
    public async Task<IReadOnlyList<CodeFixAttemptView>> ListAsync(CodeFixState? state, int limit, CancellationToken ct)
    {
        var query = db.CodeFixAttempts.AsNoTracking().Include(a => a.Incident).AsQueryable();

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
            PrUrl = a.PrUrl,
            PrNumber = a.PrNumber,
            BuildPassed = impl?.BuildPassed,
            TestsPassed = impl?.TestsPassed,
            Deviations = impl?.Deviations ?? [],
            ApprovedBy = a.ApprovedBy,
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
