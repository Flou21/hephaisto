using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.Persistence;

/// <summary>
/// Bound from <c>Llm:Budget</c> through <see cref="IOptionsMonitor{T}"/>, so a cap can be
/// lowered from the ConfigMap while the agent is running - which is the moment you actually
/// want to lower one.
/// </summary>
public sealed class LlmBudgetOptions
{
    public const string SectionName = "Llm:Budget";

    /// <summary>
    /// A runaway backstop, deliberately NOT the ceiling that governs day to day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was 2,000,000, and backlog #74 is what that cost. A token cap and a cost cap
    /// standing side by side <b>imply a price</b>: $3.00 over 2M tokens is $1.50 per 1M. On a
    /// 3:1 input:output blend that is <b>exactly</b> gemini-3.7-flash's rate ($0.75 / $3.75),
    /// which is the default model - so on the default model the two caps were precisely
    /// commensurate and cost bound first, as intended. The coincidence is not a coincidence:
    /// they were chosen together, for that model, and nothing recorded that they were a pair.
    /// </para>
    /// <para>
    /// gpt-oss-120b blends to $0.065 per 1M, so 2M tokens is <b>thirteen cents</b>. The token
    /// cap bound first by a wide margin, and because <c>CheckAsync</c> tests tokens before cost
    /// the reported block named tokens while every human involved was looking at the money.
    /// The first full <c>--mode Auto</c> run refused <b>14 of 27</b> investigations outright,
    /// escalating them <c>BudgetExhausted</c>, having spent <b>$0.066 against a $3.00 hourly
    /// cap</b> - half the corpus declined on a budget 2% used.
    /// </para>
    /// <para>
    /// It cost a milestone rather than a run: the MVP bar reported "not applicable: 8 scenarios
    /// scored, the bar needs 10". Accuracy was 7 of 8. This number dropped the denominator.
    /// </para>
    /// <para>
    /// <b>50M is the smallest round figure that puts cost back in charge on every priced model
    /// this repo supports.</b> Reaching $3.00 takes 46.2M tokens at gpt-oss-120b's rate, so 50M
    /// costs $3.25 there and the cost cap binds first. At gemini-3.7-flash 50M would cost $75,
    /// far beyond the same cap, so cost binds first there too and <b>nothing about a hosted
    /// install changes</b>. Tokens stop being the budget and go back to being a bound.
    /// </para>
    /// <para>
    /// <b>Not raised further, and not removed.</b> A model with no entry in the price table
    /// bills as $0, so <see cref="MaxCostUsdPerHour"/> can never bind and the utilisation gauge
    /// reads 0% forever - see <c>OpenAiChatClientFactory</c>. For an unpriced model this cap is
    /// the only backstop left standing, and 50M is roughly twelve times a full twelve-fixture
    /// corpus run.
    /// </para>
    /// <para>
    /// The properly general fix is to DERIVE this from the cost cap and the resolved price,
    /// with a constant fallback when the model is unpriced. That is a design change and stays
    /// open on #74. This is the recalibration, and <c>LlmBudgetRelationshipTests</c> now fails
    /// if the pair drifts back into implying a price no supported model charges - an invariant
    /// that did not exist while the per-investigation ceilings had three.
    /// </para>
    /// </remarks>
    public long MaxTokensPerHour { get; set; } = 50_000_000;

    public decimal MaxCostUsdPerHour { get; set; } = 3.00m;

    public decimal MaxCostUsdPerDay { get; set; } = 20.00m;

    /// <summary>Per incident, across every investigation of it. A single incident that
    /// costs more than this is a loop, not a hard problem.</summary>
    public decimal MaxCostUsdPerIncident { get; set; } = 0.50m;

    public double WarnAtUtilization { get; set; } = 0.80;

    /// <summary>
    /// How many distinct hours may hit a cap inside the rolling window before the runaway
    /// backstop latches. Hours, not calls - a loop hits the cap thousands of times an hour.
    /// </summary>
    public int RunawayHourlyHitsBeforeLatch { get; set; } = 3;

