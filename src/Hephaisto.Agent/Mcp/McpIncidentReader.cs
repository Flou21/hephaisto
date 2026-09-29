using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Web;
using Hephaisto.ServiceDefaults;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// Everything the MCP tools may read, and nothing they may not.
/// </summary>
/// <remarks>
/// The tools depend on this and on <see cref="McpIncidentActions"/>, never on
/// <see cref="IncidentQueries"/> or <see cref="CodeFixCoordinator"/> directly: those also approve
/// actions, decide code-fix plans and re-arm the mode, and a tool that could reach them is one
/// careless line from being a door that must stay shut. A unit test holds the tools to these two.
/// </remarks>
public sealed class McpIncidentReader(
    IncidentQueries incidents,
    CodeFixQueries codeFixes,
    ConnectionHealthCache connections)
{
    /// <summary>The agent's status as <c>/api/status</c> has it, with the connections and the build.</summary>
    public async Task<McpStatus> StatusAsync(CancellationToken ct)
    {
        var status = await incidents.GetStatusAsync(ct).ConfigureAwait(false);
        var counts = await codeFixes.CountsAsync(ct).ConfigureAwait(false);

        return new McpStatus(
            status,
            counts,
            [.. connections.Current.Select(c => new McpConnection(c.Name, c.State, c.Detail, c.CheckedAt))],
            BuildInfo.Version,
            BuildInfo.Commit);
    }
}

public sealed record McpConnection(string Name, ConnectionState State, string Detail, DateTimeOffset CheckedAt);

public sealed record McpStatus(
    AgentStatusView Status,
    CodeFixCounts CodeFixes,
    IReadOnlyList<McpConnection> Connections,
    string Version,
    string Commit);
