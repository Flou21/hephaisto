using Hephaisto.Agent.Persistence;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The global, Postgres-backed rolling windows, seen from this layer.
/// </summary>
/// <remarks>
/// <para>
/// Two budgets exist and they answer different questions.
/// <see cref="InvestigationBudget"/> asks "is <i>this</i> investigation still worth
/// continuing" and lives in memory for its few minutes. This one asks "should the agent be
/// starting <i>any</i> investigation right now" and is counted in rows, because an in-memory
/// counter resets on the crash that a runaway loop usually causes.
/// </para>
/// <para>
/// An interface here rather than a direct dependency on <see cref="LlmBudgetService"/>
/// because that type needs a <c>DbContext</c>, and requiring Postgres to unit-test the
/// investigation loop would mean the loop stops being unit-tested.
/// </para>
/// </remarks>
public interface IGlobalLlmBudget
{
    /// <summary>Asked once, before the first token is spent.</summary>
    Task<GlobalBudgetVerdict> CheckAsync(Guid incidentId, CancellationToken ct);

    /// <summary>
    /// Persists what an investigation spent.
    /// </summary>
    /// <remarks>
    /// Called by the composition root <i>with</i> the investigation's steps, not by the
    /// runner: <see cref="LlmBudgetService.Enlist"/> stages the usage row without saving so
    /// it commits in the same transaction as the steps that consumed the tokens. A separate
    /// save can fail in between, and the dangerous direction is the likely one - the tokens
    /// were really spent and the budget does not know.
    /// </remarks>
    Task RecordAsync(Guid incidentId, long inputTokens, long outputTokens, decimal costUsd, CancellationToken ct);
}

/// <summary>Flattened <see cref="BudgetVerdict"/>: this layer needs the verdict, not the ratios.</summary>
public sealed record GlobalBudgetVerdict(bool Allowed, string Reason)
{
    public static readonly GlobalBudgetVerdict Allow = new(true, string.Empty);
}

/// <summary>
/// Adapts <see cref="LlmBudgetService"/> to <see cref="IGlobalLlmBudget"/>. The only place in
/// this layer that touches persistence.
/// </summary>
public sealed class LlmBudgetServiceAdapter(LlmBudgetService budget) : IGlobalLlmBudget
{
    public async Task<GlobalBudgetVerdict> CheckAsync(Guid incidentId, CancellationToken ct)
    {
        var verdict = await budget.CheckAsync(incidentId, ct).ConfigureAwait(false);

        return verdict.Allowed
            ? GlobalBudgetVerdict.Allow
            : new GlobalBudgetVerdict(false, string.Join("; ", verdict.Reasons));
    }

    public Task RecordAsync(
        Guid incidentId,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        CancellationToken ct) =>
        budget.RecordAsync(incidentId, inputTokens, outputTokens, costUsd, ct);
}

/// <summary>
/// Used when persistence is not wired - the AppHost smoke run, and every unit test. Allows
/// everything, records nothing.
/// </summary>
/// <remarks>
/// Safe as a default only because it is the <i>outer</i> budget. The per-investigation
/// ceilings in <see cref="InvestigationBudget"/> are always enforced regardless, so a missing
/// global budget widens the cap from "the whole process" to "each incident" rather than
/// removing it.
/// </remarks>
public sealed class NullGlobalLlmBudget : IGlobalLlmBudget
{
    public Task<GlobalBudgetVerdict> CheckAsync(Guid incidentId, CancellationToken ct) =>
        Task.FromResult(GlobalBudgetVerdict.Allow);

    public Task RecordAsync(
        Guid incidentId,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        CancellationToken ct) => Task.CompletedTask;
}
