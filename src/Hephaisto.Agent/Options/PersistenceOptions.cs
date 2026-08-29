namespace Hephaisto.Agent.Options;

/// <summary>
/// Everything the persistence layer needs that is an operational choice rather than a
/// schema fact. Bound from the <c>Persistence</c> section.
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>
    /// Fallback for when no <c>ConnectionStrings:hephaisto</c> entry exists. Kept as an
    /// option rather than read directly so a test host can set it without a config file.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Named connection string checked first. The Aspire AppHost injects one under this
    /// name, so local dev needs no <c>Persistence:ConnectionString</c> at all.
    /// </summary>
    public string ConnectionStringName { get; set; } = "hephaisto";

    /// <summary>
    /// The connection the agent SERVES on, as a role that cannot rewrite <c>audit_events</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ConnectionStringName"/> stays the owner: it is what migrations run as, and
    /// what creates and grants this role. Postgres privileges cannot restrain a table's owner -
    /// it can always grant itself back - so the only way "no audit, no action" survives a
    /// compromised process is for the serving connection not to be the owner.
    /// </para>
    /// <para>
    /// Absent, the agent serves as the owner and says so loudly at startup. That is the
    /// pre-existing behaviour and it keeps an upgrade from failing closed on a Secret that does
    /// not carry the new key yet - but it is not the supported configuration.
    /// </para>
    /// </remarks>
    public string AppConnectionStringName { get; set; } = "hephaisto_app";

    /// <summary>
    /// Deliberately off. Migrating from the agent pod means every replica races the same
    /// DDL on boot, and a failed migration takes the agent down with it; migration is a
    /// separate Job that runs to completion before the Deployment rolls.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retention is asymmetric on purpose - see <c>RetentionService</c>. Blobs are ~1 MB
    /// each and expire; digests are ~2 KB and never do.
    /// </summary>
    public TimeSpan EvidenceBlobRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// LLM usage rows only need to outlive the longest budget window (24 h) and the
    /// runaway backstop's rolling 24 h, but keeping a month makes cost review possible.
    /// </summary>
    public TimeSpan LlmUsageRetention { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows deleted per statement. Bounded so a sweep never holds a long write lock.</summary>
    public int RetentionBatchSize { get; set; } = 1_000;

    /// <summary>
    /// How many times action admission retries a Postgres serialization failure before
    /// refusing. Refusing is the correct end state: under contention the safe answer to
    /// "may I change the cluster" is no.
    /// </summary>
    public int MaxAdmissionRetries { get; set; } = 3;

    /// <summary>
    /// How many candidates each retrieval arm pulls before fusion, as a multiple of the
    /// requested limit. Post-filtering happens after fusion, so a pool the same size as
    /// the limit would return almost nothing once a namespace filter is applied.
    /// </summary>
    public int SearchPoolFactor { get; set; } = 8;

    public int SearchMinPool { get; set; } = 100;
}
