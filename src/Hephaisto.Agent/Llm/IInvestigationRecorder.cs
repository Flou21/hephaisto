using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Where the two enforcement points write what they saw. <see cref="BudgetGuardChatClient"/>
/// records model turns; <see cref="SafeToolDecorator"/> records tool calls.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a direct dependency on the runner because it is what makes the
/// loop testable without a provider: a test records into a list and asserts on the ordinals,
/// digests and token counts, with no network anywhere.
/// </para>
/// <para>
/// The single shared ordinal is the reason this is one interface and not two. Steps are
/// written from two places - the chat client for turns, the tool decorator for calls - and
/// the ordered list is what the UI renders as "here is exactly what it did". Two independent
/// counters would produce a plausible-looking sequence that interleaves wrongly, which is
/// worse than no sequence at all.
/// </para>
/// </remarks>
public interface IInvestigationRecorder
{
    /// <summary>The investigation these steps belong to. Grounding is scoped to it.</summary>
    Guid InvestigationId { get; }

    InvestigationStep RecordLlmTurn(
        string? modelId,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        long durationMs,
        string? error);

    /// <summary>
    /// Reserves the step for a tool call and returns it, <b>before</b> the tool runs.
    /// </summary>
    /// <remarks>
    /// Two-phase on purpose. The step's id has to exist before the result is rendered,
    /// because the model is shown that id in the result header and cites it back as
    /// <see cref="Evidence.StepId"/>. A step created after the call would have an id nobody
    /// could have quoted.
    /// </remarks>
    InvestigationStep BeginToolCall(string toolName, string server, string? argumentsJson);

    void CompleteToolCall(
        InvestigationStep step,
        string resultDigest,
        string? rawResult,
        bool truncated,
        int resultBytes,
        long durationMs,
        string? error);
}
