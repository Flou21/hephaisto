using System.Net;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// What counts as worth asking again.
/// </summary>
/// <remarks>
/// On 2026-08-28 every one of nine faulted investigations carried the provider's own overload
/// wording and none was retried, discarding a complete run each time. These pin both arms of
/// the classifier and, just as importantly, the exceptions that must never be retried.
/// </remarks>
public class TransientRetryTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Retryable_status_codes_are_retried(HttpStatusCode status)
    {
        var ex = new HttpRequestException("boom", null, status);

        TransientRetryChatClient.Classify(ex).Should().NotBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void Client_errors_are_not_retried(HttpStatusCode status)
    {
        // A malformed request or a bad key fails identically forever. Retrying turns one
        // clear error into five slow ones.
        var ex = new HttpRequestException("boom", null, status);

        TransientRetryChatClient.Classify(ex).Should().BeNull();
    }

    [Theory]
    // The exact wording observed on the development cluster on 2026-08-31, retried five times
    // per step on every step of every investigation until the run stalled. docs/backlog.md #54.
    [InlineData("Your prepayment credits are depleted. Please go to AI Studio at "
        + "https://ai.studio/projects to manage your project and billing.")]
    [InlineData("API key not valid. Please pass a valid API key.")]
    [InlineData("Gemini API has not been used in project 12345 before or it is disabled.")]
    // The same condition as the first case, in each new provider's own words. A cheaper
    // provider does not make running out of credit any more retryable.
    [InlineData("Error code: 402 - {'error': {'message': 'Insufficient Balance', "
        + "'type': 'unknown_error'}}")]
    [InlineData("Insufficient credits. Add more at https://openrouter.ai/settings/credits")]
    [InlineData("Incorrect API key provided: sk-or-v1********. You can find your API key at "
        + "https://openrouter.ai/keys")]
    public void A_permanent_provider_failure_is_not_retried(string message)
    {
        // These arrive with NO HTTP status, which is what makes them dangerous: the transport
        // branch treats a missing status as a connection-level failure - right for DNS and TLS
        // and resets, wrong for a billing page a human has to visit. Refusing here is not
        // about saving the calls, which fail instantly; it is so the log says the real thing.
        TransientRetryChatClient.Classify(new HttpRequestException(message))
            .Should().BeNull();
    }

    [Fact]
    public void A_permanent_cause_overrules_a_retryable_status_it_arrives_with()
    {
        // Permanent markers are checked before the status, so this has to be deliberate rather
        // than incidental - and it is the reason the marker list is kept narrow. A phrase vague
        // enough to appear in a genuine 503 would turn a real hiccup into a hard stop, which is
        // the worse of the two mistakes.
        var ex = new HttpRequestException(
            "Your prepayment credits are depleted.", null, HttpStatusCode.ServiceUnavailable);

        TransientRetryChatClient.Classify(ex).Should().BeNull();
    }

    [Theory]
    // The other arm, and the one that matters more: wording that merely mentions money or keys
    // must NOT be swept up. Each of these is a transient condition a retry is exactly for.
    [InlineData("The model is overloaded. Please try again later.")]
    [InlineData("Resource has been exhausted (e.g. check quota).")]
    [InlineData("Rate limit exceeded for this project's billing tier")]
    // Mentions credits, and is not a billing failure: a request larger than the remaining
    // balance is retryable the moment a shorter turn fits. The permanent markers have to be
    // narrow enough to let this through.
    [InlineData("This request requires more credits than your remaining balance affords.")]
    public void Transient_wording_survives_the_permanent_check(string message)
    {
        TransientRetryChatClient.Classify(new HttpRequestException(message))
            .Should().NotBeNull();
    }

    /// <summary>
    /// A router with every upstream busy reads like a configuration error and is not one.
    /// </summary>
    /// <remarks>
    /// Given a 404 deliberately: that status is not retryable, so this passes only if the
    /// prose marker is doing the work. "unavailable" does not match it - OpenRouter's phrase
    /// is "no instances available", which contains "available" and not its negation.
    /// </remarks>
    [Fact]
    public void An_exhausted_upstream_pool_is_retried_despite_an_unretryable_status()
    {
        var ex = new HttpRequestException(
            "No instances available for openai/gpt-oss-120b",
            null,
            HttpStatusCode.NotFound);

        TransientRetryChatClient.Classify(ex).Should().NotBeNull();
    }

    [Fact]
    public void A_transport_failure_with_no_status_is_retried()
    {
        TransientRetryChatClient.Classify(new HttpRequestException("connection reset"))
            .Should().NotBeNull();
    }

    [Fact]
    public void The_overload_message_is_retried_even_without_a_status()
    {
        // The exact production failure: the provider throws with its own prose and the status
        // does not survive onto the exception.
        var ex = new InvalidOperationException(
            "This model is currently experiencing high demand. Spikes in demand are usually "
            + "temporary. Please try again later.");

        TransientRetryChatClient.Classify(ex).Should().NotBeNull();
    }

    [Fact]
    public void A_retryable_cause_is_found_through_the_inner_exception_chain()
    {
        var ex = new InvalidOperationException(
            "wrapped", new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable));

        TransientRetryChatClient.Classify(ex).Should().NotBeNull();
    }

    [Fact]
    public void Budget_exhaustion_is_never_retried()
    {
        // Retrying a budget stop would make a bounded investigation unbounded - the single
        // most expensive thing this class could get wrong.
        var ex = new BudgetExhaustedException(TerminationReason.StepBudgetExhausted, "12 steps");

        TransientRetryChatClient.Classify(ex).Should().BeNull();
    }

    [Fact]
    public void Cancellation_is_never_retried()
    {
        TransientRetryChatClient.Classify(new OperationCanceledException()).Should().BeNull();
    }

    [Fact]
    public void A_budget_stop_wrapped_in_something_retryable_still_wins()
    {
        // Order matters: the status arm runs over the whole chain first, so a budget stop has
        // to be found before any retryable marker on an outer frame.
        var ex = new InvalidOperationException(
            "the model is overloaded",
            new BudgetExhaustedException(TerminationReason.CostBudgetExhausted, "$0.50"));

        TransientRetryChatClient.Classify(ex).Should().BeNull();
    }

    [Fact]
    public void An_ordinary_bug_is_not_retried()
    {
        TransientRetryChatClient.Classify(new NullReferenceException()).Should().BeNull();
    }
}
