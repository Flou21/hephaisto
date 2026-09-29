namespace Hephaisto.Agent.Mcp.Results;

/// <summary>
/// A result record as somebody might add one and forget the rule: a plain string. Lives in the
/// results namespace so McpAnswer's resolver treats it as one of its own - UntrustedTextTests
/// proves the call then fails instead of handing the text to a model as the server's words.
/// </summary>
internal sealed class Leaky
{
    public string Message { get; init; } = string.Empty;
}
