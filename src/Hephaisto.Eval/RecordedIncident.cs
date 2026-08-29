using Hephaisto.Core.Domain;

namespace Hephaisto.Eval;

/// <summary>
/// The incident a cassette was recorded from, in exactly the detail the system prompt renders.
/// </summary>
/// <remarks>
/// <para>
/// A cassette replaces the cluster, not the incident. The incident card is the second section of
/// every system prompt - title, kind, severity, timestamps, target, owner, node, quarantine and
/// the full signal list - and reconstructing an approximation of it would mean every experiment
/// was measured against a prompt that never existed. So the fields the card reads are recorded,
/// and nothing else is.
/// </para>
/// <para>
/// <b>This type is a deliberate mirror of <c>PromptComposer.ComposeIncidentCard</c>, and the
/// round-trip test is what keeps it one.</b> That test composes the card from a full incident and
/// from a recorded-then-rebuilt one and asserts the two strings are identical, so a new field in
/// the card fails here rather than silently thinning every cassette on disk.
/// </para>
/// <para>
/// The fields it does <i>not</i> carry are the point: no fingerprint, no labels, no raw payload,
/// no ids beyond the incident's own. None of them reach the model, and a fixture should hold the
/// smallest thing that reproduces the prompt.
/// </para>
/// </remarks>
public sealed record RecordedIncident
{
    public required string Title { get; init; }

    public required SignalKind Kind { get; init; }

    public required Severity Severity { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public required DateTimeOffset LastSignalAt { get; init; }

    /// <summary>Set when the incident was quarantined for oscillating; the card says so.</summary>
    public DateTimeOffset? QuarantinedUntil { get; init; }

    public required RecordedTarget Target { get; init; }

    public IReadOnlyList<RecordedSignal> Signals { get; init; } = [];

    public static RecordedIncident From(Incident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        return new RecordedIncident
        {
            Title = incident.Title,
            Kind = incident.Kind,
            Severity = incident.Severity,
            OpenedAt = incident.OpenedAt,
            LastSignalAt = incident.LastSignalAt,
            QuarantinedUntil = incident.QuarantinedUntil,
            Target = new RecordedTarget
            {
                Namespace = incident.Target.Namespace,
                Kind = incident.Target.Kind,
                Name = incident.Target.Name,
                OwnerKind = incident.Target.OwnerKind,
                OwnerName = incident.Target.OwnerName,
                NodeName = incident.Target.NodeName,
            },
            Signals =
            [
                .. incident.Signals.Select(s => new RecordedSignal
                {
                    Source = s.Source,
                    Reason = s.Reason,
                    Message = s.Message,
                    FirstSeen = s.FirstSeen,
                    LastSeen = s.LastSeen,
                    Count = s.Count,
                })
            ],
        };
    }

    /// <summary>Rebuilds an incident the prompt composer cannot tell from the original.</summary>
    /// <remarks>
    /// The id is new on every rebuild rather than the recorded one: it never reaches the prompt,
    /// and a fresh one keeps two concurrent replays of the same cassette from colliding in the
    /// investigation tracker, which is keyed by incident.
    /// </remarks>
    public Incident ToIncident()
    {
        var incident = new Incident
        {
            Title = Title,
            Kind = Kind,
            Severity = Severity,
            OpenedAt = OpenedAt,
            LastSignalAt = LastSignalAt,
            QuarantinedUntil = QuarantinedUntil,
            Target = new TargetRef
            {
                Namespace = Target.Namespace,
                Kind = Target.Kind,
                Name = Target.Name,
                OwnerKind = Target.OwnerKind,
                OwnerName = Target.OwnerName,
                NodeName = Target.NodeName,
            },
        };

        foreach (var signal in Signals)
        {
            incident.Signals.Add(new Signal
            {
                Source = signal.Source,
                Kind = Kind,
                Reason = signal.Reason,
                Message = signal.Message,
                FirstSeen = signal.FirstSeen,
                LastSeen = signal.LastSeen,
                Count = signal.Count,
            });
        }

        return incident;
    }
}

/// <summary>
/// The target fields the incident card renders. <c>WorkloadKey</c> is absent because it is
/// derived from the owner and would be a second, drifting definition of the same rule.
/// </summary>
public sealed record RecordedTarget
{
    public required string Namespace { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? OwnerKind { get; init; }

    public string? OwnerName { get; init; }

    public string? NodeName { get; init; }
}

/// <summary>One signal line of the incident card.</summary>
public sealed record RecordedSignal
{
    public required SignalSource Source { get; init; }

    public required string Reason { get; init; }

    public required string Message { get; init; }

    public required DateTimeOffset FirstSeen { get; init; }

    public required DateTimeOffset LastSeen { get; init; }

    public int Count { get; init; } = 1;
}
