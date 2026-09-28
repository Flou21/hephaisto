using System.Security.Cryptography;
using System.Text;

namespace Hephaisto.Core.Fingerprinting;

/// <summary>
/// What an alert's label set says about which alert it is.
/// </summary>
/// <remarks>
/// <para>
/// Three questions, three answers, all pure functions over the labels:
/// </para>
/// <list type="bullet">
///   <item><see cref="AlertKey"/> - which alert instance, so a resolve finds the row it clears
///   (#129). Every label counts except the scrape's own.</item>
///   <item><see cref="IdentityHash"/> - for an alert that names no Kubernetes object, which of
///   the rule's series this is (#132). A rule that fires once per provider is one incident per
///   provider.</item>
///   <item><see cref="Distinguishing"/> - the labels a person needs in a title to tell two such
///   incidents apart.</item>
/// </list>
/// <para>
/// The scrape's own labels are left out of all three. They describe where a series was
/// collected, not what it is about, and they change underneath an alert that has not: an
/// exporter rescheduled to another node has a new <c>instance</c>, and an HA pair of Prometheus
/// servers sends the same alert under two <c>prometheus_replica</c> values.
/// </para>
/// </remarks>
public static class AlertIdentity
{
    /// <summary>
    /// Labels a series carries because of how it was scraped or evaluated.
    /// </summary>
    public static readonly IReadOnlySet<string> ScrapeLabels = new HashSet<string>(StringComparer.Ordinal)
    {
        "instance",
        "endpoint",
        "prometheus",
        "prometheus_replica",
        "__replica__",
        "receive",
        "tenant_id",
        "metrics_path",
        "scrape_job",
    };

    /// <summary>
    /// Labels that say how loud an alert is or where the agent routes it, not which fault it is.
    /// Out of the identity hash, so a warning that turns critical is still the same incident.
    /// </summary>
    private static readonly IReadOnlySet<string> NotIdentity = new HashSet<string>(StringComparer.Ordinal)
    {
        "alertname",
        "severity",
        "cluster",
        "namespace",
        "job",
    };

    /// <summary>
    /// Labels too generic or too internal to be worth a place in a title.
    /// </summary>
    private static readonly IReadOnlySet<string> NotInTitles = new HashSet<string>(StringComparer.Ordinal)
    {
        "container",
        "service",
        "team",
        "channel",
    };

    /// <summary>
    /// Which alert instance: sha256 over every label but the scrape's own and the agent's,
    /// sorted, hex.
    /// </summary>
    public static string AlertKey(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        return Hash(labels.Where(kv => !ScrapeLabels.Contains(kv.Key) && !IsAgentLabel(kv.Key)));
    }

    /// <summary>
    /// Which series of a rule, for an alert that names no Kubernetes object: the label set less
    /// the scrape's labels, the agent's, and the ones that are not identity (name, severity,
    /// cluster, namespace, scrape job).
    /// </summary>
    public static string IdentityHash(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        return Hash(IdentityLabels(labels));
    }

    /// <summary>
    /// The labels that tell two series of one rule apart, as <c>key=value</c>, sorted, capped.
    /// Empty when the rule has only one series.
    /// </summary>
    public static string Distinguishing(IReadOnlyDictionary<string, string> labels, int maxLength = 80)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var text = string.Join(", ", IdentityLabels(labels)
            .Where(kv => !NotInTitles.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// Whether the series came from kube-state-metrics, whose own pod name rides along on every
    /// series it exports in the <c>pod</c> label - which is not the pod the alert is about unless
    /// the alert is about a pod (#126).
    /// </summary>
    public static bool IsFromKubeStateMetrics(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        return Contains(labels, "job", "kube-state-metrics")
            || Contains(labels, "container", "kube-state-metrics")
            || Contains(labels, "service", "kube-state-metrics");

        static bool Contains(IReadOnlyDictionary<string, string> l, string key, string needle) =>
            l.TryGetValue(key, out var v) && v.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The agent's own annotations on a signal, which are not the alert's.</summary>
    public static bool IsAgentLabel(string key) =>
        key.StartsWith("hephaisto_", StringComparison.Ordinal);

    private static IEnumerable<KeyValuePair<string, string>> IdentityLabels(IReadOnlyDictionary<string, string> labels) =>
        labels.Where(kv =>
            !ScrapeLabels.Contains(kv.Key)
            && !NotIdentity.Contains(kv.Key)
            && !IsAgentLabel(kv.Key));

    private static string Hash(IEnumerable<KeyValuePair<string, string>> labels)
    {
        var sb = new StringBuilder();

        foreach (var (key, value) in labels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // Length-prefixed, so no two label sets flatten into the same string whatever their
            // values contain.
            sb.Append(key.Length).Append(':').Append(key).Append('=')
              .Append(value.Length).Append(':').Append(value).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
