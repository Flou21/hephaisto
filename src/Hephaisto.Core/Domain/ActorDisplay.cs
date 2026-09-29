namespace Hephaisto.Core.Domain;

/// <summary>
/// How an actor is shown to a person, where it might be an agent rather than a person (#157).
/// </summary>
/// <remarks>
/// A shared MCP token acts as <c>mcp/&lt;name&gt;</c>. Shown as that string, "acknowledged by
/// mcp/litellm" makes people ask whose; shown as the name the model gave, it would say a person
/// did something nobody checked they did. So: an agent, the token, and the claim - marked as one.
/// </remarks>
public static class ActorDisplay
{
    public const string AgentPrefix = "mcp/";

    public static bool IsAgent(string? actor) =>
        actor is not null && actor.StartsWith(AgentPrefix, StringComparison.Ordinal);

    public static string? Render(string? actor, string? claimedBy = null)
    {
        if (!IsAgent(actor))
        {
            return actor;
        }

        var token = actor![AgentPrefix.Length..];

        return string.IsNullOrWhiteSpace(claimedBy)
            ? $"an agent ({token})"
            : $"an agent ({token}), for {claimedBy} - unverified";
    }
}
