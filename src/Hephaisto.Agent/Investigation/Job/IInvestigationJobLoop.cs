using Microsoft.Extensions.AI;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>Whether one investigation's model loop goes to a Job, and why.</summary>
public sealed record JobLoopDecision(ExecutorChoice Choice, string Explanation)
{
    public bool UseJob => Choice == ExecutorChoice.Job;

    public static JobLoopDecision InProcess(string why) => new(ExecutorChoice.InProcessByMode, why);
}

/// <summary>What the runner hands a Job-backed model loop: exactly what its own loop would use.</summary>
public sealed record JobLoopContext
{
    public required Incident Incident { get; init; }

    public required Guid InvestigationId { get; init; }

    /// <summary>The prompt <see cref="PromptComposer"/> composed - the one source of the investigation prompt.</summary>
    public required string SystemPrompt { get; init; }

    public required string OpeningMessage { get; init; }

    /// <summary>Already wrapped by <see cref="Llm.SafeToolDecorator"/>, bound to <see cref="Recorder"/>.</summary>
    public required IReadOnlyList<AIFunction> Tools { get; init; }

    public required InvestigationRecorder Recorder { get; init; }

    public required InvestigationRunner.ConclusionHolder Conclusion { get; init; }
}

/// <summary>How a Job-backed model loop ended.</summary>
public sealed record JobLoopOutcome
{
    /// <summary>
    /// True when the Job could give no answer - refused, failed, vanished, rate-limited or past its
    /// deadline - and the runner should investigate in-process instead.
    /// </summary>
    public bool FellBack { get; init; }

    public string? FallbackReason { get; init; }

    /// <summary>How the loop ended when it did not fall back.</summary>
    public TerminationReason Termination { get; init; } = TerminationReason.Faulted;

    /// <summary>The model the Job reported, when it reported one.</summary>
    public string? ModelId { get; init; }

    /// <summary>Model turns the Job reported, for <see cref="Investigation.StepsUsed"/>.</summary>
    public int Turns { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// The conclude call's code references the runner confirmed in its checkout, keyed by the index
    /// of the finding in that call. Empty without source access.
    /// </summary>
    public IReadOnlyList<CodeFix.Contract.InvestigateCodeRef> CodeRefs { get; init; } = [];

    /// <summary>The repository the investigator read, when it read one.</summary>
    public string? Repository { get; init; }

    /// <summary>The commit it read.</summary>
    public string? AnalysedRef { get; init; }

    public static JobLoopOutcome Fallback(string reason) => new() { FellBack = true, FallbackReason = reason };
}

/// <summary>
/// The investigation's model loop, run somewhere other than this process (v0.12.0 F5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the model loop.</b> The runner still builds the tools, records every step, grounds the
/// conclusion and plans; an implementation only decides whether it takes a run and drives the
/// remote model against the tools it is handed. That is what keeps grounding honest: every
/// citation is still checked against bytes Hephaisto's own tools returned.
/// </para>
/// <para>
/// Optional on <see cref="InvestigationRunner"/>. The eval harness and every test that builds a
/// runner by hand pass none, and get exactly the v0.11 loop.
/// </para>
/// </remarks>
public interface IInvestigationJobLoop
{
    /// <summary>Whether this investigation goes to a Job. Cheap, and never throws.</summary>
    Task<JobLoopDecision> DecideAsync(Incident incident, CancellationToken ct);

    /// <summary>
    /// The budget the Job's tool calls are held to, derived from the in-process one. Its wall clock
    /// is the Job's deadline; its tool calls may be more, because a model on a subscription is not
    /// the one the in-process defaults were sized for.
    /// </summary>
    Llm.InvestigationBudgetOptions BudgetFor(Llm.InvestigationBudgetOptions inProcess);

    /// <summary>Runs the model loop in a Job. Never throws for a Job that failed: that is a fallback.</summary>
    Task<JobLoopOutcome> RunAsync(JobLoopContext context, CancellationToken ct);
}
