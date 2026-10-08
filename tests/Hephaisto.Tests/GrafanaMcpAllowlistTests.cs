using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests;

/// <summary>
/// The allowlist names only tools the server this repo runs was SEEN to offer.
/// </summary>
/// <remarks>
/// <para>
/// <c>GrafanaOptions.AllowedTools</c> is a list of strings, and nothing ties a string to a
/// server. For four releases it held four Tempo names no server here offered; after
/// production's Grafana MCP server was upgraded it held five, and production ran every
/// investigation without traces and without its alert rules. Nothing was red: an absent tool is
/// a Degraded row and a warning, by design, because the tool set really does vary by install.
/// </para>
/// <para>
/// What can be held is the DEFAULT against the server the dev stack pins - which is the chart
/// version production runs. <c>infra/observability/grafana-mcp.tools.txt</c> is that server's
/// <c>tools/list</c>, recorded; these tests fail when the default names a tool that is not in
/// it, and when the pin moves without the recording moving with it.
/// </para>
/// </remarks>
public partial class GrafanaMcpAllowlistTests
{
    [GeneratedRegex(@"^#\s+chart:\s+grafana-community/grafana-mcp\s+(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RecordedChart();

    [GeneratedRegex(@"'grafana-community/grafana-mcp',.*?'--version',\s*'([^']+)'", RegexOptions.Singleline)]
    private static partial Regex TiltfilePin();

    [GeneratedRegex(@"grafana-community/grafana-mcp\s*\\\s*\n\s*--version\s+(\S+)")]
    private static partial Regex E2ePin();

    [Fact]
    public void Every_default_tool_is_one_the_recorded_server_offers()
    {
        var offered = RecordedTools();

        new GrafanaOptions().AllowedTools
            .Where(name => !offered.Contains(name))
            .Should().BeEmpty(
                "a name the server does not offer is never handed to the model, and the only "
                + "sign is a Degraded row. Read the name from tools/list "
                + "(infra/observability/grafana-mcp.tools.txt says how), do not guess it");
    }

    [Fact]
    public void No_tool_is_on_the_default_list_twice()
    {
        // The status row counts "N of M" from this list, and the model would be handed the
        // same declaration twice.
        new GrafanaOptions().AllowedTools.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_recording_is_of_the_chart_version_both_installs_pin()
    {
        var recorded = RecordedChart().Match(File.ReadAllText(RecordingPath()));
        var tiltfile = TiltfilePin().Match(File.ReadAllText(Path.Combine(RepoRoot(), "Tiltfile")));
        var e2e = E2ePin().Match(
            File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "e2e", "lib", "deps.sh")));

        recorded.Success.Should().BeTrue("the recording's header names the chart it was taken from");
        tiltfile.Success.Should().BeTrue("the Tiltfile pins grafana-mcp with --version");
        e2e.Success.Should().BeTrue("scripts/e2e/lib/deps.sh pins grafana-mcp with --version");

        tiltfile.Groups[1].Value.Should().Be(
            recorded.Groups[1].Value,
            "moving the pin means recording tools/list again: a new server renames tools, and "
            + "the allowlist is checked against the recording, not against the server");

        e2e.Groups[1].Value.Should().Be(
            tiltfile.Groups[1].Value,
            "the e2e install and the dev stack read the same values file, and a category flag "
            + "one server knows is exit 2 on another");
    }

    [Fact]
    public void The_recording_holds_no_tool_the_values_file_switches_off()
    {
        // Not a property of the allowlist: of the recording. `grafana_api_request` is in the
        // category `api`, which production and this stack disable; a recording that holds it
        // was taken from a server started some other way and proves nothing about this one.
        RecordedTools().Should().NotContain("grafana_api_request");
    }

    [Fact]
    public void A_configured_name_is_added_to_the_list_and_takes_nothing_off_it()
    {
        // What the changelog and the options reference tell an operator, held here: .NET
        // configuration appends to a list that has a default. So an install can ADD a tool its
        // server has, and cannot make a server that lacks one of the defaults read Healthy.
        // If this ever fails, the binder has changed and both texts are wrong.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Grafana:AllowedTools:0"] = "a_tool_of_some_other_server",
            })
            .Build();

        var options = new GrafanaOptions();
        var defaults = options.AllowedTools.ToArray();

        configuration.GetSection(GrafanaOptions.SectionName).Bind(options);

        options.AllowedTools.Should().Equal([.. defaults, "a_tool_of_some_other_server"]);
    }

    private static HashSet<string> RecordedTools() =>
        File.ReadAllLines(RecordingPath())
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string RecordingPath() =>
        Path.Combine(RepoRoot(), "infra", "observability", "grafana-mcp.tools.txt");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        return dir!.FullName;
    }
}
