using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests;

/// <summary>
/// An allowlisted tool the server never registered must produce a message that says what the
/// investigation can no longer do.
/// </summary>
/// <remarks>
/// <para>
/// Backlog #31's cheap half. The absence itself is legitimate - grafana-mcp's tool set varies
/// with the datasources it was started with - so this is not about failing the startup. It is
/// about the log line being readable by the person who will otherwise spend a week wondering
/// why the model "never bothered to query traces".
/// </para>
/// <para>
/// The default allowlist carries four Tempo tools, and a server from before mcp-grafana had
/// Tempo tools of its own offers none of them, so this is a live configuration rather than a
/// hypothetical. Production met it from the other side on 2026-09-30: the server was upgraded,
/// the allowlist still held names it did not offer, and five went missing at once.
/// </para>
/// </remarks>
public class GrafanaMcpCapabilityLossTests
{
    [Fact]
    public void Absent_tempo_tools_are_described_as_losing_trace_correlation()
    {
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(
            ["search_tempo_traces", "get_tempo_trace", "list_tempo_attribute_names", "list_tempo_attribute_values"]);

        described.Should().Contain("trace");
    }

    [Fact]
    public void One_family_is_named_once_however_many_of_its_tools_are_absent()
    {
        // Four Tempo tools are one lost capability, not four. A message that repeats itself
        // four times is a message people learn to skip.
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(
            ["search_tempo_traces", "get_tempo_trace", "list_tempo_attribute_names", "list_tempo_attribute_values"]);

        described.Split("; nor ").Should().ContainSingle();
    }

    [Fact]
    public void Several_absent_families_are_all_named()
    {
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(
            ["search_tempo_traces", "query_loki_logs", "query_prometheus"]);

        described.Should().Contain("trace").And.Contain("logs").And.Contain("metrics");
    }

    [Fact]
    public void An_absent_alert_rules_tool_is_described_as_losing_the_alert_rules()
    {
        // The family is matched on "alert", not on one tool's name: the tool that reads rules
        // has been `list_alert_rules`, then `grafana_api_request` by way of a caveat, and is
        // `alerting_rules_read` now.
        GrafanaMcpToolProvider.DescribeLostCapabilities(["alerting_rules_read"])
            .Should().Contain("alert rules");
    }

    [Fact]
    public void Every_tool_on_the_default_allowlist_belongs_to_a_named_family()
    {
        // The generic clause ("use <name>") is for a tool somebody adds later. A tool that
        // ships on the list and still lands there means its loss would be reported by a name
        // nobody reading a log can act on.
        foreach (var tool in new GrafanaOptions().AllowedTools)
        {
            GrafanaMcpToolProvider.DescribeLostCapabilities([tool])
                .Should().NotStartWith("use ", because: $"{tool} is on the default allowlist");
        }
    }

    [Fact]
    public void A_tool_matching_no_known_family_is_still_named_rather_than_dropped()
    {
        // The failure this guards is a silent one: somebody adds an allowlist entry for a new
        // backend, it goes missing, and the warning says nothing was lost.
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(["query_something_new"]);

        described.Should().Contain("query_something_new");
    }

    [Fact]
    public void Nothing_absent_never_produces_an_empty_sentence()
    {
        GrafanaMcpToolProvider.DescribeLostCapabilities([]).Should().NotBeNullOrWhiteSpace();
    }
}
