using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Notifications;

/// <summary>
/// The channel names, shared so a routing rule and a registration cannot drift apart - the same
/// argument <c>HephaistoTelemetry</c> makes about metric names.
/// </summary>
public static class NotificationChannelNames
{
    public const string Webhook = "webhook";
    public const string Teams = "teams";
}

/// <summary>
/// One rule: which events, at which severity, in which namespaces, go to which channel.
/// </summary>
/// <remarks>
/// A route is additive and never subtractive - there is no "deny" rule. Two routes naming the
/// same channel produce one delivery, not two. Subtractive rules are how a routing table becomes
/// something nobody can reason about, and the thing being routed here is the message that says
/// the agent needs help.
/// </remarks>
public sealed class NotificationRoute
{
    /// <summary>
    /// The channel name this rule sends to. Must match a registered channel; a route naming a
    /// channel that is not configured is a startup validation failure rather than a silent
    /// no-op, because a routing rule that matches nothing looks exactly like one that works.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Which events this rule carries. Empty means <b>none</b>, not all - the same direction
    /// every default in this project points, and the reason a stock install notifies nowhere.
    /// </summary>
    public List<NotificationEvent> Events { get; set; } = [];

    /// <summary>Minimum severity, inclusive. Defaults to <c>Info</c>, which excludes nothing.</summary>
    public Severity MinSeverity { get; set; } = Severity.Info;

    /// <summary>
    /// Namespaces this rule applies to. Empty means "not scoped by namespace", which is the
    /// only way a rule can carry <see cref="NotificationEvent.ModeChanged"/> or
    /// <see cref="NotificationEvent.PolicyChanged"/> - those are about the agent and have no
    /// namespace to match.
    /// </summary>
    public List<string> Namespaces { get; set; } = [];
}

/// <summary>
/// The generic outbound HTTP channel: a URL, and optionally a secret to sign with.
/// </summary>
/// <remarks>
/// Called the <b>generic outbound HTTP channel</b> rather than "the webhook channel", because in
/// this repository a webhook is something Alertmanager posts INTO Hephaisto - the
/// <c>/webhooks</c> route group, the NetworkPolicy that is its only authentication. Reusing the
/// word for the opposite direction is how a security discussion ends up about the wrong thing.
/// </remarks>
public sealed class HttpChannelOptions
{
    /// <summary>Where to POST. Absent means the channel is not registered at all.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Shared secret for the <c>X-Hephaisto-Signature</c> HMAC. Optional, and worth setting:
    /// Hephaisto's own inbound webhook cannot be authenticated at all - Alertmanager has no
    /// field for a header - and is protected only by a NetworkPolicy. A receiver of ours does
    /// not have to accept that trade.
    /// </summary>
    public string? SigningSecret { get; set; }
}

/// <summary>Microsoft Teams, through a Power Automate Workflows trigger.</summary>
public sealed class TeamsChannelOptions
{
    /// <summary>
    /// The Workflows trigger URL. <b>A bearer credential in a query string</b>, so it comes from
    /// a Secret, is never a Helm value, and is never logged.
    /// </summary>
    public string? WorkflowUrl { get; set; }
}

/// <summary>
/// Outbound delivery configuration. Bound via <c>IOptionsMonitor</c> so it hot-reloads from the
/// ConfigMap, like <c>PolicyOptions</c>.
/// </summary>
/// <remarks>
/// <b>Every default here is off or conservative.</b> <see cref="Routes"/> is empty, so a stock
/// install delivers nothing, matching <c>Policy:AllowedNamespaces</c> and <c>mode: Observe</c>.
/// Turning notifications on is a reviewed commit.
/// </remarks>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// The externally reachable base URL of this Hephaisto, used to build the incident links a
    /// message carries. The pod cannot discover this - it knows the address it binds, not the
    /// one a person reaches it on - so it is required whenever any channel is enabled, and
    /// validated at startup rather than discovered as a card full of dead links.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Empty by default. See the remarks on this class.</summary>
    public List<NotificationRoute> Routes { get; set; } = [];

    public HttpChannelOptions Webhook { get; set; } = new();

    public TeamsChannelOptions Teams { get; set; } = new();

    /// <summary>
    /// Grafana's external base URL, used to put a "look at the graphs" link beside the
    /// diagnosis. Optional - a message without it is thinner, not broken.
    /// </summary>
    public string? GrafanaUrl { get; set; }

    /// <summary>The channels that are actually configured, and therefore routable.</summary>
    /// <remarks>
    /// Used by startup validation, so a route naming a channel nobody configured is refused
    /// rather than discovered the first time something escalates and reaches nobody.
    /// </remarks>
    public IEnumerable<string> ConfiguredChannels()
    {
        if (!string.IsNullOrWhiteSpace(Webhook.Url))
        {
            yield return NotificationChannelNames.Webhook;
        }

        if (!string.IsNullOrWhiteSpace(Teams.WorkflowUrl))
        {
            yield return NotificationChannelNames.Teams;
        }
    }

    /// <summary>
    /// Ceiling on deliveries per channel per hour. A notifier inherits none of ingest's dedup,
    /// flap suppression or storm breaker, so it needs its own.
    /// </summary>
    public int MaxPerChannelPerHour { get; set; } = 60;

    /// <summary>
    /// After a delivery for a correlation key, further deliveries for that key are suppressed
    /// for this long. The FIRST one always goes out - a cooldown that swallowed the opening
    /// message would be a worse failure than the storm it prevents.
    /// </summary>
    public TimeSpan CorrelationCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many times a retryable failure is retried before the row is marked failed.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Base of the exponential backoff.</summary>
    public TimeSpan FirstRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling on the backoff, so a long outage retries steadily rather than never.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the dispatcher looks for due rows.</summary>
    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Rows per tick. Bounded so one backlog cannot monopolise a scope.</summary>
    public int DispatchBatchSize { get; set; } = 20;

    /// <summary>
    /// Per-call timeout. Short on purpose: the outbox is the retry authority, so an individual
    /// attempt should give up quickly and let the schedule decide when to try again.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
