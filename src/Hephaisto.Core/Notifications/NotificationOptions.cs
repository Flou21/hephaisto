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

    /// <summary>
    /// Microsoft Teams through a registered bot. Spelled as the chart spells the value, because
    /// the same word is a routing key in a values file and a metric label here.
    /// </summary>
    public const string TeamsBot = "teamsBot";
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

    /// <summary>A name for the route, shown on every delivery it made. Unique when set.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Label matchers, all of which must hold (#141). The alerts arriving are already labelled
    /// for routing - by team, by product, by channel - and this is what reads that.
    /// </summary>
    public List<LabelMatcher> Matchers { get; set; } = [];

    /// <summary>Clusters this route owns. Empty means any.</summary>
    public List<string> Clusters { get; set; } = [];

    /// <summary>Signal kinds this route owns. Empty means any.</summary>
    public List<SignalKind> Kinds { get; set; } = [];

    /// <summary>
    /// Who this route tells, on a channel that tells people (#123). Empty means the channel's own
    /// list - the Teams bot's <c>Recipients</c> - which is how every route behaved before.
    /// </summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>
    /// A fallback owns an incident no scoped route owns. Without one, a routing table scoped by
    /// label tells nobody about an alert nobody labelled - quietly (#141).
    /// </summary>
    public bool Fallback { get; set; }

    /// <summary>
    /// What happens when nobody answers, in order (#142). Each step fires once per outage, when
    /// the incident has been open - since it opened or last reopened - for its
    /// <see cref="NotificationStep.After"/> and is still unacknowledged. An acknowledgement stops
    /// every step that has not fired.
    /// </summary>
    public List<NotificationStep> Steps { get; set; } = [];

    /// <summary>Whether this route owns only some incidents.</summary>
    public bool IsScoped => Namespaces.Count > 0 || Matchers.Count > 0 || Clusters.Count > 0 || Kinds.Count > 0;
}

/// <summary>One step of a route's escalation: after how long, to whom (#142).</summary>
public sealed class NotificationStep
{
    /// <summary>How long after the incident opened, or last reopened, the step fires.</summary>
    public TimeSpan After { get; set; }

    /// <summary>The least severity the step fires for. Info, the default, excludes nothing.</summary>
    public Severity MinSeverity { get; set; } = Severity.Info;

    /// <summary>The channel it tells on. Empty means the route's own.</summary>
    public string? Channel { get; set; }

    /// <summary>Who it tells. Empty with <see cref="ToAssignee"/> false means the route's recipients.</summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>Tell whoever the incident is assigned to, as well.</summary>
    public bool ToAssignee { get; set; }
}

/// <summary>A label and the values it may have. It matches when the label is present with one of them.</summary>
public sealed class LabelMatcher
{
    public string Label { get; set; } = string.Empty;

    public List<string> Values { get; set; } = [];
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
    /// an unauthenticated receiver accepts anything that can reach it, which is why
    /// Hephaisto's own inbound webhook takes a bearer token too (<c>Web:WebhookToken</c>).
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
/// Microsoft Teams, through a registered bot speaking the Bot Connector REST API.
/// </summary>
/// <remarks>
/// <para>
/// A different thing from <see cref="TeamsChannelOptions"/>, and it coexists with it. A Workflows
/// trigger can post and nothing else; a bot can <b>edit what it posted</b>, which is what makes
/// one living board possible instead of a card per event.
/// </para>
/// <para>
/// <b>It never deletes.</b> Teams leaves "This message has been deleted." behind for a deleted
/// channel post and for a deleted reply alike - measured against a real tenant on 2026-09-28 -
/// so a closed incident is removed by editing the board, and an alert that is over is edited into
/// its final state and left.
/// </para>
/// </remarks>
public sealed class TeamsBotOptions
{
    /// <summary>The Microsoft Entra tenant the bot is registered in. Single-tenant, always.</summary>
    public string? TenantId { get; set; }

    /// <summary>The bot's Microsoft App ID. Not a secret: it is printed in the app manifest.</summary>
    public string? AppId { get; set; }

    /// <summary>The app registration's client secret. From a Secret, never a Helm value.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The channel holding the board, as <c>19:...@thread.tacv2</c>.</summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// The team's Microsoft Entra group id. Optional: it is only what a link from an alert to the
    /// board is built from, and an alert without that button is thinner, not broken.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// Work email addresses that receive alerts as a personal chat message from the bot.
    /// </summary>
    /// <remarks>
    /// A personal chat rather than the channel, because a channel post cannot be taken back and
    /// an edit notifies nobody. Matched against the team's member list, which the bot may read
    /// because it is installed there - so an address has to belong to a member, and no directory
    /// permission is involved.
    /// </remarks>
    public List<string> Recipients { get; set; } = [];

