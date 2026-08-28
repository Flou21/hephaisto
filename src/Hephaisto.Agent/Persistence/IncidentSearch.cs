using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Hephaisto.Agent.Options;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence;

/// <summary>Post-fusion narrowing. Applied after the fusion select, not inside either arm.</summary>
public sealed record SearchFilter
{
    public IReadOnlyList<string>? Namespaces { get; init; }

    public IReadOnlyList<SignalKind>? Kinds { get; init; }

    public string? WorkloadKey { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary>Only incidents that actually got resolved. What "how was this fixed before"
    /// really means.</summary>
    public bool ResolvedOnly { get; init; }
}

public sealed record IncidentSearchHit
{
    public required Guid DigestId { get; init; }

    public required Guid IncidentId { get; init; }

    public required string Digest { get; init; }

    public required string Namespace { get; init; }

    public required string WorkloadKey { get; init; }

    public required SignalKind Kind { get; init; }

    public required bool Resolved { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The fused RRF score. Comparable within one result set, meaningless across two.</summary>
    public required double Score { get; init; }

    /// <summary>Rank in the lexical arm, null if that arm did not return it.</summary>
    public int? LexicalRank { get; init; }

    public int? SemanticRank { get; init; }
}

/// <summary>
/// Hybrid retrieval over <see cref="IncidentDigest"/>: full-text and vector similarity, run
/// as one query and fused with Reciprocal Rank Fusion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hybrid rather than pure vector.</b> Vector search reliably misses exact
/// identifiers - an image tag, an error code, a workload name, a Kubernetes reason string -
/// and that is exactly what an SRE query is usually about. "CrashLoopBackOff on
/// payments-api after 1.24.3" is three literals and one concept; an embedding blurs all four
/// into a direction in space and happily returns a semantically similar incident about a
/// different service. The lexical arm anchors on the literals, the vector arm covers the
/// paraphrase ("pods keep dying after the deploy"), and neither alone is enough.
/// </para>
/// <para>
/// RRF with k=60 rather than a weighted score blend, because the two arms produce scores on
/// incomparable scales - ts_rank_cd and cosine distance have no exchange rate, and any
/// weighting between them is a number someone made up. Ranks are comparable by construction.
/// </para>
/// <para>
/// One query with a CTE per arm, not two round trips: the arms have to see the same snapshot,
/// and fusing in C# would mean shipping both candidate pools over the wire to throw most of
/// them away.
/// </para>
/// </remarks>
public sealed class IncidentSearch(
    HephaistoDbContext db,
    IOptionsMonitor<PersistenceOptions> options,
    ILogger<IncidentSearch> logger)
{
    /// <summary>
    /// The RRF constant. 60 is the value from the original Cormack et al. paper and the one
    /// every implementation since has used; it flattens the difference between rank 1 and
    /// rank 5 enough that one arm's confident-but-wrong top hit cannot dominate the fusion.
    /// </summary>
    private const int RrfK = 60;

    // The score expressions are cast to double precision explicitly: 1.0 is numeric in
    // Postgres, so the fused score would come back as a numeric and reading it as a double
    // would throw at the reader rather than at compile time.

    public async Task<IReadOnlyList<IncidentSearchHit>> SearchAsync(
        string query,
        float[]? queryEmbedding,
        SearchFilter filter,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) && queryEmbedding is null)
        {
            return [];
        }

        var o = options.CurrentValue;
        var pool = Math.Max(o.SearchMinPool, limit * o.SearchPoolFactor);

        var lexical = !string.IsNullOrWhiteSpace(query);
        var semantic = queryEmbedding is { Length: > 0 };

