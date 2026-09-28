namespace Hephaisto.Agent.Options;

/// <summary>
/// Which port the webhook answers on, and the token it may require.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves (backlog #108).</b> The console, the JSON API and
/// <c>/webhooks/alertmanager</c> all answered on one port, and the agent has no authentication of
/// its own. The webhook was not authenticated either - the code believed Alertmanager had no field
/// for a credential, which was wrong (<see cref="WebhookToken"/>, backlog #138) - so a
/// NetworkPolicy was its entire protection. Sharing a port therefore forced a
/// choice between two bad options on every install: leave the console unreachable except by
/// port-forward, or widen the policy for the console and admit forged alerts with it.
/// </para>
/// <para>
/// On the first production cluster that cost a real outage of the console: the ingress controller
/// runs <c>hostNetwork</c>, so its packets carry the node's address and no <c>namespaceSelector</c>
/// can match them, and the NetworkPolicy had to be disabled entirely to get a dashboard.
/// </para>
/// <para>
/// With the webhook on its own port a NetworkPolicy can protect exactly that port while the
/// console is exposed by any ordinary means.
/// </para>
/// </remarks>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>
    /// The port <c>/webhooks/*</c> is served on, or <c>0</c> to keep everything on one port.
    /// </summary>
    /// <remarks>
    /// <b>Zero is the default and means the previous behaviour.</b> Splitting only helps when the
    /// deployment actually binds two ports and puts a policy in front of one of them; anywhere
    /// else - a developer running <c>dotnet run</c>, the eval harness, a single-port install that
    /// has not been updated - a second port that nothing listens on would make the webhook
    /// unreachable and every alert would vanish silently. So the split is opt-in and the chart
    /// opts in.
    /// </remarks>
    public int WebhookPort { get; set; }

    /// <summary>
    /// The port everything else answers on. Only consulted when <see cref="WebhookPort"/> is set.
    /// </summary>
    /// <remarks>
    /// Stated rather than inferred from the listener addresses: Kestrel may be bound to several,
    /// and a split that guessed wrong would silently serve the console on the port the
    /// NetworkPolicy is guarding.
    /// </remarks>
    public int MainPort { get; set; } = 8080;

    /// <summary>Whether the split is actually in effect.</summary>
    public bool WebhookPortIsSeparate => WebhookPort > 0 && WebhookPort != MainPort;

    /// <summary>
    /// Honour <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> from the proxy in front of the console.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed as soon as TLS ends at an ingress and OIDC is on. Without it the agent sees plain
    /// http behind the proxy, sends the IdP <c>redirect_uri=http://…/signin-oidc</c> (which a client
    /// registered for https rejects), and issues its correlation cookies without Secure, which
    /// browsers drop on the way back - a login that loops.
    /// </para>
    /// <para>
    /// Off by default because it trusts the headers from any peer: only turn it on when the console
    /// is reachable solely through the proxy. Behind a proxy that sets them, a client-supplied header
    /// is overwritten, not trusted.
    /// </para>
    /// </remarks>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// The bearer token <c>/webhooks/*</c> requires, or empty to require none. Chart value
    /// <c>secrets.webhookToken</c>, a Secret with key <c>token</c>.
    /// </summary>
    /// <remarks>
    /// Alertmanager sends it with <c>http_config.authorization.credentials_file</c> on the
    /// receiver. Set both in one change: a token the receiver does not send refuses every alert.
    /// At least <see cref="Web.WebhookTokenFilter.MinimumLength"/> characters, or the agent
    /// refuses to start. See <see cref="Web.WebhookTokenFilter"/>.
    /// </remarks>
    public string? WebhookToken { get; set; }
}
