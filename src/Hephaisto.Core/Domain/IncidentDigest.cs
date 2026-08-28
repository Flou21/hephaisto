namespace Hephaisto.Core.Domain;

/// <summary>
/// The compact, self-contained summary of a resolved incident that gets embedded and
/// indexed. Not the raw logs - those expire at 30 days while this is kept indefinitely,
/// so a digest has to still make sense long after the evidence behind it is gone.
/// </summary>
public sealed class IncidentDigest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid IncidentId { get; set; }

    public Incident? Incident { get; set; }

    /// <summary>Title, kind, workload, winning hypothesis, top evidence, actions, outcome.</summary>
    public string Digest { get; set; } = string.Empty;

    /// <summary>sha256 of <see cref="Digest"/>. Re-embed only when this changes.</summary>
    public string DigestHash { get; set; } = string.Empty;

    /// <summary>
    /// gemini-embedding-001 output, 768 dimensions. Mapped to pgvector with an HNSW
    /// cosine index; null until the embedding call succeeds, so a provider outage
    /// degrades search rather than blocking incident resolution.
    /// </summary>
    public float[]? Embedding { get; set; }

    // Denormalised for cheap post-filtering after the fusion step.
    public string Namespace { get; set; } = string.Empty;

    public string WorkloadKey { get; set; } = string.Empty;

    public SignalKind Kind { get; set; }

    public bool Resolved { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
