namespace Hephaisto.Agent.Mcp.Answers;

/// <summary>What a write tool answers: what was done, and who it was recorded as.</summary>
public sealed record WriteResult
{
    [ServerAuthored]
    public required string Done { get; init; }

    /// <summary>The audit trail's actor: the token's person, or the token itself for a shared one.</summary>
    public required McpText RecordedAs { get; init; }

    /// <summary>Who the caller said it acted for. Stored and shown as a claim.</summary>
    public McpText? ClaimedBy { get; init; }

    public required bool ClaimVerified { get; init; }

    public IncidentRow? Incident { get; init; }

    [ServerAuthored]
    public string? Note { get; init; }
}
