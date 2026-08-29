using Microsoft.Extensions.AI;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Supplies the Grafana-side tools for an investigation.
/// </summary>
/// <remarks>
/// <para>
/// An interface over a single concrete implementation, which normally would not earn its keep.
/// It exists because <see cref="Investigations.InvestigationRunner"/> asks for these tools
/// itself rather than receiving them, so there is no seam from outside to wrap them at — and
/// the eval harness has to wrap <b>every</b> tool to record or replay a run. The Kubernetes
/// tools already arrive as an injected <c>IEnumerable&lt;AIFunction&gt;</c> and need no
/// equivalent.
/// </para>
/// <para>
/// A cassette that covered Kubernetes tools only would omit exactly the calls the accuracy work
/// is about: the Loki label discovery that spends the step budget.
/// </para>
/// </remarks>
public interface IGrafanaToolProvider
{
    /// <summary>
    /// The allowlisted tools, or an empty list when grafana-mcp is unconfigured or unreachable.
    /// Never throws: an investigation without metrics is degraded, not failed.
    /// </summary>
    Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct);
}
