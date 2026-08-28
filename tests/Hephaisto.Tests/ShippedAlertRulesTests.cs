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
}
