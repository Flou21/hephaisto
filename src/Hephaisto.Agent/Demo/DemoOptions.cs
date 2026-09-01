namespace Hephaisto.Agent.Demo;

/// <summary>
/// The demo stack: a console with real recorded investigations in it and nothing behind it.
/// </summary>
/// <remarks>
/// <b>Off by default, and every field here is inert unless <see cref="Seed"/> is true.</b> This
/// exists so a stranger can look at the product before deciding whether to install it, which
/// previously required Kubernetes, Prometheus, Alertmanager, prometheus-operator, Postgres with
/// pgvector and a model API key. That is a reasonable production dependency list and an
/// unreasonable evaluation one.
/// </remarks>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>
    /// Whether to load <see cref="TranscriptPath"/> into an empty database at startup.
    /// </summary>
    /// <remarks>
    /// Seeding is refused on a database that already holds incidents, so this cannot overwrite
    /// a real installation's history even if it is set by accident. It is still not something
    /// to set on an agent that watches a cluster: seeded rows describe faults in somebody
    /// else's cluster, months ago.
    /// </remarks>
    public bool Seed { get; set; }

    /// <summary>
    /// Where the transcripts live. Relative paths resolve against the content root, which in
    /// the published image is where the <c>transcripts/</c> directory is copied to.
    /// </summary>
    public string TranscriptPath { get; set; } = "transcripts";
}
