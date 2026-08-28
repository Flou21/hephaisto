namespace Hephaisto.Core.Domain;

/// <summary>
/// One observation that something may be wrong. Signals are cheap and duplicated on
/// purpose; <see cref="Fingerprint"/> is what collapses them into an <see cref="Incident"/>.
/// </summary>
public sealed class Signal
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// sha256 over source, kind, cluster, namespace, owner and reason - never the pod name.
    /// Computed by <c>SignalFingerprinter</c>; see <see cref="TargetRef"/> for why.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public SignalSource Source { get; set; }

    public SignalKind Kind { get; set; }

    public TargetRef Target { get; set; } = new();

    public Severity Severity { get; set; }

    /// <summary>Short machine reason, e.g. the Kubernetes event reason or the alertname.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Human-readable message straight from the source. Untrusted text - see remarks on tool output.</summary>
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    /// <summary>How many raw observations this row represents after burst collapse.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Labels from Alertmanager or derived from the Kubernetes object. Stored as jsonb.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>The original payload, kept verbatim for the audit trail. Stored as jsonb.</summary>
    public string? RawPayload { get; set; }

    public Guid? IncidentId { get; set; }

    public Incident? Incident { get; set; }
}
