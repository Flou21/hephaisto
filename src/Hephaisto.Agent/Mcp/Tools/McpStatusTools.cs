using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hephaisto.Agent.Mcp.Tools;

/// <summary>The agent's status, and who the caller is.</summary>
[McpServerToolType]
public sealed class McpStatusTools(McpIncidentReader reader)
{
    [McpServerTool(Name = "get_status", Title = "Agent status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the status and health of the Hephaisto agent: mode and effective mode, kill switch arms, runaway latch, budget utilization of tokens and cost, counts of open and escalated incidents, running and queued investigations, the health of its connections (Postgres, Kubernetes, Grafana, notification channels, identity provider), version and commit. Read only: nothing here sets a mode.")]
    public async Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        var s = await reader.StatusAsync(cancellationToken).ConfigureAwait(false);

        // Flat, with /api/status's own field names, so "is the mode what the API says" is a
        // comparison of two fields and not a translation.
        var node = JsonSerializer.SerializeToNode(s.Status, McpAnswer.Json)!.AsObject();
        node["codeFixes"] = JsonSerializer.SerializeToNode(s.CodeFixes, McpAnswer.Json);
        node["connections"] = JsonSerializer.SerializeToNode(s.Connections, McpAnswer.Json);
        node["version"] = s.Version;
        node["commit"] = s.Commit;

        return node.ToJsonString(McpAnswer.Json);
    }

    [McpServerTool(Name = "get_caller_identity", Title = "Who am I", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get who am I for this server (whoami): the identity of the token in use, whether it is a shared token or identifies a person, its role (reader or approver), its permissions, whether it may write, which tools it may call and how the word me is resolved for assigned to.")]
    public static string GetCallerIdentity(ClaimsPrincipal user)
    {
        var caller = McpCaller.From(user) ?? throw new McpException("This request did not come through the MCP endpoint's authentication.");

        return McpAnswer.Of(new
        {
            name = caller.Actor,
            token = caller.Token,
            kind = caller.Kind,
            role = caller.Role,
            mayWrite = caller.MayWrite,
            me = caller.Me,
            meMeans = caller.IsShared
                ? "nobody: this is a shared token, used for many people, so assignedTo: me is refused. Name the person instead."
                : $"{caller.Actor}: assignedTo: me finds the incidents assigned to {caller.Actor}.",
            writesRecordedAs = caller.Actor,
            tools = McpCatalogue.For(caller),
        });
    }
}