        if (!semantic)
        {
            // The embedding provider is down or the digest predates embedding. Degrading to
            // lexical-only is the whole reason the arms are separate CTEs: search that
            // returns worse results is useful, search that throws is not.
            logger.LogDebug("Searching lexically only: no query embedding available");
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = false;

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            await using var cmd = new NpgsqlCommand(BuildSql(lexical, semantic, filter), connection);
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

            if (lexical)
            {
                cmd.Parameters.AddWithValue("q", query);
            }

            if (semantic)
            {
                // Pgvector's Npgsql plugin, registered by UseVector() in
                // AddHephaistoPersistence, is what makes a Vector a legal parameter value.
                cmd.Parameters.AddWithValue("qvec", new Vector(queryEmbedding!));
            }

            cmd.Parameters.AddWithValue("pool", pool);
            cmd.Parameters.AddWithValue("lim", limit);

            AddFilterParameters(cmd, filter);

            var hits = new List<IncidentSearchHit>(limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                hits.Add(new IncidentSearchHit
                {
                    DigestId = reader.GetGuid(0),
                    IncidentId = reader.GetGuid(1),
                    Digest = reader.GetString(2),
                    Namespace = reader.GetString(3),
                    WorkloadKey = reader.GetString(4),
                    Kind = Enum.TryParse<SignalKind>(reader.GetString(5), out var kind) ? kind : SignalKind.Unknown,
                    Resolved = reader.GetBoolean(6),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
                    Score = reader.GetDouble(8),
                    LexicalRank = reader.IsDBNull(9) ? null : (int)reader.GetInt64(9),
                    SemanticRank = reader.IsDBNull(10) ? null : (int)reader.GetInt64(10),
                });
            }

            return hits;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string BuildSql(bool lexical, bool semantic, SearchFilter filter)
    {
        var sql = new StringBuilder();

        sql.Append("WITH ");

        if (lexical)
        {
            // websearch_to_tsquery, not plainto_tsquery: it understands quoted phrases and
            // OR, which is how someone actually types an error string into a search box.
            // The ORDER BY is repeated outside the window function on purpose: Postgres
            // computes window functions before LIMIT, so without it the pool would be an
            // arbitrary slice of the matches that happened to be ranked, not the top ones.
            sql.Append("""
                lex AS (
                    SELECT d.id,
                           ROW_NUMBER() OVER (ORDER BY ts_rank_cd(d.tsv, q.query) DESC, d.created_at DESC) AS rnk
                    FROM incident_digests d, websearch_to_tsquery('english', @q) AS q(query)
                    WHERE d.tsv @@ q.query
                    ORDER BY ts_rank_cd(d.tsv, q.query) DESC, d.created_at DESC
                    LIMIT @pool
                )
                """);
        }

        if (lexical && semantic)
        {
            sql.Append(", ");
        }

        if (semantic)
        {
            // The inner select is ORDER BY ... LIMIT and nothing else, which is the only
            // shape the HNSW index can serve. Wrapping the ranking in a window function on
            // the outside keeps the index scan intact; ranking inside would force a sort
            // over every digest ever written.
            sql.Append("""
                sem AS (
                    SELECT id, ROW_NUMBER() OVER () AS rnk
                    FROM (
                        SELECT id
                        FROM incident_digests
                        WHERE embedding IS NOT NULL
                        ORDER BY embedding <=> @qvec
                        LIMIT @pool
                    ) ranked
                )
                """);
        }

        sql.Append(", fused AS (");

        if (lexical && semantic)
        {
            sql.Append($"""

                SELECT COALESCE(lex.id, sem.id) AS id,
                       (COALESCE(1.0 / ({RrfK} + lex.rnk), 0.0)
                        + COALESCE(1.0 / ({RrfK} + sem.rnk), 0.0))::double precision AS score,
                       lex.rnk AS lex_rank,
                       sem.rnk AS sem_rank
                FROM lex FULL OUTER JOIN sem ON sem.id = lex.id
                """);
        }
        else if (lexical)
        {
            sql.Append($"""

                SELECT lex.id, (1.0 / ({RrfK} + lex.rnk))::double precision AS score,
                       lex.rnk AS lex_rank, NULL::bigint AS sem_rank
                FROM lex
                """);
        }
        else
        {
            sql.Append($"""

                SELECT sem.id, (1.0 / ({RrfK} + sem.rnk))::double precision AS score,
                       NULL::bigint AS lex_rank, sem.rnk AS sem_rank
                FROM sem
                """);
        }

        sql.Append("""
            )
            SELECT d.id, d.incident_id, d.digest, d.namespace, d.workload_key, d.kind, d.resolved,
                   d.created_at, f.score, f.lex_rank, f.sem_rank
            FROM fused f
            JOIN incident_digests d ON d.id = f.id
            """);

        // Post-filters, appended only when set. Building the predicate rather than passing
        // NULLs and writing (@ns IS NULL OR ...) keeps the planner able to use the plain
        // btree indexes on namespace and created_at.
        var where = new List<string>();

        if (filter.Namespaces is { Count: > 0 })
        {
            where.Add("d.namespace = ANY(@ns)");
        }

        if (filter.Kinds is { Count: > 0 })
        {
            where.Add("d.kind = ANY(@kinds)");
        }

        if (filter.WorkloadKey is { Length: > 0 })
        {
            where.Add("d.workload_key = @workload");
        }

        if (filter.From is not null)
        {
            where.Add("d.created_at >= @from");
        }

        if (filter.To is not null)
        {
            where.Add("d.created_at <= @to");
        }

        if (filter.ResolvedOnly)
        {
            where.Add("d.resolved");
        }

        if (where.Count > 0)
        {
            sql.Append("\nWHERE ").Append(string.Join(" AND ", where));
        }

        sql.Append("\nORDER BY f.score DESC, d.created_at DESC\nLIMIT @lim");

        return sql.ToString();
    }

    private static void AddFilterParameters(NpgsqlCommand cmd, SearchFilter filter)
    {
        if (filter.Namespaces is { Count: > 0 } namespaces)
        {
            cmd.Parameters.Add(new NpgsqlParameter("ns", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = namespaces.ToArray(),
            });
        }

        if (filter.Kinds is { Count: > 0 } kinds)
        {
            // The column holds enum names, not ordinals - see the enum convention in
            // HephaistoDbContext - so the filter is over strings here too.
            cmd.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = kinds.Select(k => k.ToString()).ToArray(),
            });
        }

        if (filter.WorkloadKey is { Length: > 0 } workload)
        {
            cmd.Parameters.AddWithValue("workload", workload);
        }

        if (filter.From is { } from)
        {
            cmd.Parameters.AddWithValue("from", from);
        }

        if (filter.To is { } to)
        {
            cmd.Parameters.AddWithValue("to", to);
        }
    }
}
