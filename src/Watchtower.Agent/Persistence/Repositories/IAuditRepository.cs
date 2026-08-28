using Watchtower.Core.Domain;

namespace Watchtower.Agent.Persistence.Repositories;

/// <summary>
/// Insert-only by design. There is no update, no delete and no "correct a row" - the
/// database role backing this holds INSERT and nothing else, so an interface offering more
/// would only be offering calls that throw. A mistaken audit row is corrected by appending
/// the correction, exactly as it would be in a paper ledger.
/// </summary>
public interface IAuditRepository
{
    /// <summary>
    /// Writes immediately. Participates in an ambient transaction if one is open, which is
    /// what lets an action and the record of why it was allowed commit or fail together -
    /// "no audit, no action" is only a real invariant if the two are atomic.
    /// </summary>
    Task AppendAsync(AuditEvent auditEvent, CancellationToken ct);

    Task AppendAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken ct);

    /// <summary>
    /// Stages the row without writing. For a caller that is about to save other work in the
    /// same unit and wants the audit row in that same statement batch.
    /// </summary>
    void Enlist(AuditEvent auditEvent);

    Task<IReadOnlyList<AuditEvent>> GetForIncidentAsync(Guid incidentId, CancellationToken ct);
}
