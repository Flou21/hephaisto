using System.Diagnostics;
using Microsoft.Extensions.AI;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The per-investigation budget, enforced as a link in the <see cref="IChatClient"/> chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Budgets are enforced here, in code, and are not stated in the system prompt.</b> A
/// model asked to stay within twelve steps will sincerely believe it did; a counter cannot
/// be persuaded. Everything the prompt says about spending the step budget wisely is advice
/// on how to use it well, not the mechanism that bounds it.
/// </para>
/// <para>
/// <b>Where this sits in the chain matters.</b> It is built <i>innermost</i>, beneath
/// <c>UseFunctionInvocation()</c>, because <c>FunctionInvokingChatClient</c> turns one
/// caller-visible request into as many provider round trips as the model wants tool
/// iterations. Wrapped outside it, this would count 1 where the bill counts 9. Innermost, one
/// call through here is exactly one call to the provider - which is what a "step" means and
/// what gets charged.
/// </para>
/// <para>
/// Throwing is the only way to stop the loop. <c>FunctionInvokingChatClient</c> iterates
/// until the model stops asking for tools; there is no return value that means "stop". The
/// exception unwinds out of <c>GetResponseAsync</c> and the runner reads
/// <see cref="BudgetExhaustedException.Reason"/> for the right
/// <see cref="TerminationReason"/>.
/// </para>
/// </remarks>
public sealed class BudgetGuardChatClient(
    IChatClient inner,
    InvestigationBudget budget,
    LlmPricing pricing,
    string defaultModelId,
    IInvestigationRecorder? recorder = null,
    Guid? incidentId = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        budget.EnsureCanStartStep();

        var start = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            Account(response, Elapsed(start), error: null);

            return response;
        }
        catch (Exception ex) when (ex is not BudgetExhaustedException and not OperationCanceledException)
        {
            // A failed call still cost wall clock and, usually, tokens the provider will
            // bill for. Recording it as a step keeps the audit trail honest about what was
            // attempted, and keeps a provider that fails slowly from being free.
            recorder?.RecordLlmTurn(defaultModelId, 0, 0, 0m, Elapsed(start), ex.Message, null);
            budget.RecordStep(0, 0, 0m);
            throw;
        }
    }

    /// <summary>
    /// Not supported, deliberately.
    /// </summary>
    /// <remarks>
    /// Streaming would have to accumulate usage across updates, and a provider that omits
    /// usage on the final update - several do, intermittently - would silently produce a
    /// free investigation. Nothing in Hephaisto streams: the UI renders persisted steps, not
    /// a live token feed. An unenforceable budget is worse than a missing feature, so this
    /// says so out loud rather than half-counting.
    /// </remarks>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Hephaisto does not stream LLM responses: streaming usage accounting is not reliable "
            + "enough to enforce a budget on, and an unenforceable budget is not a budget.");

    private void Account(ChatResponse response, long durationMs, string? error)
    {
        var modelId = response.ModelId ?? defaultModelId;
        var input = response.Usage?.InputTokenCount ?? 0;
        var output = response.Usage?.OutputTokenCount ?? 0;
        var cost = pricing.CostOf(modelId, input, output);

        budget.RecordStep(input, output, cost);
        recorder?.RecordLlmTurn(modelId, input, output, cost, durationMs, error, DigestOf(response));

        var tags = new TagList
        {
            { "gen_ai.request.model", modelId },
            { "incident.id", incidentId?.ToString() },
        };

        var inputTags = tags;
        inputTags.Add("gen_ai.token.type", "input");
        LlmInstrumentation.Tokens.Add(input, inputTags);

        var outputTags = tags;
        outputTags.Add("gen_ai.token.type", "output");
        LlmInstrumentation.Tokens.Add(output, outputTags);

        LlmInstrumentation.CostUsd.Add((double)cost, tags);
        LlmInstrumentation.InvestigationSteps.Add(1, tags);
    }

    /// <summary>How long a turn's digest may be before it is clipped.</summary>
    /// <remarks>
    /// Generous, because this is the only record of the model's reasoning and a truncated
    /// chain of thought is often worse than none. It is still bounded: the digest is rendered
    /// into a Blazor circuit and held in memory for every in-flight investigation.
    /// </remarks>
    private const int MaxDigestChars = 8_000;

    /// <summary>
    /// What the turn produced, as text: the model's reasoning, its prose, and which tools it
    /// went on to ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tool arguments are deliberately not included</b>, only the tool name. The arguments
    /// are recorded on the tool-call step itself, where <see cref="SafeToolDecorator"/> has
    /// already redacted them. Serialising the raw <c>FunctionCallContent.Arguments</c> here
    /// would put an unredacted copy of the same values one expander away, which quietly
    /// undoes that redaction.
    /// </para>
    /// <para>
    /// Returns null rather than an empty string when a turn genuinely produced nothing, so the
    /// UI can distinguish "no output" from "not recorded".
    /// </para>
    /// </remarks>
    internal static string? DigestOf(ChatResponse response)
    {
        var parts = new List<string>();

        foreach (var content in response.Messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case TextReasoningContent { Text: { Length: > 0 } text }:
                    parts.Add($"[reasoning]\n{text.Trim()}");
                    break;

                case TextContent { Text: { Length: > 0 } text }:
                    parts.Add(text.Trim());
                    break;

                case FunctionCallContent call:
                    parts.Add($"-> calls {call.Name}");
                    break;

                case ErrorContent error:
                    parts.Add($"[error] {error.Message}");
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var joined = string.Join("\n\n", parts);

        return joined.Length <= MaxDigestChars
            ? joined
            : joined[..MaxDigestChars] + "\n\n… clipped";
    }

    private static long Elapsed(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
