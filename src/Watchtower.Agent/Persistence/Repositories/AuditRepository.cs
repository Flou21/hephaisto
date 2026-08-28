using Microsoft.EntityFrameworkCore;
using Watchtower.Core.Abstractions;
using Watchtower.Core.Domain;

namespace Watchtower.Agent.Persistence.Repositories;

public sealed class AuditRepository(WatchtowerDbContext db, IClock clock) : IAuditRepository
{
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        Enlist(auditEvent);
        await db.SaveChangesAsync(ct);
    }

    public async Task AppendAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken ct)
    {
        foreach (var auditEvent in auditEvents)
        {
            Enlist(auditEvent);
        }

        await db.SaveChangesAsync(ct);
    }

    public void Enlist(AuditEvent auditEvent)
    {
        // Stamped here rather than trusting the caller: an audit trail whose timestamps
        // come from whoever happened to construct the object sorts by nothing in particular.
        if (auditEvent.At == default)
        {
            auditEvent.At = clock.UtcNow;
        }

        db.AuditEvents.Add(auditEvent);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetForIncidentAsync(Guid incidentId, CancellationToken ct) =>
        await db.AuditEvents
            .AsNoTracking()
            .Where(a => a.IncidentId == incidentId)
            .OrderBy(a => a.At)
            .ToListAsync(ct);
}