    public TimeSpan RunawayWindow { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Which ceiling stopped the caller, if any.</summary>
public enum BudgetBlock
{
    None = 0,
    HourlyTokens = 1,
    HourlyCost = 2,
    DailyCost = 3,
    IncidentCost = 4,
    RunawayLatched = 5,
}

/// <summary>
/// The answer to "may I start an investigation". Carries the utilizations as well as the
/// verdict so the caller can emit the gauge without a second round trip.
/// </summary>
public sealed record BudgetVerdict
{
    public required bool Allowed { get; init; }

    public required BudgetBlock Block { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public required double HourlyTokenUtilization { get; init; }

    public required double HourlyCostUtilization { get; init; }

    public required double DailyCostUtilization { get; init; }

    public required double IncidentCostUtilization { get; init; }

    /// <summary>True past <see cref="LlmBudgetOptions.WarnAtUtilization"/> on any window.</summary>
    public required bool Warn { get; init; }

    /// <summary>
    /// The backstop has tripped: the caller must drop to <c>observe</c> and a human must
    /// re-arm. Distinct from <see cref="Allowed"/>, which is about this one call.
    /// </summary>
    public required bool RunawayTripped { get; init; }
}

/// <summary>
/// Sliding-window LLM spend, counted in Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Not an in-memory counter. An in-memory counter resets on every crash, which is precisely
/// when a runaway loop is most likely - the loop that burned the budget is usually the thing
/// that killed the pod, so the restart would hand it a clean slate and it would do it again.
/// Rows in a table survive that; a static long does not.
/// </para>
/// <para>
/// <b>Degrade, never die.</b> At 100% the agent stops STARTING new investigations. It keeps
/// detecting, dedup'ing, correlating, annotating and serving the UI - all of which are cheap
/// and are exactly what a human on call needs when the expensive part has been switched off.
/// Affected incidents go to <c>Escalated{BudgetExhausted}</c>, which is a legible outcome
/// rather than silence. An investigation already in flight is allowed to finish: killing it
/// mid-loop burns every token already spent for no result, which makes the overspend worse,
/// not better.
/// </para>
/// </remarks>
public sealed class LlmBudgetService(
    HephaistoDbContext db,
    IAgentModeStore modes,
    IClock clock,
    IOptionsMonitor<LlmBudgetOptions> options,
    ILogger<LlmBudgetService> logger)
{
    public const string WindowHourTokens = "hour-tokens";
    public const string WindowHourCost = "hour-cost";
    public const string WindowDayCost = "day-cost";

    public async Task<BudgetVerdict> CheckAsync(Guid incidentId, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = clock.UtcNow;

        var hour = await SumAsync(now.AddHours(-1), null, ct);
        var day = await SumAsync(now.AddDays(-1), null, ct);
        var incident = await SumAsync(DateTimeOffset.MinValue, incidentId, ct);

        var hourlyTokens = Ratio(hour.Tokens, o.MaxTokensPerHour);
        var hourlyCost = Ratio(hour.Cost, o.MaxCostUsdPerHour);
        var dailyCost = Ratio(day.Cost, o.MaxCostUsdPerDay);
        var incidentCost = Ratio(incident.Cost, o.MaxCostUsdPerIncident);

        var latched = (await modes.GetAsync(ct)).RunawayLatched;

        var block = BudgetBlock.None;
        var reasons = new List<string>();

        if (latched)
        {
            block = BudgetBlock.RunawayLatched;
            reasons.Add("runaway backstop latched; a human must re-arm before investigations resume");
        }
        else if (hour.Tokens >= o.MaxTokensPerHour)
        {
            block = BudgetBlock.HourlyTokens;
            reasons.Add($"{hour.Tokens:N0} tokens this hour (max {o.MaxTokensPerHour:N0})");
        }
        else if (hour.Cost >= o.MaxCostUsdPerHour)
        {
            block = BudgetBlock.HourlyCost;
            reasons.Add($"${hour.Cost:F4} this hour (max ${o.MaxCostUsdPerHour:F2})");
        }
        else if (day.Cost >= o.MaxCostUsdPerDay)
        {
            block = BudgetBlock.DailyCost;
            reasons.Add($"${day.Cost:F4} today (max ${o.MaxCostUsdPerDay:F2})");
        }
        else if (incident.Cost >= o.MaxCostUsdPerIncident)
        {
            block = BudgetBlock.IncidentCost;
            reasons.Add($"${incident.Cost:F4} spent on incident {incidentId} (max ${o.MaxCostUsdPerIncident:F2})");
        }

        var runawayTripped = false;

        // An hourly ceiling being hit is what the backstop counts, so it is recorded here
        // rather than in RecordAsync: this is the point where "the cap held" is known.
        if (block is BudgetBlock.HourlyTokens or BudgetBlock.HourlyCost)
        {
            runawayTripped = await NoteHourlyBreachAsync(block, now, reasons[^1], ct);
        }
        else if (block is BudgetBlock.RunawayLatched)
        {
            runawayTripped = true;
        }

        return new BudgetVerdict
        {
            Allowed = block is BudgetBlock.None,
            Block = block,
            Reasons = reasons,
            HourlyTokenUtilization = hourlyTokens,
            HourlyCostUtilization = hourlyCost,
            DailyCostUtilization = dailyCost,
            IncidentCostUtilization = incidentCost,
            Warn = Math.Max(Math.Max(hourlyTokens, hourlyCost), Math.Max(dailyCost, incidentCost)) >= o.WarnAtUtilization,
            RunawayTripped = runawayTripped,
        };
    }

    /// <summary>
    /// Records spend and saves immediately. For a caller with nothing else to write.
    /// </summary>
    public async Task RecordAsync(
        Guid incidentId,
        long inTokens,
        long outTokens,
        decimal costUsd,
        CancellationToken ct)
    {
        Enlist(incidentId, null, inTokens, outTokens, costUsd);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stages the usage row WITHOUT saving, so it lands in the same transaction as the
    /// <see cref="Core.Domain.InvestigationStep"/> that consumed the tokens.
    /// </summary>
    /// <remarks>
    /// This is the whole point of exposing it separately. If the step row and the counter
    /// are written by two independent saves, any failure between them leaves accounting
    /// that disagrees with the step log - and the direction of the drift is the dangerous
    /// one: the step happened, the tokens were really spent, and the budget does not know.
    /// Call this, then save the step; one commit, or neither.
    /// </remarks>
    public LlmUsageRecord Enlist(
        Guid incidentId,
        Guid? investigationId,
        long inTokens,
        long outTokens,
        decimal costUsd)
    {
        var record = new LlmUsageRecord
        {
            IncidentId = incidentId,
            InvestigationId = investigationId,
            At = clock.UtcNow,
            InputTokens = inTokens,
            OutputTokens = outTokens,
            CostUsd = costUsd,
        };

        db.LlmUsage.Add(record);

        return record;
    }

    /// <summary>
    /// Feeds the <c>hephaisto_llm_budget_utilization</c> gauge. 0..1, and deliberately not
    /// clamped above 1: a window sitting at 1.4 is a different fact from one sitting at 1.0.
    /// </summary>
    public async Task<double> GetUtilizationAsync(string window, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = clock.UtcNow;

        return window switch
        {
            WindowHourTokens => Ratio((await SumAsync(now.AddHours(-1), null, ct)).Tokens, o.MaxTokensPerHour),
            WindowHourCost => Ratio((await SumAsync(now.AddHours(-1), null, ct)).Cost, o.MaxCostUsdPerHour),
            WindowDayCost => Ratio((await SumAsync(now.AddDays(-1), null, ct)).Cost, o.MaxCostUsdPerDay),
            _ => throw new ArgumentOutOfRangeException(
                nameof(window),
                window,
                $"expected one of {WindowHourTokens}, {WindowHourCost}, {WindowDayCost}"),
        };
    }

    /// <summary>
    /// Records that an hourly cap was hit, deduplicated to one row per hour per kind, and
    /// latches the agent if it has now happened in enough distinct hours.
    /// </summary>
    /// <remarks>
    /// The backstop exists because every individual cap is doing its job in this scenario -
    /// the spend stops each hour, exactly as configured - while something is still wrong
    /// enough to run into the ceiling hour after hour. Nothing below this level can notice
    /// that, because each hour looks like a correctly enforced budget.
    /// </remarks>
    private async Task<bool> NoteHourlyBreachAsync(
        BudgetBlock block,
        DateTimeOffset now,
        string detail,
        CancellationToken ct)
    {
        var bucket = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var kind = block.ToString();

        // ON CONFLICT DO NOTHING against the unique (hour_bucket, kind) index: a runaway
        // loop calls this thousands of times an hour and must leave exactly one row.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO llm_budget_breaches (id, hour_bucket, kind, at, detail)
             VALUES ({Guid.CreateVersion7()}, {bucket}, {kind}, {now}, {detail})
             ON CONFLICT (hour_bucket, kind) DO NOTHING
             """,
            ct);

        var o = options.CurrentValue;
        var since = now - o.RunawayWindow;

        var distinctHours = await db.LlmBudgetBreaches
            .Where(b => b.HourBucket >= since)
            .Select(b => b.HourBucket)
            .Distinct()
            .CountAsync(ct);

        if (distinctHours < o.RunawayHourlyHitsBeforeLatch)
        {
            return false;
        }

        var reason =
            $"hourly LLM cap hit in {distinctHours} distinct hours within {o.RunawayWindow.TotalHours:F0}h";

        logger.LogError("Runaway backstop tripped: {Reason}. Dropping to observe until re-armed", reason);

        await modes.LatchAsync(reason, ct);

        return true;
    }

    private async Task<(long Tokens, decimal Cost)> SumAsync(
        DateTimeOffset since,
        Guid? incidentId,
        CancellationToken ct)
    {
        var query = db.LlmUsage.AsNoTracking().Where(u => u.At >= since);

        if (incidentId is { } id)
        {
            query = query.Where(u => u.IncidentId == id);
        }

        // One round trip for both aggregates. Two SUMs over the same rows is one index scan;
        // two queries is two, and they would see the window at two different instants.
        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Tokens = (long?)g.Sum(u => u.InputTokens + u.OutputTokens),
                Cost = (decimal?)g.Sum(u => u.CostUsd),
            })
            .FirstOrDefaultAsync(ct);

        return (totals?.Tokens ?? 0, totals?.Cost ?? 0m);
    }

    private static double Ratio(long used, long cap) => cap <= 0 ? 1 : (double)used / cap;

    private static double Ratio(decimal used, decimal cap) => cap <= 0 ? 1 : (double)(used / cap);
}
