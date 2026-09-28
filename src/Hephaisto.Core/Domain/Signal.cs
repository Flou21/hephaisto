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

    /// <summary>Firing or resolved, as the source last said. Backlog #129.</summary>
    public SignalStatus Status { get; set; }

    /// <summary>
    /// Which alert INSTANCE this is: a hash of the whole label set, less the scrape's own labels.
    /// Null for a signal that is not an alert.
    /// </summary>
    /// <remarks>
    /// Two keys, two questions. <see cref="Fingerprint"/> answers "which incident" and leaves the
    /// pod out on purpose; this one answers "what has cleared". Two pods of one Deployment firing
    /// the same rule are one incident and two alert instances, and the incident is not over until
    /// both have resolved. One row per alert instance, updated in place. See
    /// <c>AlertIdentity.AlertKey</c>.
    /// </remarks>
    public string? AlertKey { get; set; }

    /// <summary>Labels from Alertmanager or derived from the Kubernetes object. Stored as jsonb.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>
    /// An alert's annotations - summary, description, runbook_url and whatever else the rule's
    /// author wrote for a person. Stored as jsonb, and shown to the model as the rule's own words
    /// (#135). Empty for a signal that is not an alert.
    /// </summary>
    public Dictionary<string, string> Annotations { get; set; } = [];

    /// <summary>The original payload, kept verbatim for the audit trail. Stored as jsonb.</summary>
    public string? RawPayload { get; set; }

    public Guid? IncidentId { get; set; }

    public Incident? Incident { get; set; }
}
