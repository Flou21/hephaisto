namespace Hephaisto.Core.Domain;

/// <summary>
/// Append-only. The application's database role holds INSERT but not UPDATE or DELETE on
/// this table, so immutability is enforced by Postgres rather than by convention.
/// </summary>
/// <remarks>
/// "No audit, no action" is a hard invariant: if this table cannot be written, the executor
/// refuses to act. That is the deliberate fail-safe direction - an agent that acts without
/// leaving a record is strictly worse than one that does nothing.
/// </remarks>
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset At { get; set; }

    /// <summary>e.g. <c>signal.accepted</c>, <c>policy.decided</c>, <c>action.executed</c>, <c>mode.changed</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public Guid? IncidentId { get; set; }

    public Guid? InvestigationId { get; set; }

    public Guid? ActionId { get; set; }

    /// <summary>Who or what caused this. <c>hephaisto/auto</c>, <c>hephaisto/system</c>, or a person's name.</summary>
    public string Actor { get; set; } = "hephaisto/system";

    public string Summary { get; set; } = string.Empty;

    /// <summary>Full structured detail as jsonb - for policy decisions, the complete input and every reason.</summary>
    public string? Detail { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }
}

/// <summary>
/// A human's verdict on an incident. The only honest false-positive rate available -
/// every other quality number the agent reports is self-assessed.
/// </summary>
public sealed class HumanFeedback
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid IncidentId { get; set; }

    public Incident? Incident { get; set; }

    /// <summary>True = the diagnosis was useful and correct.</summary>
    public bool Helpful { get; set; }

    /// <summary>Separate from <see cref="Helpful"/>: a useful investigation can still name the wrong cause.</summary>
    public bool? RootCauseCorrect { get; set; }

    /// <summary>True when the incident should never have opened. Feeds the false-positive metric.</summary>
    public bool FalsePositive { get; set; }

    public string? Comment { get; set; }

    public string SubmittedBy { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}
