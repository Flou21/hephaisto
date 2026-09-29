namespace Hephaisto.Tests.Mcp;

/// <summary>
/// A model behind an MCP gateway with tool search on never sees the tool list. It sees two
/// tools - search and call - and finds Hephaisto's by the words of its question. A tool the
/// search does not rank is a tool that does not exist for that model.
/// </summary>
/// <remarks>
/// The rows of <c>findability.tsv</c> are questions an on-call person asks, including the three
/// prompts the endpoint was built for. Each must put the expected tool in the gateway's default
/// top five - alone, and with other servers' tools registered first, which is where a gateway's
/// registry may well put them. The gateway tier (<c>mcp-litellm-local.sh</c>, L04) checks the
/// real gateway ranks the same way this scorer does.
/// </remarks>
public sealed class McpFindabilityTests
{
    public static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();

        foreach (var (query, tool) in McpSpecification.Findability())
        {
            data.Add(query, tool);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void The_gateway_finds_the_tool_for_the_question(string query, string tool)
    {
        var registry = McpSpecification.Golden().Select(t => ("hephaisto", t));

        McpSpecification.Search(query, registry).Should().Contain($"hephaisto-{tool}");
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void It_still_does_with_other_servers_listed_first(string query, string tool)
    {
        var registry = McpSpecification.Neighbours().Select(t => ("neighbours", t))
            .Concat(McpSpecification.Golden().Select(t => ("hephaisto", t)));

        McpSpecification.Search(query, registry).Should().Contain($"hephaisto-{tool}");
    }

    [Fact]
    public void There_are_enough_questions_and_every_one_names_a_real_tool()
    {
        var names = McpSpecification.Golden().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var rows = McpSpecification.Findability();

        rows.Should().HaveCountGreaterThanOrEqualTo(25);
        rows.Select(r => r.Tool).Should().OnlyContain(t => names.Contains(t));
        rows.Should().Contain(r => r.Query == "incident just happened check it")
            .And.Contain(r => r.Query == "latest open incidents grouped by severity")
            .And.Contain(r => r.Query == "my latest closed incidents");
    }

    [Fact]
    public void The_scorer_can_rank_a_tool_out()
    {
        // The guard is only worth something if a bad description fails it.
        var bland = new McpSpecification.Tool("get_thing", "reader", true, "Returns a thing.");
        var registry = Enumerable.Range(0, 6)
            .Select(i => ("other", new McpSpecification.Tool($"incident_{i}", "reader", true, "incident severity grouped latest open")))
            .Append(("hephaisto", bland));

        McpSpecification.Search("latest open incidents grouped by severity", registry)
            .Should().NotContain("hephaisto-get_thing");

        McpSpecification.Search("zzz", registry).Should().BeEmpty("a tool that matches nothing is not ranked at all");
    }
}
