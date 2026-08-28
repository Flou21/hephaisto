// The namespace is `Investigations`, plural, while the folder is `Investigation/`.
//
// `Watchtower.Core.Domain.Investigation` is the aggregate every file in here works with, and
// a namespace named `Watchtower.Agent.Investigation` would shadow it: inside that namespace
// the simple name `Investigation` binds to the namespace, not the type, and every reference
// to the domain object would need a full alias. Pluralising the namespace is cheaper than
// aliasing it in a dozen files.
namespace Watchtower.Agent.Investigations;

/// <summary>
/// The facts about <i>this</i> cluster that the model cannot discover from a tool, rendered
/// into the system prompt as the environment card.
/// </summary>
/// <remarks>
/// Kept out of <c>Prompts/*.md</c> on purpose. The prompt fragments are prose about how to
/// investigate and are the same everywhere; this is deployment configuration, and a
/// namespace list that lives in a markdown file is a namespace list that goes stale in a
/// file nobody thinks to update when they change a ConfigMap.
/// </remarks>
public sealed class EnvironmentCardOptions
{
    public const string SectionName = "Investigation:Environment";

    /// <summary>
    /// The <c>cluster</c> label value carried by every metric and log line here. Without it
    /// the model writes label matchers that return nothing and reads that as "no data".
    /// </summary>
    public string ClusterName { get; set; } = "studio-rancher-desktop";

    /// <summary>Namespaces the agent may investigate at all.</summary>
    public List<string> InScopeNamespaces { get; set; } = ["watchtower-chaos"];

    /// <summary>
    /// Namespaces that are permanently off limits. Stated to the model so it does not spend
    /// steps on things the policy engine would refuse anyway - the enforcement is in the
    /// policy engine and RBAC, not here.
    /// </summary>
    public List<string> ProtectedNamespaces { get; set; } = ["watchtower", "watchtower-obs", "kube-system"];

    /// <summary>Datasource name to uid, as grafana-mcp expects them.</summary>
    public Dictionary<string, string> DatasourceUids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Workload key to the human or team that owns it. The agent cannot page anyone, but
    /// naming the owner in a finding is what makes an escalation actionable rather than a
    /// notification.
    /// </summary>
    public Dictionary<string, string> WorkloadOwners { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Extra deployment-specific lines appended verbatim to the card.</summary>
    public List<string> Notes { get; set; } = [];
}
