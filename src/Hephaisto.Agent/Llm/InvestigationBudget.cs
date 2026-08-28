using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Thrown when a per-investigation ceiling is reached. Carries the
/// <see cref="TerminationReason"/> so the runner records *which* budget blew rather than a
/// generic failure - "it ran out of steps" and "it ran out of money" want different
/// follow-up from a human.
/// </summary>
public sealed class BudgetExhaustedException(TerminationReason reason, string detail)
    : Exception($"Investigation budget exhausted ({reason}): {detail}")
{
    public TerminationReason Reason { get; } = reason;

    public string Detail { get; } = detail;
}

/// <summary>
/// The mutable counters for one investigation, shared by <see cref="BudgetGuardChatClient"/>
/// (which counts model round trips and spend) and every <see cref="SafeToolDecorator"/>
/// (which counts tool calls).
/// </summary>
/// <remarks>
/// <para>
/// One object rather than two independent counters because the two enforcement points must
/// agree on when the run is over: a tool call that pushes the run past its cap has to stop
/// the <i>next model turn</i>, and the tool decorator has no other way to say so.
/// </para>
/// <para>
/// Not thread-safe by design of the caller, not by absence of thought: one investigation is
/// one logical thread of control. The interlocked increments are here only because
/// <c>FunctionInvokingChatClient</c> may invoke several tool calls from one turn
/// concurrently, and a lost tool-call increment would be a budget that under-counts.
/// </para>
/// </remarks>
public sealed class InvestigationBudget(InvestigationBudgetOptions options, IClock clock)
{
    private readonly Lock _gate = new();
    private readonly DateTimeOffset _startedAt = clock.UtcNow;

    private int _steps;
    private int _toolCalls;
    private long _inputTokens;
    private long _outputTokens;
    private decimal _costUsd;

    public InvestigationBudgetOptions Options => options;

    public DateTimeOffset StartedAt => _startedAt;

    public DateTimeOffset Deadline => _startedAt + options.MaxWallClock;

    public int Steps => Volatile.Read(ref _steps);

    public int ToolCalls => Volatile.Read(ref _toolCalls);

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public decimal CostUsd
    {
        get
        {
            lock (_gate)
            {
                return _costUsd;
            }
        }
    }

    /// <summary>The first ceiling that was reached, or null while the run is still inside all of them.</summary>
    public TerminationReason? Breach { get; private set; }

    public bool IsExhausted => Breach is not null;

    /// <summary>
    /// Checked immediately before every model round trip, and it throws.
    /// </summary>
    /// <remarks>
    /// Deliberately a <i>pre</i>-call check rather than a post-call one. Aborting after a
    /// call that has already been paid for burns the tokens and keeps none of the answer,
    /// which makes an overspend worse rather than better - the same reasoning
    /// <c>LlmBudgetService</c> applies when it lets an in-flight investigation finish. The
    /// cost of this choice is bounded and known: a run can overshoot by at most one call.
    /// </remarks>
    public void EnsureCanStartStep()
    {
        // The latched breach first: a tool call refused by TryConsumeToolCall records the
        // breach there and cannot throw from inside the tool, so this is where that run
        // actually stops.
        var breach = Breach ?? Evaluate();

        if (breach is null)
        {
            return;
        }

        Breach = breach;
        throw new BudgetExhaustedException(breach.Value, Describe(breach.Value));
    }

    /// <summary>
    /// Records what a model round trip actually cost. Returns the cost of this step alone,
    /// which the caller writes onto the <see cref="InvestigationStep"/>.
    /// </summary>
    public void RecordStep(long inputTokens, long outputTokens, decimal costUsd)
    {
        Interlocked.Increment(ref _steps);
        Interlocked.Add(ref _inputTokens, inputTokens);
        Interlocked.Add(ref _outputTokens, outputTokens);

        lock (_gate)
        {
            _costUsd += costUsd;
        }

        // Latch the breach now so the runner can report the right reason even if the loop
        // ends for another cause on the same turn.
        Breach ??= Evaluate();
    }

    /// <summary>
    /// Counted by the tool decorator before the tool runs. Returns false when the call must
    /// be refused.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws. An exception raised inside a tool invocation is caught by
    /// <c>FunctionInvokingChatClient</c> and handed back to the model as a failed tool
    /// result, so throwing here would not stop the loop - it would just look like a broken
    /// tool. Refusing with an explanatory string and letting the next
    /// <see cref="EnsureCanStartStep"/> stop the run is both honest to the model and
    /// actually effective.
    /// </remarks>
    public bool TryConsumeToolCall()
    {
        var used = Interlocked.Increment(ref _toolCalls);

        if (used <= options.MaxToolCalls)
        {
            return true;
        }

        Breach ??= TerminationReason.ToolCallBudgetExhausted;
        return false;
    }

    public TimeSpan Elapsed => clock.UtcNow - _startedAt;

    private TerminationReason? Evaluate()
    {
        if (_steps >= options.MaxSteps)
        {
            return TerminationReason.StepBudgetExhausted;
        }

        // Zero means "this phase has no tools", not "the tool budget is spent". Phase 2 runs
        // on a budget with MaxToolCalls = 0 by design, and reading that as exhausted would
        // stop the planning call before it was made - so an investigation that found the
        // cause would end with no plan and no explanation.
        if (options.MaxToolCalls > 0 && Volatile.Read(ref _toolCalls) >= options.MaxToolCalls)
        {
            return TerminationReason.ToolCallBudgetExhausted;
        }

        if (clock.UtcNow >= Deadline)
        {
            return TerminationReason.WallClockExhausted;
        }

        if (Interlocked.Read(ref _inputTokens) >= options.MaxInputTokens)
        {
            return TerminationReason.TokenBudgetExhausted;
        }

        lock (_gate)
        {
            if (_costUsd >= options.MaxCostUsd)
            {
                return TerminationReason.CostBudgetExhausted;
            }
        }

        return null;
    }

    private string Describe(TerminationReason reason) => reason switch
    {
        TerminationReason.StepBudgetExhausted => $"{Steps} of {options.MaxSteps} steps used",
        TerminationReason.ToolCallBudgetExhausted => $"{ToolCalls} of {options.MaxToolCalls} tool calls used",
        TerminationReason.WallClockExhausted =>
            $"{Elapsed.TotalSeconds:F0}s elapsed of {options.MaxWallClock.TotalSeconds:F0}s",
        TerminationReason.TokenBudgetExhausted =>
            $"{InputTokens:N0} of {options.MaxInputTokens:N0} input tokens used",
        TerminationReason.CostBudgetExhausted => $"${CostUsd:F4} of ${options.MaxCostUsd:F2} spent",
        _ => reason.ToString(),
    };
}
