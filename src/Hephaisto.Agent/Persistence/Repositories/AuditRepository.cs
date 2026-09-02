using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence.Repositories;

public sealed class AuditRepository(HephaistoDbContext db, IClock clock) : IAuditRepository
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

        // Same argument as the timestamp above, and it cost more. Detail is a jsonb column,
        // so a caller that assigns a human-readable sentence does not fail here - it fails in
        // Postgres, at SaveChanges, taking down whatever else was in that transaction. The
        // audit row is written in the SAME transaction as the state change it describes,
        // precisely so that "no audit, no action" holds, which means a malformed detail is not
        // a logging bug: it silently prevents the state change. #72 was that, for three
        // releases, and it presented as an incident that verified successfully and then never
        // closed.
        //
        // Wrapping rather than throwing. A detail that is not JSON is a caller's mistake, but
        // refusing to write the row would keep exactly the failure mode this exists to remove.
        // A JSON string is valid jsonb, so the text survives and is still queryable.
        if (auditEvent.Detail is { Length: > 0 } detail && !IsJson(detail))
        {
            auditEvent.Detail = JsonSerializer.Serialize(detail);
        }

        db.AuditEvents.Add(auditEvent);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> GetForIncidentAsync(Guid incidentId, CancellationToken ct) =>
        await db.AuditEvents
            .AsNoTracking()
            .Where(a => a.IncidentId == incidentId)
            .OrderBy(a => a.At)
            .ToListAsync(ct);
}
