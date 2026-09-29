using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hephaisto.Agent.Web;

/// <summary>
/// Where a change came from when it was not a person at the console or a click in Teams: an agent
/// through the MCP endpoint (#157). Written into the audit row's detail as <c>origin</c>.
/// </summary>
/// <remarks>
/// The audit row's actor is who is answerable: the person a person token names, or the token
/// itself (<c>mcp/&lt;name&gt;</c>) for a shared one. <see cref="ClaimedBy"/> is who a model said it
/// acted for - kept, shown, and never believed: <see cref="ClaimVerified"/> is false whenever it
/// is set, because nothing checked it.
/// </remarks>
public sealed record AuditOrigin
{
    public const string Mcp = "mcp";

    public required string Source { get; init; }

    public string? Token { get; init; }

    public string? TokenKind { get; init; }

    public string? Role { get; init; }

    /// <summary>What the client said it was (its User-Agent). Unverified by nature.</summary>
    public string? Client { get; init; }

    public string? ClaimedBy { get; init; }

    public bool ClaimVerified { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>An audit detail with the origin added, or unchanged when there is none.</summary>
    internal static string Attach(string detail, AuditOrigin? origin)
    {
        if (origin is null)
        {
            return detail;
        }

        var node = JsonNode.Parse(detail) as JsonObject ?? [];
        node["origin"] = JsonSerializer.SerializeToNode(origin, Json);

        return node.ToJsonString(Json);
    }
}
