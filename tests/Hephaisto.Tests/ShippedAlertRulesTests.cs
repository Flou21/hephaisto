using System.Text.RegularExpressions;
using Hephaisto.Core.Classification;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests;

/// <summary>
/// Reads the PrometheusRule files this repo actually ships and asserts the agent can
/// classify every alert in them.
/// </summary>
/// <remarks>
/// <para>
/// This test exists because the failure it catches already happened. The alert rules and the
/// classifier were written by different people at different times, and 27 of 34
/// <c>hephaisto_kind</c> labels were values like <c>PvcFillingUp</c> and
/// <c>ServiceErrorRate</c> - perfectly reasonable names that are not
/// <see cref="SignalKind"/> members. <c>Enum.TryParse</c> failed silently on every one of
/// them and the classifier fell through to guessing from the alertname, which for
/// <c>KubeContainerWaiting</c> yields <see cref="SignalKind.Unknown"/> and therefore the
/// default runbook instead of the image-pull one.
/// </para>
/// <para>
/// Nothing about that is visible from either side alone: the YAML looks well-labelled and the
/// classifier looks correct. Only reading the real files catches it, so this test does.
/// </para>
/// </remarks>
public class ShippedAlertRulesTests
{
    private static readonly Regex AlertLine = new(@"^\s*- alert:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex KindLine = new(@"^\s*hephaisto_kind:\s*(\S+)", RegexOptions.Compiled);

    public static TheoryData<string, string, string> ShippedAlerts()
    {
        var data = new TheoryData<string, string, string>();

        foreach (var file in Directory.EnumerateFiles(AlertsDirectory(), "*.yaml"))
        {
            string? alert = null;

            foreach (var line in File.ReadLines(file))
            {
                if (AlertLine.Match(line) is { Success: true } a)
                {
                    alert = a.Groups[1].Value;
                    continue;
                }

                if (alert is not null && KindLine.Match(line) is { Success: true } k)
                {
                    data.Add(Path.GetFileName(file), alert, k.Groups[1].Value);
                    alert = null;
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedAlerts))]
    public void Every_shipped_alert_classifies_to_a_real_kind(string file, string alertName, string kindLabel)
    {
        Enum.TryParse<SignalKind>(kindLabel, ignoreCase: true, out var declared)
            .Should().BeTrue(
                $"{file}: '{alertName}' declares hephaisto_kind '{kindLabel}', which is not a SignalKind "
                + "member. It parses as nothing, so the classifier silently falls back to guessing "
                + "from the alertname and the investigation gets the wrong runbook.");

        var labels = new Dictionary<string, string>
        {
            [AlertClassifier.KindLabel] = kindLabel,
            ["alertname"] = alertName,
        };

        AlertClassifier.Kind(alertName, labels).Should().Be(declared);
    }

    [Theory]
    [MemberData(nameof(ShippedAlerts))]
    public void No_shipped_alert_is_Unknown(string file, string alertName, string kindLabel)
    {
        var labels = new Dictionary<string, string> { [AlertClassifier.KindLabel] = kindLabel };

        AlertClassifier.Kind(alertName, labels).Should().NotBe(
            SignalKind.Unknown,
            $"{file}: '{alertName}' would be investigated with the default runbook rather than one "
            + "written for its failure mode.");
    }

    [Fact]
    public void The_alert_rule_files_are_where_this_test_thinks_they_are()
    {
        // Guards the guard: if the directory moves, the two theories above silently become
        // zero test cases and stop protecting anything.
        Directory.EnumerateFiles(AlertsDirectory(), "*.yaml").Should().NotBeEmpty();
        ShippedAlerts().Should().HaveCountGreaterThan(20);
    }

    private static string AlertsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        // The rules moved into the chart: they are the agent's INPUT, so they ship with it
        // rather than with this repo's own observability stack. One source of truth - the
        // Tiltfile applies these same files.
        return Path.Combine(dir!.FullName, "charts", "hephaisto", "files", "alerts");
    }

    /// <summary>
    /// Every namespace-shaped label in the shipped rules is one the ingest actually reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of backlog #33, and the half that stops it coming back. The fallback in
    /// <c>AlertmanagerEndpoints.ResolveTarget</c> is a fix; this is the thing that fails when
    /// somebody adds a rule grouping by a fourth spelling.
    /// </para>
    /// <para>
    /// An empty namespace is not cosmetic. It is part of the signal fingerprint, it is what
    /// <c>Policy:AllowedNamespaces</c> is checked against, it is what every tool call needs as
    /// an argument, and as of v0.3.0 it is what a notification route filters on - so a rule
    /// that labels it something unread produces an incident that can be neither acted on nor
    /// escalated to anybody. It cost two release candidates the first time, because the
    /// harness reported c10 as having opened no incident while the incident existed
    /// throughout.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_namespace_label_in_the_shipped_rules_is_one_the_ingest_reads()
    {
        // The three ResolveTarget falls back through, in order.
        string[] understood = ["namespace", "exported_namespace", "k8s_namespace_name"];

        // Any identifier that looks like it names a namespace. Deliberately broad: the point is
        // to catch a spelling nobody thought of, so a pattern that only matched known ones
        // would assert nothing.
        var candidate = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*namespace[A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(AlertsDirectory(), "*.yaml"))
        {
            foreach (var match in candidate.Matches(File.ReadAllText(file)).Cast<Match>())
            {
                var label = match.Groups[1].Value;

                // `namespace:` as a YAML key, and Prometheus's own metric names, are not label
                // names an alert would carry.
                if (understood.Contains(label, StringComparer.Ordinal)
                    || label.StartsWith("kube_", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {label}");
            }
        }

        offenders.Should().BeEmpty(
            "every namespace-shaped label in a shipped rule must be one AlertmanagerEndpoints "
            + "reads, or the incident it opens has no namespace and can be neither acted on "
            + "nor routed to anybody");
    }

    /// <summary>
    /// Every span-metrics aggregation groups by the namespace as well as the service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theory above catches a namespace label spelled a way the ingest cannot read. This
    /// catches the other half, which is backlog #104: a rule that spells it correctly
    /// everywhere and then aggregates it away. The three latency rules did that for four
    /// releases while the error-rate rules twenty lines above them did not, so reading either
    /// one in isolation looked right.
    /// </para>
    /// <para>
    /// Scoped to <c>traces_spanmetrics_*</c> deliberately. That is the family whose identity
    /// is the pair (service, namespace) - the observability self-check rules aggregate by
    /// <c>exporter</c> and <c>processor</c> and correctly have no namespace at all, so a
    /// blanket rule over every aggregation would assert something false.
    /// </para>
    /// <para>
    /// The consequence of getting it wrong is not cosmetic and not deferred: an empty
    /// namespace fails <c>Policy:AllowedNamespaces</c>, so every latency incident this repo
    /// could ever raise was un-actionable by construction.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_span_metrics_aggregation_groups_by_namespace_as_well_as_service()
    {
        // `sum by (<labels>) (` immediately preceding, or wrapping, a spanmetrics selector.
        var aggregation = new Regex(
            @"sum\s+by\s*\(([^)]*)\)\s*\(\s*(?:rate|increase|irate)?\(?\s*traces_spanmetrics_",
            RegexOptions.Compiled);

        string[] understood = ["namespace", "exported_namespace", "k8s_namespace_name"];

        var offenders = new List<string>();
        var checkedCount = 0;

        foreach (var file in Directory.EnumerateFiles(AlertsDirectory(), "*.yaml"))
        {
            var text = File.ReadAllText(file);

            foreach (var match in aggregation.Matches(text).Cast<Match>())
            {
                checkedCount++;

                var labels = match.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (!labels.Any(l => understood.Contains(l, StringComparer.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(file)}: sum by ({match.Groups[1].Value})");
                }
            }
        }

        // Guards the guard: a regex that matches nothing is a test that asserts nothing, and
        // this one is matching against a file it does not own.
        checkedCount.Should().BeGreaterThan(10,
            "the aggregation pattern no longer matches the shipped span-metrics rules, so this "
            + "test has silently stopped checking them");

        offenders.Should().BeEmpty(
            "a span-metrics rule that aggregates the namespace away produces an incident with "
            + "an empty namespace, which fails Policy:AllowedNamespaces, matches no "
            + "notification route, and gives every tool call an argument it cannot use");
    }
}
