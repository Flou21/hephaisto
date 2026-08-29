using Hephaisto.Eval;
using static Hephaisto.Tests.Eval.EvalScaffolding;

namespace Hephaisto.Tests.Eval;

/// <summary>
/// Routing a replayed tool back to the seam it was recorded from.
/// </summary>
/// <remarks>
/// The runner takes Kubernetes tools by injection and Grafana tools by fetching them through
/// <c>IGrafanaToolProvider</c>. Replay therefore has to split a cassette's declarations the same
/// way; a tool put on the wrong side, or dropped, would be a tool the model is never offered, and
/// every recorded call to it would come back as a miss that looks like the agent changed
/// its mind.
/// </remarks>
public class ReplayRoutingTests
{
    private static ToolDeclaration Tool(string name, string server) => new()
    {
        Name = name,
        Description = $"the {name} tool",
        Server = server,
        Schema = Schema("""{"type":"object","properties":{}}"""),
    };

    private static Cassette Mixed(params ToolDeclaration[] tools) => new()
    {
        Id = "c1",
        Description = "mixed surface",
        ExpectedRootCause = "something",
        Tools = tools,
        Calls = [],
    };

    [Fact]
    public async Task Each_tool_goes_back_to_the_seam_it_came_from()
    {
        var replay = new ReplayToolset(Mixed(
            Tool("get_events", ToolDeclaration.Kubernetes),
            Tool("list_nodes", ToolDeclaration.Kubernetes),
            Tool("query_loki_logs", ToolDeclaration.GrafanaMcp)));

        replay.FunctionsFor(ToolDeclaration.Kubernetes)
            .Select(f => f.Name).Should().BeEquivalentTo(["get_events", "list_nodes"]);

        var grafana = await new ReplayGrafanaToolProvider(replay).GetToolsAsync(TestContext.Current.CancellationToken);

        grafana.Select(f => f.Name).Should().BeEquivalentTo(["query_loki_logs"]);
    }

    [Fact]
    public void The_split_is_total_so_nothing_is_silently_dropped()
    {
        var replay = new ReplayToolset(Mixed(
            Tool("get_events", ToolDeclaration.Kubernetes),
            Tool("query_loki_logs", ToolDeclaration.GrafanaMcp)));

        var routed = replay.FunctionsFor(ToolDeclaration.Kubernetes).Count
            + replay.FunctionsFor(ToolDeclaration.GrafanaMcp).Count;

        routed.Should().Be(replay.Functions.Count);
    }

    [Fact]
    public void A_server_with_nowhere_to_route_is_visible_rather_than_silent()
    {
        // There is no third seam on the runner, so a declaration from anywhere else cannot be
        // offered. Servers is what lets `run` say so instead of reporting a mystery miss rate.
        var replay = new ReplayToolset(Mixed(
            Tool("get_events", ToolDeclaration.Kubernetes),
            Tool("conclude", "internal")));

        replay.Servers.Should().BeEquivalentTo(["internal", "kubernetes"]);
        replay.FunctionsFor("internal").Should().ContainSingle();
    }

    [Fact]
    public void An_absent_server_yields_no_tools_rather_than_throwing()
    {
        // The ordinary case: most cassettes are Kubernetes-only, and asking for the Grafana half
        // of one is not an error.
        new ReplayToolset(Mixed(Tool("get_events", ToolDeclaration.Kubernetes)))
            .FunctionsFor(ToolDeclaration.GrafanaMcp).Should().BeEmpty();
    }
}
