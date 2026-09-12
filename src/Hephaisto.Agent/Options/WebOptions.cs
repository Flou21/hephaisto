namespace Hephaisto.Agent.Options;

/// <summary>
/// Which port the unauthenticated webhook answers on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves (backlog #108).</b> The console, the JSON API and
/// <c>/webhooks/alertmanager</c> all answered on one port, and the agent has no authentication of
/// its own. The webhook cannot be authenticated either - Alertmanager has no field for a
/// credential - so a NetworkPolicy is its entire protection. Sharing a port therefore forced a
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
}
