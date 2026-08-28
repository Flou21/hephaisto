using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Retries a provider call that failed for a reason the provider itself calls temporary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists when the SDK has its own retry.</b> Google.GenAI exposes
/// <c>HttpOptions.RetryOptions</c> and Hephaisto configures it, but that configuration does
/// not reach the <c>AsIChatClient</c> path. Measured on the dev cluster on 2026-08-28, with
/// five attempts and 1s/2s/4s/8s backoff configured and no warning logged: failed turns
/// returned in 1.2s to 5.7s. Four retries cannot complete in under fifteen seconds, so
/// nothing retried. The SDK setting is left in place because it does cover the embedding
/// path, which has no chat-client chain around it.
/// </para>
/// <para>
/// <b>Why innermost, beneath <see cref="BudgetGuardChatClient"/>.</b> The budget guard is
/// built innermost precisely so that one pass through it is one provider round trip, which is
/// what a step means and what gets billed. Retrying above it would re-enter
/// <c>EnsureCanStartStep()</c> for every attempt and spend the investigation's step budget on
/// calls that returned zero tokens - twelve steps could become three real questions and nine
/// retries. Underneath, an attempt that fails is invisible to the budget, which is correct:
/// the provider charges nothing for refusing to answer.
/// </para>
/// <para>
/// <b>What counts as retryable.</b> Primarily the HTTP status - 408, 429 and 5xx - which is
/// the honest signal. The message check behind it is a deliberate backstop: this provider
/// reports an overload by throwing with its own prose ("This model is currently experiencing
/// high demand"), and depending on the path the status may not survive onto the exception. A
/// missed retry here costs a whole investigation, so the check errs toward retrying and logs
/// which arm matched, so the heuristic can be narrowed once the shape is certain.
/// </para>
/// </remarks>
public sealed class TransientRetryChatClient(
    IChatClient inner,
    LlmRetryOptions options,
    ILogger logger) : DelegatingChatClient(inner)
{
    /// <summary>
    /// Substrings that mean "ask again". Lowercase; matched case-insensitively against the
    /// whole exception chain.
    /// </summary>
    private static readonly string[] RetryableMarkers =
    [
        "high demand",
        "overloaded",
        "unavailable",
        "resource_exhausted",
        "resource exhausted",
        "try again later",
        "temporarily",
        "rate limit",
        "too many requests",
        "deadline exceeded",
    ];

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(1, options.Attempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(messages, chatOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < attempts
                && !cancellationToken.IsCancellationRequested
                && Classify(ex) is { } reason)
            {
                var delay = DelayFor(attempt);

                logger.LogWarning(
                    ex,
                    "Provider call failed transiently ({Reason}); retrying in {Delay} "
                    + "(attempt {Attempt} of {Attempts}). Without this the whole investigation "
                    + "would be discarded.",
                    reason,
                    delay,
                    attempt,
                    attempts);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Not supported. <see cref="BudgetGuardChatClient"/> refuses streaming for budget
    /// reasons; this link sits beneath it and would never see a streaming call.
    /// </summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken);

    /// <summary>Why this exception is worth repeating, or null if it is not.</summary>
    internal static string? Classify(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            // Never retry our own budget stops or a caller cancellation. Both mean "stop",
            // and repeating them would turn a bounded investigation into an unbounded one.
            if (ex is BudgetExhaustedException or OperationCanceledException)
            {
                return null;
            }

            if (ex is HttpRequestException http)
            {
                if (http.StatusCode is { } status && IsRetryableStatus(status))
                {
                    return $"http {(int)status}";
                }

                // No status at all is a transport failure - DNS, connection reset, TLS - and
                // is exactly what a retry is for.
                if (http.StatusCode is null)
                {
                    return "transport";
                }
            }
        }

        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex.Message is not { Length: > 0 } message)
            {
                continue;
            }

            foreach (var marker in RetryableMarkers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return $"message:{marker}";
                }
            }
        }

        return null;
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    /// <summary>
    /// <c>min(initial * base^(attempt-1) + U(0, jitter), max)</c>, the same shape the SDK
    /// documents, so the configured numbers mean what they say wherever retry ends up running.
    /// </summary>
    internal TimeSpan DelayFor(int attempt)
    {
        var seconds = options.InitialDelay.TotalSeconds * Math.Pow(options.ExpBase, attempt - 1)
            + (Random.Shared.NextDouble() * options.Jitter);

        return TimeSpan.FromSeconds(Math.Min(seconds, options.MaxDelay.TotalSeconds));
    }
}
