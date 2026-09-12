namespace Hephaisto.Agent.Observability;

/// <summary>
/// How a dependency is doing, in the four states an operator actually needs to tell apart.
/// </summary>
/// <remarks>
/// <b>Three would not be enough, and two would be actively misleading.</b> "Not configured" is a
/// legitimate, deliberate choice all over this codebase - every outbound integration degrades
/// silently when it is unset, and that is the right behaviour per delivery. A panel that painted
/// those red would be wrong on a correct install, and a panel that is wrong on a correct install
/// is one people stop reading. Equally, <see cref="Degraded"/> has to be separate from
/// <see cref="Healthy"/>: grafana-mcp connected but missing its Tempo tools is backlog #31, and
/// it is the exact shape of failure that spent thirteen steps and produced nothing.
/// </remarks>
public enum ConnectionState
{
    /// <summary>Deliberately unset. Not a fault, and must not render as one.</summary>
    NotConfigured = 0,

    Healthy = 1,

    /// <summary>Reachable, but not able to do everything it is relied on for.</summary>
    Degraded = 2,

    /// <summary>Configured and not answering.</summary>
    Unreachable = 3,
}

/// <param name="Name">Stable identifier, used as the row key and the test hook.</param>
/// <param name="State">See <see cref="ConnectionState"/>.</param>
/// <param name="Detail">
/// One line a human can act on. Never a credential: several of these describe endpoints whose
/// URL carries its own bearer token, which is why the probes render a component's own
/// <c>Describe()</c> rather than the configured value.
/// </param>
/// <param name="CheckedAt">
/// When this answer was obtained. Rendered, because the whole defect being fixed here is that the
/// answers were computed once at startup and thrown into a log line - and "it worked at 09:00" is
/// not "it works".
/// </param>
public sealed record ConnectionReport(
    string Name,
    ConnectionState State,
    string Detail,
    DateTimeOffset CheckedAt)
{
    public static ConnectionReport NotConfigured(string name, string detail, DateTimeOffset at) =>
        new(name, ConnectionState.NotConfigured, detail, at);
}

/// <summary>One dependency that can be asked how it is doing.</summary>
/// <remarks>
/// <b>A probe must never throw and must never have a side effect the system would notice.</b> The
/// first because one broken dependency must not blank the whole panel - the panel is most needed
/// when something is broken. The second is why the notification channels are reported from
/// configuration alone: the only way to test a webhook is to post to it, and a health check that
/// pages somebody is not a health check.
/// </remarks>
public interface IConnectionProbe
{
    string Name { get; }

    Task<ConnectionReport> ProbeAsync(CancellationToken ct);
}
