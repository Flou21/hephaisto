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

/// <summary>What close_incidents did, or would do (#161).</summary>
public sealed record BulkCloseAnswer
{
    [ServerAuthored]
    public required string Done { get; init; }

    /// <summary>True when nothing closed: this is the count and a sample, to confirm with.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Open incidents the filters match.</summary>
    public required int Matched { get; init; }

    public required int Closed { get; init; }

    /// <summary>The filters as they were understood.</summary>
    [ServerAuthored]
    public required string Filters { get; init; }

    public required McpText RecordedAs { get; init; }

    public McpText? ClaimedBy { get; init; }

    public required bool ClaimVerified { get; init; }

    /// <summary>The oldest that match, on a dry run.</summary>
    public IReadOnlyList<IncidentRow> Sample { get; init; } = [];

    [ServerAuthored]
    public string? Note { get; init; }
}
