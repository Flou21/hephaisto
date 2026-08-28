namespace Hephaisto.Core.Domain;

/// <summary>
/// The Kubernetes object a signal is about, resolved up to its owning controller.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OwnerKind"/> / <see cref="OwnerName"/> are the important fields. Pods are
/// cattle: a crash-looping Deployment produces a new pod name every couple of minutes, so
/// anything keyed on <see cref="Name"/> generates a fresh incident each time and the agent
/// never realises it is looking at one problem. Fingerprinting, correlation, cooldowns and
/// oscillation detection are all keyed on the owner instead.
/// </para>
/// </remarks>
public sealed class TargetRef
{
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Kind of the object the signal arrived about, e.g. <c>Pod</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The object's UID. Distinguishes a recreated object from the original one - a pod
    /// with the same name but a new UID is a restart, not the same pod still failing.
    /// </summary>
    public string? Uid { get; set; }

    /// <summary>Kind of the top-level controller, e.g. <c>Deployment</c>. Null for bare objects.</summary>
    public string? OwnerKind { get; set; }

    public string? OwnerName { get; set; }

    /// <summary>Node the object is scheduled on, when known. Used to absorb pod signals into node-level ones.</summary>
    public string? NodeName { get; set; }

    /// <summary>
    /// Stable identity for cooldowns, oscillation detection and blast-radius maths.
    /// Falls back to the object itself when it has no controller.
    /// </summary>
    public string WorkloadKey =>
        OwnerKind is { Length: > 0 } ok && OwnerName is { Length: > 0 } on
            ? $"{Namespace}/{ok}/{on}"
            : $"{Namespace}/{Kind}/{Name}";

    /// <summary>
    /// A field-for-field copy, for when the same target has to be attached to a second entity.
    /// </summary>
    /// <remarks>
    /// This exists for one specific reason and it is not defensive style. <see cref="TargetRef"/>
    /// is mapped as an EF Core <b>owned</b> type on both <c>Signal</c> and <c>Incident</c>.
    /// Owned instances belong to exactly one owner, so assigning one Signal's Target directly to
    /// the Incident opened from it hands a single CLR instance to two owners, and the change
    /// tracker throws on save:
    /// <code>
    /// The property 'IncidentId' belongs to the type 'Incident.Target#TargetRef',
    /// but is being used with an instance of type 'Signal.Target#TargetRef'.
    /// </code>
    /// The failure is at SaveChanges, far from the assignment, and it takes down ingest for
    /// every signal rather than just the one - so always copy, never share.
    /// </remarks>
    public TargetRef Clone() => new()
    {
        Namespace = Namespace,
        Kind = Kind,
        Name = Name,
        Uid = Uid,
        OwnerKind = OwnerKind,
        OwnerName = OwnerName,
        NodeName = NodeName,
    };

    public override string ToString() => $"{Namespace}/{Kind}/{Name}";
}
