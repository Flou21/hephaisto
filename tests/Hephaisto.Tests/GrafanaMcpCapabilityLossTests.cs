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
/// The default allowlist carries four Tempo tools, and this repo's own grafana-mcp starts with
/// Tempo unconfigured, so this is the live configuration rather than a hypothetical.
/// </para>
/// </remarks>
public class GrafanaMcpCapabilityLossTests
{
    [Fact]
    public void Absent_tempo_tools_are_described_as_losing_trace_correlation()
    {
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(
            ["query_tempo_traces", "query_tempo_traceql", "list_tempo_tag_names", "list_tempo_tag_values"]);

        described.Should().Contain("trace");
    }

    [Fact]
    public void One_family_is_named_once_however_many_of_its_tools_are_absent()
    {
        // Four Tempo tools are one lost capability, not four. A message that repeats itself
        // four times is a message people learn to skip.
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(
            ["query_tempo_traces", "query_tempo_traceql", "list_tempo_tag_names", "list_tempo_tag_values"]);

        described.Split("; nor ").Should().ContainSingle();
    }

    [Fact]
    public void Several_absent_families_are_all_named()
    {
        var described = GrafanaMcpToolProvider.DescribeLostCapabilities(
            ["query_tempo_traces", "query_loki_logs", "query_prometheus"]);

        described.Should().Contain("trace").And.Contain("logs").And.Contain("metrics");
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
