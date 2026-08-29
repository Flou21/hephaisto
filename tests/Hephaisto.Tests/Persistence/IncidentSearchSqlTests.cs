using Hephaisto.Agent.Persistence;

namespace Hephaisto.Tests.Persistence;

/// <summary>
/// The SQL the hybrid search composes, arm by arm.
/// </summary>
/// <remarks>
/// <para>
/// These are string assertions, which is normally a smell - but the thing being protected is
/// not the text, it is the <b>shape</b>. Two shapes here are load-bearing and silently
/// destroyed by an innocuous edit: the semantic arm's inner select must stay
/// <c>ORDER BY ... LIMIT</c> and nothing else, or the HNSW index stops being usable and the
/// query degrades to a sequential scan over every digest ever written; and the trigram arm must
/// use the word-similarity operators, because the whole-string ones score a five-character query
/// against a paragraph at near zero and match nothing.
/// </para>
/// <para>
/// Neither failure raises an error. Both just quietly return worse results, which is exactly how
/// the vector arm came to be dead for the whole life of the feature.
/// </para>
/// </remarks>
public class IncidentSearchSqlTests
{
    private static readonly SearchFilter NoFilter = new();

    [Fact]
    public void All_three_arms_appear_when_all_three_can_run()
    {
        var sql = IncidentSearch.BuildSql(lexical: true, semantic: true, trigram: true, NoFilter);

        sql.Should().Contain("lex AS (").And.Contain("sem AS (").And.Contain("trg AS (");
        sql.Should().Contain("f.lex_rank").And.Contain("f.sem_rank").And.Contain("f.trg_rank");
    }

    [Fact]
    public void An_arm_that_cannot_run_is_absent_rather_than_empty()
    {
        // The degradation that matters: no embedding provider must produce a query that still
        // runs, not one with an empty CTE or a reference to a table that was never defined.
        var sql = IncidentSearch.BuildSql(lexical: true, semantic: false, trigram: true, NoFilter);

        sql.Should().NotContain("sem AS (");
        sql.Should().NotContain("@qvec");
        sql.Should().Contain("NULL::bigint AS sem_rank");
    }

    [Fact]
    public void A_single_arm_still_produces_valid_looking_fusion()
    {
        var sql = IncidentSearch.BuildSql(lexical: false, semantic: true, trigram: false, NoFilter);

        sql.Should().Contain("sem AS (");
        sql.Should().NotContain("UNION ALL");
        sql.Should().Contain("GROUP BY id");
    }

    [Fact]
    public void Every_arm_contributes_one_union_branch()
    {
        var three = IncidentSearch.BuildSql(lexical: true, semantic: true, trigram: true, NoFilter);
        var two = IncidentSearch.BuildSql(lexical: true, semantic: false, trigram: true, NoFilter);

        Occurrences(three, "UNION ALL").Should().Be(2);
        Occurrences(two, "UNION ALL").Should().Be(1);
    }

    [Fact]
    public void The_semantic_arm_keeps_the_shape_the_hnsw_index_can_serve()
    {
        // ORDER BY <=> then LIMIT, with the ranking applied outside. Moving ROW_NUMBER inside
        // forces a sort over the whole table and the index is never used - no error, just a
        // search that gets slower and slower as the corpus grows.
        var sql = IncidentSearch.BuildSql(lexical: false, semantic: true, trigram: false, NoFilter);

        sql.Should().Contain("ORDER BY embedding <=> @qvec");
        sql.Should().MatchRegex(@"SELECT id\s+FROM incident_digests\s+WHERE embedding IS NOT NULL");
    }

    [Fact]
    public void The_trigram_arm_uses_word_similarity_with_the_query_on_the_left()
    {
        // word_similarity(a, b) looks for a inside b, so the query has to be the left operand.
        // Reversed, it asks how much of a paragraph-long digest appears inside a five-character
        // query, which is always about zero.
        var sql = IncidentSearch.BuildSql(lexical: false, semantic: false, trigram: true, NoFilter);

        sql.Should().Contain("@q <% d.digest");
        sql.Should().Contain("word_similarity(@q, d.digest)");

        // Not the whole-string operators, which is the mistake this replaces.
        sql.Should().NotContain("d.digest % @q");
    }

    [Fact]
    public void Filters_are_predicates_rather_than_null_checks()
    {
        // Building the predicate keeps the planner able to use the btree indexes; the
        // (@ns IS NULL OR ...) form defeats them.
        var sql = IncidentSearch.BuildSql(
            lexical: true,
            semantic: true,
            trigram: true,
            new SearchFilter { Namespaces = ["hephaisto-chaos"], ResolvedOnly = true });

        sql.Should().Contain("d.namespace = ANY(@ns)").And.Contain("d.resolved");
        sql.Should().NotContain("IS NULL OR");
    }

    [Fact]
    public void An_unfiltered_search_has_no_where_clause_on_the_outer_select()
    {
        IncidentSearch.BuildSql(lexical: true, semantic: true, trigram: true, NoFilter)
            .Should().NotContain("\nWHERE ");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;

        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