    /// <summary>
    /// Where the Bot Connector lives. The global endpoint for the commercial cloud; overridden
    /// by the e2e harness, which stands in for it.
    /// </summary>
    public string ServiceUrl { get; set; } = "https://smba.trafficmanager.net/teams";

    /// <summary>Where tokens come from. Overridden by the e2e harness for the same reason.</summary>
    public string LoginUrl { get; set; } = "https://login.microsoftonline.com";

    /// <summary>
    /// How many incidents the board lists before it says "and N more". Twenty measured at about
    /// 58 KB and forty at 114 KB were both accepted, above either limit Microsoft documents - so
    /// this is a readability ceiling, deliberately well inside a limit that is not written down.
    /// </summary>
    public int BoardMaxIncidents { get; set; } = 20;

    /// <summary>How often the board and the live alerts are compared with the incidents they show.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Buttons on an alert that act rather than link. Off unless turned on.</summary>
    public TeamsBotActionsOptions Actions { get; set; } = new();

    /// <summary>Everything a request needs. Anything less and the channel is not registered at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(ChannelId);
}

/// <summary>
/// The one route Microsoft calls: a click on an alert's Acknowledge or Assign-to-me button.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and served only on its own port.</b> A button that acts is an
/// <c>Action.Execute</c>, and Teams delivers the click by POSTing an invoke activity to the bot's
/// messaging endpoint - so turning this on means a route this process answers for Microsoft,
/// authenticated by a Bot Framework token rather than by the console's identity provider. On a
/// port of its own nothing else is reachable through whatever exposes it, and a NetworkPolicy can
/// open that port without opening the console.
/// </para>
/// <para>
/// <b>Acknowledge and assign-to-me only.</b> Both are read-level acts in the console already.
/// Closing, approving and denying stay links: the click arrives as a Microsoft Entra identity,
/// and the approver role lives in whatever <c>Auth:Authority</c> names (backlog #124).
/// </para>
/// </remarks>
public sealed class TeamsBotActionsOptions
{
    /// <summary>Whether the route exists and the buttons are drawn. Both or neither.</summary>
    public bool Enabled { get; set; }

    /// <summary>The port <c>POST /api/teams/messages</c> answers on, and the only thing that does.</summary>
    public int Port { get; set; } = 8082;

    /// <summary>
    /// Where the Bot Framework's signing keys are described. Microsoft's, unless a test harness
    /// stands in for it; deliberately not a chart value, for the reason <c>LoginUrl</c> is not.
    /// </summary>
    public string OpenIdMetadataUrl { get; set; } = "https://login.botframework.com/v1/.well-known/openidconfiguration";

    /// <summary>
    /// Whether that document must be fetched over https. Only the stand-in, which serves http
    /// inside the cluster, turns this off.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>The issuer a Bot Framework token for a channel carries.</summary>
    public string Issuer { get; set; } = "https://api.botframework.com";

    /// <summary>The channel a signing key must be endorsed for.</summary>
    public string Channel { get; set; } = "msteams";

    /// <summary>
    /// How long "this person is in the team" is believed before the roster is read again. Someone
    /// removed from the team can still click for at most this long.
    /// </summary>
    public TimeSpan MembershipCacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Outbound delivery configuration.
/// </summary>
/// <remarks>
/// <para>
/// Bound through <c>IOptionsMonitor</c> for consistency with the rest of the options in this
/// codebase, but <b>it does not hot-reload in the shipped chart</b> and nothing should be
/// designed as though it does. Every setting here arrives as an environment variable on the pod
/// spec - unlike the kill switch, which is a projected ConfigMap volume precisely so it can
/// change without a roll - so there is no file for the monitor to watch, and a
/// <c>helm upgrade</c> that changes one of these replaces the pod anyway.
/// </para>
/// </remarks>
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

    public TeamsBotOptions TeamsBot { get; set; } = new();

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

        if (TeamsBot.IsConfigured)
        {
            yield return NotificationChannelNames.TeamsBot;
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

    /// <summary>How often the escalation steps are checked (#142).</summary>
    public TimeSpan UnansweredInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Rows per tick. Bounded so one backlog cannot monopolise a scope.</summary>
    public int DispatchBatchSize { get; set; } = 20;

    /// <summary>
    /// Per-call timeout. Short on purpose: the outbox is the retry authority, so an individual
    /// attempt should give up quickly and let the schedule decide when to try again.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
