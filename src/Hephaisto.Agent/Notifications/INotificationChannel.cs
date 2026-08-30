using System.Net;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Notifications;

/// <summary>How an attempt ended, and whether trying again could ever help.</summary>
public enum DeliveryDisposition
{
    Delivered = 0,

    /// <summary>Worth another go later: a timeout, a 5xx, a 429.</summary>
    Retryable = 1,

    /// <summary>
    /// Trying again cannot help. A rejected payload or a revoked credential is not a transient
    /// condition, and retrying a 400 until the attempt budget runs out is how an outbox becomes
    /// a landfill - while the backlog hides the deliveries that would have worked.
    /// </summary>
    Permanent = 2,
}

/// <param name="Detail">
/// What the endpoint said, for the row and the span. Never a metric label: this is somebody
/// else's free text, and it is exactly the shape of value that produced backlog #12.
/// </param>
public readonly record struct DeliveryResult(DeliveryDisposition Disposition, string? Detail)
{
    public static DeliveryResult Ok() => new(DeliveryDisposition.Delivered, null);

    public static DeliveryResult Retry(string detail) => new(DeliveryDisposition.Retryable, detail);

    public static DeliveryResult Permanent(string detail) => new(DeliveryDisposition.Permanent, detail);

    /// <summary>
    /// The HTTP status classification, in one place so two channels cannot disagree about
    /// whether a 429 is worth retrying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>429 and 408 are retryable although they are 4xx.</b> "You are going too fast" and "you
    /// took too long" are statements about this moment, not about the request - which is the
    /// distinction the rest of the 4xx range does not have.
    /// </para>
    /// <para>
    /// <b>3xx is permanent.</b> The client does not follow redirects for these, so a redirect
    /// means the configured URL is not the endpoint - a misconfiguration that will still be a
    /// misconfiguration in thirty minutes.
    /// </para>
    /// </remarks>
    public static DeliveryResult FromStatus(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        var detail = string.IsNullOrWhiteSpace(body) ? $"HTTP {code}" : $"HTTP {code}: {body}";

        if (code is >= 200 and < 300)
        {
            return Ok();
        }

        if (code >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return Retry(detail);
        }

        return Permanent(detail);
    }
}

/// <summary>
/// One way of getting a message to a person.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>IGrafanaAnnotator</c>, which is the shape this codebase already uses for an
/// outbound side effect: conditional registration, a <c>Null*</c> implementation when
/// unconfigured, a per-call timeout, and a <c>Describe()</c> line at startup that says whether it
/// is on <i>and why not</i>.
/// </para>
/// <para>
/// <b>An implementation must not retry.</b> The outbox owns retry, because the outbox is the only
/// layer that survives a pod restart, and a channel that retried internally would multiply
/// against the schedule out here. It must not throw either - a failure is a
/// <see cref="DeliveryResult"/>, so that one bad endpoint cannot take down the loop that serves
/// the others.
/// </para>
/// </remarks>
public interface INotificationChannel
{
    /// <summary>
    /// Matches <c>Notifications:Routes[].Channel</c>, and is a metric label. A closed vocabulary
    /// in practice: the set of registered channels.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// One line at startup saying this channel is on, and where it points.
    /// </summary>
    /// <remarks>
    /// <b>Must never include the credential.</b> A Teams Workflows URL carries its bearer token
    /// in the query string, so the obvious implementation - printing the configured URL - writes
    /// a live credential into the pod log.
    /// </remarks>
    string Describe();

    Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct);
}
