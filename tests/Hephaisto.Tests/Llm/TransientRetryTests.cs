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
