using System.Diagnostics.Metrics;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Telemetry;

/// <summary>
/// Polls the LLM budget windows and publishes them as <c>hephaisto.llm.budget_utilization</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This gauge was declared, documented, alerted on and charted for months without ever
/// being emitted.</b> <see cref="HephaistoTelemetry.Metrics.LlmBudgetUtilization"/> named it,
/// <see cref="LlmBudgetService.GetUtilizationAsync"/> computed it, two rules in
/// <c>files/alerts/observability-selfcheck.yaml</c> alerted on it and two dashboard panels drew
/// it - and no instrument existed, so both rules were unfireable and both panels permanently
/// empty. Nothing failed. A gauge nobody emits reads as "no data", which on a budget panel is
/// indistinguishable from "nothing has been spent".
/// </para>
/// <para>
/// <b>Why a poller.</b> Observable-gauge callbacks are synchronous and
/// <see cref="LlmBudgetService.GetUtilizationAsync"/> reads Postgres, so the callback cannot
/// simply call it - which is very likely why this was never wired up. The resolution is the one
/// <c>SwitchWatcher</c> already uses for <c>hephaisto.mode</c>: a background service does the
/// awaiting and caches the result, and the callback reads the cache. Both instruments are
/// visibility rather than control, so a poller's staleness is acceptable in a way it would not
/// be for a gate.
/// </para>
/// <para>
/// <b>This must not be the enforcement path.</b> Every budget decision goes through
/// <see cref="LlmBudgetService.CheckAsync"/>, which re-reads the windows itself inside the same
/// transaction that inserts the usage row. If this class stops, spending still stops correctly;
/// only the dashboard goes stale. Nothing may come to depend on this value.
/// </para>
/// <para>
/// The scope label reuses <see cref="LlmBudgetService"/>'s own window constants rather than
/// inventing a parallel vocabulary, so the label on a series is exactly the argument that
/// produced it. The dashboard's spec table claimed <c>incident/daily/monthly</c>; those windows
/// do not exist in the code and that text is corrected alongside this.
/// </para>
/// </remarks>
public sealed class BudgetGaugePublisher : BackgroundService
{
    /// <summary>
    /// Slower than <c>SwitchWatcher</c>'s 10s: this is three aggregate queries over
    /// <c>llm_usage</c>, and a budget window that is 30 seconds stale changes no decision.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopes;
    private readonly BudgetUtilizationSnapshot snapshot;
    private readonly ILogger<BudgetGaugePublisher> logger;
    private readonly Meter meter;

    public BudgetGaugePublisher(
        IServiceScopeFactory scopes,
        BudgetUtilizationSnapshot snapshot,
        IMeterFactory meterFactory,
        ILogger<BudgetGaugePublisher> logger)
    {
        this.scopes = scopes;
        this.snapshot = snapshot;
        this.logger = logger;

        meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        meter.CreateObservableGauge(
            HephaistoTelemetry.Metrics.LlmBudgetUtilization,
            snapshot.Measure,
            unit: "1",
            description:
                "Fraction of each LLM budget window consumed. Deliberately NOT clamped above 1: "
                + "a window sitting at 1.4 is a different fact from one sitting at 1.0.");
    }

    public override void Dispose()
    {
        meter.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Poll once before the first delay. Without this the gauge reports 0 for the first
        // interval after a restart, which on a panel is a spend that appears to have been
        // refunded.
        await PollAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var budget = scope.ServiceProvider.GetRequiredService<LlmBudgetService>();

            // Read all three before publishing any, so the gauge never shows two windows from
            // different instants - a reader comparing hour-cost against day-cost would
            // otherwise occasionally see the daily figure below the hourly one.
            var hourTokens = await budget
                .GetUtilizationAsync(LlmBudgetService.WindowHourTokens, ct).ConfigureAwait(false);
            var hourCost = await budget
                .GetUtilizationAsync(LlmBudgetService.WindowHourCost, ct).ConfigureAwait(false);
            var dayCost = await budget
                .GetUtilizationAsync(LlmBudgetService.WindowDayCost, ct).ConfigureAwait(false);

            snapshot.Set(hourTokens, hourCost, dayCost);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let the poller die, and never publish a zero on failure. Keeping the last
            // known values means a database blip shows as a flat line rather than as a budget
            // that emptied itself, and the alert rules read the flat line correctly.
            logger.LogError(ex, "Budget utilization poll failed; keeping the previous values");
        }
    }
}

/// <summary>
/// The last polled utilizations, shared between the poller that computes them and the gauge
/// callback that reports them.
/// </summary>
/// <remarks>
/// Starts <see cref="Published"/> false so the gauge emits <b>nothing at all</b> until the first
/// successful poll. Emitting 0.0 would be a claim - "no budget consumed" - made before anything
/// had been read, and after a restart mid-incident that claim is wrong. Absent is honest;
/// <c>max by (scope)</c> over an absent series returns nothing, which the alert rules treat as
/// not firing rather than as zero.
/// </remarks>
public sealed class BudgetUtilizationSnapshot
{
    private readonly Lock gate = new();

    private double hourTokens;
    private double hourCost;
    private double dayCost;

    public bool Published { get; private set; }

    public void Set(double hourTokensValue, double hourCostValue, double dayCostValue)
    {
        lock (gate)
        {
            hourTokens = hourTokensValue;
            hourCost = hourCostValue;
            dayCost = dayCostValue;
            Published = true;
        }
    }

    /// <summary>The gauge callback. Synchronous and allocation-light by contract.</summary>
    public IEnumerable<Measurement<double>> Measure()
    {
        double t, h, d;

        lock (gate)
        {
            if (!Published)
            {
                return [];
            }

            (t, h, d) = (hourTokens, hourCost, dayCost);
        }

        return
        [
            new Measurement<double>(t, new KeyValuePair<string, object?>("scope", LlmBudgetService.WindowHourTokens)),
            new Measurement<double>(h, new KeyValuePair<string, object?>("scope", LlmBudgetService.WindowHourCost)),
            new Measurement<double>(d, new KeyValuePair<string, object?>("scope", LlmBudgetService.WindowDayCost)),
        ];
    }
}
