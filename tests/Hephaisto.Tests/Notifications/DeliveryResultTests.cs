using System.Net;
using Hephaisto.Agent.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// Whether trying again could ever help. One implementation, so two channels cannot disagree.
/// </summary>
/// <remarks>
/// Getting this wrong is expensive in both directions. Treating a permanent rejection as
/// retryable burns the attempt budget and fills the backlog with rows that will never go,
/// hiding the ones that would. Treating a transient failure as permanent throws away the
/// message during exactly the outage the outbox exists for.
/// </remarks>
public sealed class DeliveryResultTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public void Any_2xx_is_delivered(HttpStatusCode status)
    {
        // Power Automate answers 202 to a Workflows trigger, so "200 only" would fail every
        // Teams delivery while reporting a transport problem.
        DeliveryResult.FromStatus(status, null).Disposition.Should().Be(DeliveryDisposition.Delivered);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Any_5xx_is_worth_retrying(HttpStatusCode status)
    {
        DeliveryResult.FromStatus(status, "upstream is down").Disposition
            .Should().Be(DeliveryDisposition.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void The_two_transient_4xx_are_worth_retrying(HttpStatusCode status)
    {
        // "You are going too fast" and "you took too long" are statements about this moment,
        // not about the request - which the rest of the 4xx range is not.
        DeliveryResult.FromStatus(status, null).Disposition.Should().Be(DeliveryDisposition.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public void Every_other_4xx_is_permanent(HttpStatusCode status)
    {
        // A rejected payload or a revoked credential is not transient, and retrying one until
        // the budget runs out buries the deliveries that would have worked.
        DeliveryResult.FromStatus(status, "nope").Disposition.Should().Be(DeliveryDisposition.Permanent);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    public void A_redirect_is_permanent_because_the_url_is_wrong(HttpStatusCode status)
    {
        // Redirects are not followed, so this means the configured URL is not the endpoint -
        // and it will still not be the endpoint in thirty minutes.
        DeliveryResult.FromStatus(status, null).Disposition.Should().Be(DeliveryDisposition.Permanent);
    }

    [Fact]
    public void The_endpoints_own_words_are_kept_for_the_row_and_the_span()
    {
        var result = DeliveryResult.FromStatus(HttpStatusCode.Forbidden, "token expired");

        result.Detail.Should().Contain("403").And.Contain("token expired");
    }

    [Fact]
    public void A_success_carries_no_detail_to_store()
    {
        DeliveryResult.FromStatus(HttpStatusCode.OK, "ignored").Detail.Should().BeNull();
    }
}
